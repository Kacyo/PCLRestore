using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Analyze
{
    private static readonly string[] Markers =
    {
        "下载已禁用", "为遵守相关法律法规", "不再提供", "非常抱歉", "下载功能",
        "下载功能相关入口", "整合包安装请求"
    };

    private static void Main(string[] args)
    {
        string path = args[0];
        string outPath = args.Length > 1 ? args[1] : "dump.txt";
        var asm = AssemblyDefinition.ReadAssembly(path);
        var module = asm.MainModule;
        using (var w = new StreamWriter(outPath, false, System.Text.Encoding.UTF8))
        {
            w.WriteLine("Assembly: " + asm.FullName);
            w.WriteLine("Types: " + module.Types.Count);
            w.WriteLine("EntryPoint: " + (module.EntryPoint == null ? "<none>" : module.EntryPoint.FullName));
            w.WriteLine("--- References ---");
            foreach (var r in module.AssemblyReferences) w.WriteLine(r.FullName);
            w.WriteLine("--- Resources (" + module.Resources.Count + ") ---");
            foreach (var r in module.Resources.Take(50)) w.WriteLine(r.Name + "  " + r.ResourceType);

            w.WriteLine("--- Types with Download/PageDownload ---");
            foreach (var t in AllTypes(module))
            {
                if (t.FullName.IndexOf("Download", StringComparison.OrdinalIgnoreCase) >= 0)
                    w.WriteLine(t.FullName);
            }

            w.WriteLine("--- Marker hits ---");
            foreach (var t in AllTypes(module))
            {
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    var hits = new List<string>();
                    foreach (var ins in m.Body.Instructions)
                    {
                        string s = ins.Operand as string;
                        if (ins.OpCode == OpCodes.Ldstr && s != null)
                        {
                            foreach (var mk in Markers)
                                if (s.Contains(mk)) { hits.Add(mk + " => " + s); break; }
                        }
                    }
                    if (hits.Count == 0) continue;
                    w.WriteLine("### " + m.FullName);
                    foreach (var h in hits) w.WriteLine("    " + h);
                }
            }
        }
        Console.WriteLine("done -> " + outPath);
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
