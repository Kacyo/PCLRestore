using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace PCLRestore
{
    /// <summary>
    /// 托管 payload：由 bootstrap32.dll 通过 ExecuteInDefaultAppDomain 在 PCL2 的默认
    /// AppDomain 中调用 Init，然后用 Harmony 给被阉割的下载相关方法打补丁。
    /// </summary>
    public static class Payload
    {
        private const BindingFlags FInst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags FStat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags FAny = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static string _logPath = Path.Combine(Path.GetTempPath(), "PCLRestore.log");
        private static readonly object _logLock = new object();
        private static Assembly _pcl;

        // ---------------------------------------------------------------- 入口

        public static int Init(string arg)
        {
            try
            {
                string dir = null;
                try { dir = Path.GetDirectoryName(typeof(Payload).Assembly.Location); }
                catch { }
                if (!string.IsNullOrEmpty(dir)) _logPath = Path.Combine(dir, "PCLRestore.log");
            }
            catch { }

            try { AppDomain.CurrentDomain.AssemblyResolve += OnResolve; }
            catch { }

            Log("=============== Init (" + arg + ") ===============");
            try { Work(); }
            catch (Exception ex) { Log("FATAL: " + ex); }
            return 0;
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs e)
        {
            try
            {
                string simple = new AssemblyName(e.Name).Name;
                if (!string.Equals(simple, "0Harmony", StringComparison.OrdinalIgnoreCase)) return null;

                // 优先从 payload 同目录按路径加载（字节数组加载会破坏 MonoMod 的泛型约束解析）
                try
                {
                    string dir = Path.GetDirectoryName(typeof(Payload).Assembly.Location);
                    string file = Path.Combine(dir, "0Harmony.dll");
                    if (File.Exists(file))
                    {
                        Log("resolve: 0Harmony from " + file);
                        return Assembly.LoadFrom(file);
                    }
                }
                catch (Exception ex) { Log("resolve file fail: " + ex.Message); }

                using (Stream s = typeof(Payload).Assembly.GetManifestResourceStream("0Harmony.dll"))
                {
                    if (s == null) { Log("resolve: embedded 0Harmony.dll missing"); return null; }
                    byte[] buf = new byte[s.Length];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = s.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    Log("resolve: 0Harmony from embedded resource");
                    return Assembly.Load(buf);
                }
            }
            catch (Exception ex)
            {
                Log("resolve fail: " + ex.Message);
                return null;
            }
        }

        // ---------------------------------------------------------------- 打补丁

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Work()
        {
            AppDomain domain = AppDomain.CurrentDomain;
            Log("AppDomain: " + domain.FriendlyName + " default=" + domain.IsDefaultAppDomain());

            for (int i = 0; i < 600 && _pcl == null; i++)
            {
                _pcl = Assembly.GetEntryAssembly();
                if (_pcl == null)
                {
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (a.GetName().Name == "Plain Craft Launcher 2") { _pcl = a; break; }
                    }
                }
                if (_pcl == null && i % 50 == 0)
                {
                    Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
                    StringBuilder sb = new StringBuilder("loaded(" + all.Length + "):");
                    for (int k = 0; k < all.Length && k < 15; k++) sb.Append(" ").Append(all[k].GetName().Name);
                    Log(sb.ToString());
                    Thread.Sleep(100);
                }
                else if (_pcl == null) Thread.Sleep(100);
            }

            if (_pcl == null) { Log("PCL2 程序集未找到，放弃"); return; }
            Log("PCL2 程序集: " + _pcl.FullName);

            Harmony harmony = new Harmony("pcl.restore.download");

            // 1. 版本列表加载完成回调：原实现被替换成了一律 throw，这里换掉缓存的完成委托
            HookVersionListCallback();

            // 2. 下载页左侧列表：恢复“安装”项、资源标签，并默认选中安装页
            Type left = _pcl.GetType("PCL.PageDownloadLeft", false);
            ConstructorInfo ctor = left == null ? null : left.GetConstructor(Type.EmptyTypes);
            PatchOne(harmony, "下载页左侧列表", ctor, null, "Postfix_LeftCtor");

            // 3. 启动页按钮：无版本时恢复“下载游戏”
            Type launchLeft = _pcl.GetType("PCL.PageLaunchLeft", false);
            MethodInfo m3 = launchLeft == null ? null : FindMethod(launchLeft, "RefreshButtonsUI", 0, FInst);
            PatchOne(harmony, "启动按钮状态", m3, null, "Postfix_RefreshButtonsUI");

            // 4. 标题栏“下载”标签
            Type setupUi = _pcl.GetType("PCL.PageSetupUI", false);
            MethodInfo m4 = setupUi == null ? null : FindMethod(setupUi, "HiddenRefresh", 0, FStat);
            PatchOne(harmony, "标题栏下载标签", m4, null, "Postfix_HiddenRefresh");

            // 5. 关于页：追加 PCL-Restore 标识
            Type about = _pcl.GetType("PCL.PageOtherAbout", false);
            MethodInfo m5 = about == null ? null : FindMethod(about, "PageOtherAbout_Loaded", 2, FInst);
            PatchOne(harmony, "关于页标识", m5, null, "Postfix_AboutLoaded");

            // 6. 版本下载页水印
            Type dlInstall = _pcl.GetType("PCL.PageDownloadInstall", false);
            MethodInfo m6 = dlInstall == null ? null : FindMethod(dlInstall, "InitializeComponent", 0, FInst);
            PatchOne(harmony, "下载页水印", m6, null, "Postfix_DownloadInstallInit");

            StartUiFixup();
        }

        private static void PatchOne(Harmony harmony, string title, MethodBase target,
                                     string prefix, string postfix)
        {
            if (target == null) { Log("[" + title + "] 目标方法未找到"); return; }
            try
            {
                HarmonyMethod pre = prefix == null ? null
                    : new HarmonyMethod(typeof(Payload).GetMethod(prefix, FStat));
                HarmonyMethod post = postfix == null ? null
                    : new HarmonyMethod(typeof(Payload).GetMethod(postfix, FStat));
                if (prefix != null && pre.method == null) { Log("[" + title + "] prefix 缺失"); return; }
                if (postfix != null && post.method == null) { Log("[" + title + "] postfix 缺失"); return; }

                harmony.Patch(target, pre, post);
                Log("[" + title + "] 已打补丁: " + target.DeclaringType.FullName + "::" + target.Name);
            }
            catch (Exception ex)
            {
                Log("[" + title + "] 打补丁失败: " + ex);
            }
        }

        // ---------------------------------------------------------------- 各补丁实现

        /// <summary>
        /// PageDownloadInstall.LoaderInit 把闭包里的静态字段 $IR3-3 当作“加载完成”委托缓存，
        /// 该委托指向的 _Lambda$__3-0 已经被改成一律 throw。这里在页面加载前把它换成自己的委托。
        /// </summary>
        private static void HookVersionListCallback()
        {
            try
            {
                Type closure = _pcl.GetType("PCL.PageDownloadInstall+_Closure$__", false);
                if (closure == null) { Log("[版本列表回调] 闭包类型未找到"); return; }

                FieldInfo field = closure.GetField("$IR3-3", FStat);
                if (field == null) { Log("[版本列表回调] $IR3-3 字段未找到"); return; }

                Type delType = field.FieldType;
                Type[] args = delType.GetGenericArguments();
                if (args.Length != 1) { Log("[版本列表回调] 委托签名不符: " + delType); return; }

                MethodInfo hook = typeof(Payload).GetMethod("OnVersionListLoaded", FStat);
                DynamicMethod dm = new DynamicMethod("PclRestoreVersionListHook", typeof(void),
                                                     new Type[] { args[0] }, typeof(Payload).Module, true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, hook);
                il.Emit(OpCodes.Ret);

                field.SetValue(null, dm.CreateDelegate(delType));
                Log("[版本列表回调] 完成委托已替换");
            }
            catch (Exception ex)
            {
                Log("[版本列表回调] 替换失败: " + ex);
            }
        }

        private static int _versionHookRunning;

        /// <summary>
        /// 被替换的加载逻辑：启动真正的静态版本列表加载器，等它结束后由 PCL 自己的
        /// OnFinish（_Lambda$__R3-4）调用 LoadMinecraft_OnFinish。
        /// </summary>
        private static void OnVersionListLoaded(object task)
        {
            if (Interlocked.Exchange(ref _versionHookRunning, 1) == 1) return;
            Log("[版本列表回调] 触发，开始加载版本列表");
            try
            {
                object loader = StaticMember(PclType("PCL.ModDownload"), "DlClientListLoader");
                int state = Convert.ToInt32(Member(loader, "State"));
                if (state == 0)
                {
                    Log("  启动 DlClientListLoader");
                    Invoke(loader, "Start", new object[] { null, false });
                }

                for (int i = 0; i < 600; i++)
                {
                    state = Convert.ToInt32(Member(loader, "State"));
                    if (state == 2) break;
                    if (state == 3 || state == 4) { Log("  加载失败，状态=" + state); break; }
                    Thread.Sleep(100);
                }

                Log("  加载结束，状态=" + state);

                object page = StaticMember(PclType("PCL.ModMain"), "FrmDownloadInstall");
                if (state == 2 && page != null)
                {
                    RunOnUi(delegate { Invoke(page, "LoadMinecraft_OnFinish", new object[0]); }, page);
                    Log("  已调用 LoadMinecraft_OnFinish");
                }

                Thread.Sleep(300);
                object versions = page == null ? null : Member(page, "MinecraftVersions");
                Log("  版本列表数量 = " + (versions == null ? "null" : Member(versions, "Count")));
            }
            catch (Exception ex)
            {
                Log("  版本列表加载失败: " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref _versionHookRunning, 0);
            }
        }

        private static void Postfix_LeftCtor(object __instance)
        {
            try { FixupLeftPage(__instance); }
            catch (Exception ex) { Log("[下载页左侧列表] 修复失败: " + ex); }
        }

        private static void FixupLeftPage(object page)
        {
            object itemInstall = Member(page, "_ItemInstall");
            object labResource = Member(page, "_LabResource");
            object itemMod = Member(page, "_ItemMod");

            if (itemInstall == null) { Log("[下载页左侧列表] _ItemInstall 为空"); return; }

            SetVisible(itemInstall, true);
            if (labResource != null) SetVisible(labResource, true);
            SetEnumField(page, "PageID", 0);

            if (itemMod != null) SetMember(itemMod, "Checked", false);
            SetMember(itemInstall, "Checked", true);
            Log("[下载页左侧列表] 已恢复安装项，PageID=0");
        }

        private static void Postfix_RefreshButtonsUI(object __instance)
        {
            try
            {
                object state = Member(__instance, "BtnLaunchState");
                if (state == null || Convert.ToInt32(state) != 1) return;

                object btn = Member(__instance, "BtnLaunch");
                object lab = Member(__instance, "LabVersion");
                object btnVersion = Member(__instance, "BtnVersion");
                object btnMore = Member(__instance, "BtnMore");
                if (btn == null) return;

                SetMember(btn, "Text", "下载游戏");
                SetMember(btn, "IsEnabled", true);
                if (lab != null) SetMember(lab, "Text", "未找到可用的游戏版本");
                if (btnVersion != null) SetMember(btnVersion, "IsEnabled", true);
                if (btnMore != null) SetVisible(btnMore, false);
                Log("[启动按钮状态] 已恢复为“下载游戏”");
            }
            catch (Exception ex)
            {
                Log("[启动按钮状态] 修复失败: " + ex);
            }
        }

        private static void Postfix_HiddenRefresh()
        {
            try
            {
                object frmMain = StaticMember(PclType("PCL.ModMain"), "FrmMain");
                if (frmMain == null) return;
                SetVisible(Member(frmMain, "BtnTitleSelect1"), true);
                SetVisible(Member(frmMain, "PanTitleSelect"), true);
                Log("[标题栏下载标签] 已强制显示");
            }
            catch (Exception ex)
            {
                Log("[标题栏下载标签] 修复失败: " + ex);
            }
        }


        private static void Postfix_AboutLoaded(object __instance)
        {
            try { AddAboutEntry(__instance); }
            catch (Exception ex) { Log("[关于页] 添加失败: " + ex.Message); }
        }

        private static void Postfix_DownloadInstallInit(object __instance)
        {
            try { AddDownloadWatermark(__instance); }
            catch (Exception ex) { Log("[水印] 添加失败: " + ex.Message); }
        }

        /// <summary>在关于页的 PanMain 里追加一条 PCLRestore 条目（复用 PCL 的列表项样式与图标）。</summary>
        private static void AddAboutEntry(object page)
        {
            object panel = Member(page, "PanMain");
            if (panel == null) { Log("[关于页] PanMain 为空"); return; }

            object children = Member(panel, "Children");
            System.Collections.IEnumerable seq = children as System.Collections.IEnumerable;
            if (seq == null) { Log("[关于页] Children 不可枚举"); return; }
            foreach (object child in seq)
            {
                if ("PclRestoreEntry".Equals(Member(child, "Name") as string)) return;
            }

            Type liType = PclType("PCL.MyListItem");
            if (liType == null) { Log("[关于页] 未找到 MyListItem"); return; }

            object pclItem = Member(page, "ItemAboutPcl");
            string logo = pclItem == null ? null : Member(pclItem, "Logo") as string;

            object item = Activator.CreateInstance(liType);
            SetMember(item, "Name", "PclRestoreEntry");
            SetMember(item, "Title", "PCLRestore");
            SetMember(item, "Info", "恢复PCL2的下载功能");
            if (!string.IsNullOrEmpty(logo)) SetMember(item, "Logo", logo);

            int index = -1, i = 0;
            foreach (object child in seq)
            {
                if (pclItem != null && ReferenceEquals(child, pclItem)) index = i;
                i++;
            }
            if (index >= 0) Invoke(children, "Insert", new object[] { index + 1, item });
            else Invoke(children, "Add", new object[] { item });
            Log("[关于页] 已添加 PCLRestore 条目");
        }

        /// <summary>在版本下载页右下角加水印。</summary>
        private static void AddDownloadWatermark(object page)
        {
            object host = Member(page, "Content");
            if (host != null) { AttachWatermark(host); return; }

            // 页面尚未完成布局（Content / Parent 都为空），后台等它就绪
            Thread t = new Thread(delegate ()
            {
                try
                {
                    for (int i = 0; i < 3000; i++)
                    {
                        Thread.Sleep(200);
                        object panBack = Member(page, "PanBack");
                        object parent = panBack == null ? null : Member(panBack, "Parent");
                        if (parent == null) continue;
                        RunOnUi(delegate { AttachWatermark(parent); }, page);
                        return;
                    }
                    Log("[水印] 等待页面布局超时");
                }
                catch (Exception ex) { Log("[水印] 等待失败: " + ex.Message); }
            });
            t.IsBackground = true;
            t.Start();
        }

        private static void AttachWatermark(object panel)
        {
            object children = Member(panel, "Children");
            System.Collections.IEnumerable seq = children as System.Collections.IEnumerable;
            if (seq == null) { Log("[水印] 容器不是 Panel"); return; }
            foreach (object child in seq)
            {
                if ("PclRestoreWatermark".Equals(Member(child, "Name") as string)) return;
            }

            object tb = CreateText("PCLRestore 提供服务", 11.0, 0.35);
            if (tb == null) { Log("[水印] 未找到 TextBlock 类型"); return; }
            SetMember(tb, "Name", "PclRestoreWatermark");
            SetMember(tb, "IsHitTestVisible", false);

            Type tbType = tb.GetType();
            PropertyInfo halign = tbType.GetProperty("HorizontalAlignment");
            if (halign != null) halign.SetValue(tb, Enum.ToObject(halign.PropertyType, 2), null);   // Right
            PropertyInfo valign = tbType.GetProperty("VerticalAlignment");
            if (valign != null) valign.SetValue(tb, Enum.ToObject(valign.PropertyType, 2), null);   // Bottom

            PropertyInfo margin = tbType.GetProperty("Margin");
            if (margin != null)
                margin.SetValue(tb, Activator.CreateInstance(margin.PropertyType,
                    new object[] { 0.0, 0.0, 12.0, 8.0 }), null);

            Invoke(children, "Add", new object[] { tb });
            Log("[水印] 已添加到下载页");
        }

        private static object CreateText(string text, double fontSize, double opacity)
        {
            Type tbType = FindTypeInLoadedAssembly("PresentationFramework", "System.Windows.Controls.TextBlock");
            if (tbType == null) return null;

            object tb = Activator.CreateInstance(tbType);
            SetMember(tb, "Text", text);
            SetMember(tb, "FontSize", fontSize);
            SetMember(tb, "Opacity", opacity);
            PropertyInfo align = tbType.GetProperty("HorizontalAlignment");
            if (align != null) align.SetValue(tb, Enum.ToObject(align.PropertyType, 0), null);   // Left
            return tb;
        }


        // ---------------------------------------------------------------- 延迟修复已创建的 UI

        private static void StartUiFixup()
        {
            Log("[UI 修复] 线程启动");
            Thread th = new Thread(delegate()
            {
                for (int i = 0; i < 240; i++)
                {
                    Thread.Sleep(250);
                    try
                    {
                        if (TryUiFixup()) { FixupLeftPageIfExists(); NotifyInjected(); return; }
                        if (i == 0 || i % 40 == 0) Log("[UI 修复] 等待主窗口... i=" + i);
                    }
                    catch (Exception ex) { Log("[UI 修复] " + ex.Message); }
                }
                Log("[UI 修复] 超时");
            });
            th.IsBackground = true;
            th.Start();
        }

        private static bool _mainWindowLogged;

        /// <summary>等 PCL2 启动加载结束后，用 PCL 的公告弹窗（MyMsgBox，更新日志同款）提示注入成功。</summary>
        private static void NotifyInjected()
        {
            try
            {
                Thread.Sleep(2500);
                object frmMain = StaticMember(PclType("PCL.ModMain"), "FrmMain");
                if (frmMain == null) frmMain = GetMainWindow();

                Log("[公告] 正在显示注入成功公告");
                RunOnUi(delegate
                {
                    Type modMain = PclType("PCL.ModMain");
                    MethodInfo box = FindMethod(modMain, "MyMsgBox", 11, FStat);
                    if (box == null) { Log("[公告] 未找到 MyMsgBox 方法"); return; }

                    string content =
                        "已通过 DLL 注入恢复被移除的 MC 下载功能。\r\n\r\n" +
                        "· 下载页入口与版本列表已恢复\r\n" +
                        "· 模组 / 资源包 / 整合包下载已恢复\r\n\r\n" +
                        "PCLRestore";

                    box.Invoke(null, new object[]
                    {
                        content,
                        "加载成功",
                        "确定",
                        "",
                        "",
                        false, true, false,
                        null, null, null
                    });
                }, frmMain);

                Log("[公告] MyMsgBox 调用已返回");
            }
            catch (Exception ex)
            {
                Log("[公告] 显示失败: " + ex.Message);
            }
        }

        private static object GetMainWindow()
        {
            Type appType = FindTypeInLoadedAssembly("PresentationFramework", "System.Windows.Application");
            if (appType == null) return null;
            PropertyInfo current = appType.GetProperty("Current", FStat);
            object app = current == null ? null : current.GetValue(null, null);
            return app == null ? null : Member(app, "MainWindow");
        }

        private static bool TryUiFixup()
        {
            object frmMain = StaticMember(PclType("PCL.ModMain"), "FrmMain");
            if (frmMain == null) frmMain = GetMainWindow();
            if (frmMain == null) return false;
            if (!_mainWindowLogged) { _mainWindowLogged = true; Log("[UI 修复] 主窗口类型: " + frmMain.GetType().FullName); }

            bool ok = RunOnUi(delegate
            {
                SetVisible(Member(frmMain, "BtnTitleSelect1"), true);
                SetVisible(Member(frmMain, "PanTitleSelect"), true);

                object launchLeft = StaticMember(PclType("PCL.ModMain"), "FrmLaunchLeft");
                if (launchLeft != null) Invoke(launchLeft, "RefreshButtonsUI", new object[0]);
            }, frmMain);
            if (ok) Log("[UI 修复] 标题栏与启动按钮已刷新");

            AutoTestIfRequested(frmMain);
            return ok;
        }

        /// <summary>仅用于自动化验证：设置 PCLRESTORE_AUTOTEST=1 时自动打开下载页。</summary>
        private static void AutoTestIfRequested(object frmMain)
        {
            if (Environment.GetEnvironmentVariable("PCLRESTORE_AUTOTEST") != "1") return;
            if (_autoTested) return;
            _autoTested = true;

            try
            {
                RunOnUi(delegate
                {
                    Type pageType = PclType("PCL.FormMain+PageType");
                    Type stackType = PclType("PCL.FormMain+PageStackData");
                    Type subType = PclType("PCL.FormMain+PageSubType");
                    object download = Enum.ToObject(pageType, 1);
                    object stack = ToPageStackData(stackType, pageType, download);
                    Invoke(frmMain, "PageChange", new object[] { stack, Enum.ToObject(subType, 0) });
                    Log("[AUTOTEST] 已切换到下载页");
                }, frmMain);

                // 稍后再切到关于页，验证 PCL-Restore 条目
                Thread t = new Thread(delegate ()
                {
                    try
                    {
                        Thread.Sleep(5000);
                        RunOnUi(delegate
                        {
                            Type pageType = PclType("PCL.FormMain+PageType");
                            Type stackType = PclType("PCL.FormMain+PageStackData");
                            Type subType = PclType("PCL.FormMain+PageSubType");
                            object other = Enum.ToObject(pageType, 4);
                            object stack = ToPageStackData(stackType, pageType, other);
                            Invoke(frmMain, "PageChange", new object[] { stack, Enum.ToObject(subType, 1) });
                            Log("[AUTOTEST] 已切换到关于页");
                        }, frmMain);
                    }
                    catch (Exception ex) { Log("[AUTOTEST] 切换关于页失败: " + ex.Message); }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception ex)
            {
                Log("[AUTOTEST] 切换下载页失败: " + ex);
            }
        }

        private static bool _autoTested;

        private static object ToPageStackData(Type stackType, Type pageType, object pageValue)
        {
            MethodInfo op = stackType.GetMethod("op_Implicit", FStat, null, new Type[] { pageType }, null);
            if (op != null) return op.Invoke(null, new object[] { pageValue });

            foreach (ConstructorInfo c in stackType.GetConstructors(FAny))
            {
                ParameterInfo[] ps = c.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == pageType)
                    return c.Invoke(new object[] { pageValue });
            }

            object stack = Activator.CreateInstance(stackType, true);
            SetMember(stack, "Page", pageValue);
            SetMember(stack, "Additional", null);
            return stack;
        }

        private static void FixupLeftPageIfExists()
        {
            object left = StaticMember(PclType("PCL.ModMain"), "FrmDownloadLeft");
            if (left == null) Log("[UI 修复] FrmDownloadLeft 尚未创建");
            else RunOnUi(delegate { FixupLeftPage(left); }, left);

            object install = StaticMember(PclType("PCL.ModMain"), "FrmDownloadInstall");
            if (install != null) RunOnUi(delegate { AddDownloadWatermark(install); }, install);
        }

        private static bool _uiDiagLogged;

        private static bool RunOnUi(Action action, object uiObject)
        {
            object dispatcher = uiObject == null ? null : Member(uiObject, "Dispatcher");
            if (dispatcher == null)
            {
                Type appType = FindTypeInLoadedAssembly("PresentationFramework", "System.Windows.Application");
                if (appType == null) { LogUiDiag("Application 类型未找到"); return false; }

                PropertyInfo current = appType.GetProperty("Current", FStat);
                if (current == null) { LogUiDiag("Current property null"); return false; }
                object app = current.GetValue(null, null);
                if (app == null) { LogUiDiag("Application.Current null"); return false; }
                dispatcher = Member(app, "Dispatcher");
            }
            if (dispatcher == null) { LogUiDiag("Dispatcher null"); return false; }

            Type dt = dispatcher.GetType();
            MethodInfo check = dt.GetMethod("CheckAccess", Type.EmptyTypes);
            bool onUi = check != null && (bool)check.Invoke(dispatcher, null);
            if (onUi) { action(); return true; }

            MethodInfo invoke = dt.GetMethod("Invoke", new Type[] { typeof(Delegate), typeof(object[]) });
            if (invoke == null) { LogUiDiag("Dispatcher.Invoke null"); return false; }
            invoke.Invoke(dispatcher, new object[] { action, new object[0] });
            return true;
        }

        private static Type FindTypeInLoadedAssembly(string assemblyName, string typeName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != assemblyName) continue;
                Type t = a.GetType(typeName, false);
                if (t != null) return t;
            }
            return null;
        }

        private static void LogUiDiag(string msg)
        {
            if (_uiDiagLogged) return;
            _uiDiagLogged = true;
            Log("[UI 修复] " + msg);
        }

        // ---------------------------------------------------------------- 反射小工具

        private static Type PclType(string fullName)
        {
            Type t = _pcl.GetType(fullName, false);
            if (t == null) throw new Exception("类型未找到: " + fullName);
            return t;
        }

        private static FieldInfo FindField(Type t, string name)
        {
            while (t != null)
            {
                FieldInfo f = t.GetField(name, FAny | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        private static PropertyInfo FindProp(Type t, string name)
        {
            while (t != null)
            {
                PropertyInfo p = t.GetProperty(name, FAny | BindingFlags.DeclaredOnly);
                if (p != null) return p;
                t = t.BaseType;
            }
            return null;
        }

        private static MethodInfo FindMethod(Type t, string name, int paramCount, BindingFlags flags)
        {
            foreach (MethodInfo m in t.GetMethods(flags))
            {
                if (m.Name == name && m.GetParameters().Length == paramCount) return m;
            }
            return null;
        }

        private static object Member(object obj, string name)
        {
            if (obj == null) return null;
            FieldInfo f = FindField(obj.GetType(), name);
            if (f != null) return f.GetValue(obj);
            PropertyInfo p = FindProp(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            return null;
        }

        private static object StaticMember(Type t, string name)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                FieldInfo f = cur.GetField(name, FStat | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(null);
                PropertyInfo p = cur.GetProperty(name, FStat | BindingFlags.DeclaredOnly);
                if (p != null) return p.GetValue(null, null);
            }
            return null;
        }

        private static void SetMember(object obj, string name, object value)
        {
            if (obj == null) throw new Exception("对象为空: " + name);
            FieldInfo f = FindField(obj.GetType(), name);
            if (f != null) { f.SetValue(obj, value); return; }
            PropertyInfo p = FindProp(obj.GetType(), name);
            if (p != null) { p.SetValue(obj, value, null); return; }
            throw new Exception("成员未找到: " + obj.GetType().FullName + "." + name);
        }

        private static void SetEnumField(object obj, string name, int value)
        {
            FieldInfo f = FindField(obj.GetType(), name);
            if (f == null) throw new Exception("字段未找到: " + name);
            f.SetValue(obj, Enum.ToObject(f.FieldType, value));
        }

        private static void SetVisible(object uiElement, bool visible)
        {
            if (uiElement == null) return;
            PropertyInfo p = FindProp(uiElement.GetType(), "Visibility");
            if (p == null) return;
            p.SetValue(uiElement, Enum.ToObject(p.PropertyType, visible ? 0 : 2), null);
        }

        private static object Invoke(object obj, string name, object[] args)
        {
            MethodInfo m = FindMethod(obj.GetType(), name, args.Length, FInst);
            if (m == null) throw new Exception("方法未找到: " + name);
            return m.Invoke(obj, args);
        }

        private static object InvokeStatic(Type t, string name, object[] args)
        {
            MethodInfo m = FindMethod(t, name, args.Length, FStat);
            if (m == null) throw new Exception("方法未找到: " + t.Name + "." + name);
            return m.Invoke(null, args);
        }

        // ---------------------------------------------------------------- 日志

        private static void Log(string msg)
        {
            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n";
                lock (_logLock)
                {
                    File.AppendAllText(_logPath, line, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
