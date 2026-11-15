using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Dump
{
    private static void Main(string[] args)
    {
        string path = args[0];
        string mode = args[1];
        var asm = AssemblyDefinition.ReadAssembly(path);
        var module = asm.MainModule;
        if (mode == "types")
        {
            foreach (var t in AllTypes(module))
            {
                Console.WriteLine(t.FullName + "   methods=" + t.Methods.Count + " fields=" + t.Fields.Count);
            }
        }
        else if (mode == "il")
        {
            string pattern = args[2];
            foreach (var t in AllTypes(module))
                foreach (var m in t.Methods)
                    if (m.FullName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        DumpMethod(m);
        }
        else if (mode == "attrs")
        {
            string pattern = args[2];
            foreach (var t in AllTypes(module))
                foreach (var m in t.Methods)
                    if (m.FullName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        Console.WriteLine(m.FullName + "  [" + m.Attributes + "]  static=" + m.IsStatic);
        }
        else if (mode == "fields")
        {
            string pattern = args[2];
            foreach (var t in AllTypes(module))
            {
                if (t.FullName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Console.WriteLine("== " + t.FullName);
                foreach (var f in t.Fields)
                    Console.WriteLine("   " + f.FieldType.FullName + " " + f.Name + "  const=" + f.HasConstant + (f.HasConstant ? " " + f.Constant : ""));
                foreach (var p in t.Properties) Console.WriteLine("   prop " + p.PropertyType.FullName + " " + p.Name);
            }
        }
        else if (mode == "xref")
        {
            string pattern = args[2];
            foreach (var t in AllTypes(module))
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        string text = null;
                        var mr = ins.Operand as MemberReference;
                        var tr = ins.Operand as TypeReference;
                        if (mr != null) text = mr.FullName;
                        else if (tr != null) text = tr.FullName;
                        if (text != null && text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Console.WriteLine(m.FullName + "  ->  " + text);
                            break;
                        }
                    }
                }
        }
        else if (mode == "strs")
        {
            string pattern = args[2];
            foreach (var t in AllTypes(module))
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        var s = ins.Operand as string;
                        if (ins.OpCode == OpCodes.Ldstr && s != null && s.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Console.WriteLine(m.FullName + "  ::  " + s);
                            break;
                        }
                    }
                }
        }
    }

    private static void DumpMethod(MethodDefinition m)
    {
        Console.WriteLine("==== " + m.FullName);
        if (!m.HasBody) { Console.WriteLine("   <no body>"); return; }
        foreach (var v in m.Body.Variables) Console.WriteLine("   .local " + v.VariableType.FullName + " V" + v.Index);
        foreach (var ins in m.Body.Instructions)
        {
            string operand = "";
            var mr = ins.Operand as MethodReference;
            var fr = ins.Operand as FieldReference;
            var tr = ins.Operand as TypeReference;
            if (ins.Operand is string) operand = "\"" + ins.Operand + "\"";
            else if (mr != null) operand = mr.FullName;
            else if (fr != null) operand = fr.FullName;
            else if (tr != null) operand = tr.FullName;
            else if (ins.Operand != null) operand = ins.Operand.ToString();
            Console.WriteLine("   IL_" + ins.Offset.ToString("X4") + ": " + ins.OpCode.Name + " " + operand);
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        var stack = new Stack<TypeDefinition>(module.Types);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            yield return t;
            foreach (var n in t.NestedTypes) stack.Push(n);
        }
    }
}
