// OpenMIFS — 同方 MIFS (MiInterface) 控制台 · 托盘版
// =====================================================================
// 目标框架 : .NET Framework 4.8（Windows 10/11 系统自带，无需安装运行时）
// 编译方式 : 见 build/build.ps1（用系统自带 csc.exe，产物为单个 exe）
// 语法约束 : Framework 自带 csc 仅支持 C# 5，故不使用字符串插值 / ?. / nameof
//
// 接口说明见 docs/PROTOCOL.md
//   ACPI 设备 : ACPI\PNP0C14\MIFS
//   WMI 类    : root\wmi:MICommonInterface
//   方法      : MiInterface(InData[32]) -> OutData[30]
//   ACPI GUID : {B60BFB48-3E5B-49E4-A0E9-8CFFE1B3434B}
//
// 数据目录  : %LOCALAPPDATA%\OpenMIFS\
//   openmifs.log       诊断日志（每次动作一行，超过 1 MB 自动轮转）
//   capabilities.txt   能力探测缓存（哪些功能号本机真的可用）
//   osd-diagnose.txt   OSD 诊断输出

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Management;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// ─────────────────────────────────────────────────────── 程序集版本信息
[assembly: AssemblyTitle("OpenMIFS")]
[assembly: AssemblyDescription("同方 MIFS (MiInterface) 控制台 — 不依赖官方控制中心")]
[assembly: AssemblyProduct("OpenMIFS")]
[assembly: AssemblyCompany("OpenMIFS contributors")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("0.6.1.0")]
[assembly: AssemblyFileVersion("0.6.1.0")]

namespace OpenMIFS
{
    // ──────────────────────────────────────────────────────────────── 日志
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static readonly UTF8Encoding Enc = new UTF8Encoding(false);
        private const long MaxBytes = 1024 * 1024;

        private static string _folder;
        private static string _file;
        private static bool _disabled;
        private static bool _inited;

        public static string Folder
        {
            get
            {
                if (_folder == null)
                {
                    string b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (b == null || b.Length == 0) b = Path.GetTempPath();
                    _folder = Path.Combine(b, "OpenMIFS");
                }
                return _folder;
            }
        }

        public static string FilePath
        {
            get
            {
                if (_file == null) _file = Path.Combine(Folder, "openmifs.log");
                return _file;
            }
        }

        public static bool Available { get { return !_disabled; } }

        private static void Ensure()
        {
            if (_inited || _disabled) return;
            _inited = true;
            try
            {
                if (!Directory.Exists(Folder)) Directory.CreateDirectory(Folder);
                // 首次创建时写入 UTF-8 BOM：否则 Windows PowerShell 5.1 / 记事本会把中文读成乱码。
                // 之后追加都用无 BOM 编码，避免每行都插一个 BOM。
                if (!File.Exists(FilePath)) File.WriteAllText(FilePath, "", new UTF8Encoding(true));
                FileInfo fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string old = FilePath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(FilePath, old);
                    File.AppendAllText(FilePath, Stamp("INFO ") + "日志超过 1 MB，已轮转为 openmifs.log.1" + Environment.NewLine, Enc);
                }
            }
            catch { _disabled = true; }
        }

        private static string Stamp(string level)
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                 + " [" + level + "] pid=" + Process.GetCurrentProcess().Id
                 + " tid=" + Thread.CurrentThread.ManagedThreadId + " | ";
        }

        public static void Write(string level, string msg)
        {
            if (_disabled) return;
            try
            {
                lock (Sync)
                {
                    Ensure();
                    if (_disabled) return;
                    File.AppendAllText(FilePath, Stamp(level) + msg + Environment.NewLine, Enc);
                }
            }
            catch { _disabled = true; }
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        public static void Ex(string context, Exception ex)
        {
            if (ex == null) { Write("ERROR", context + "：未知异常"); return; }
            string detail = ex.GetType().Name + ": " + ex.Message;
            try
            {
                if (ex.StackTrace != null)
                {
                    string[] lines = ex.StackTrace.Split('\n');
                    if (lines.Length > 0) detail += " @ " + lines[0].Trim();
                }
            }
            catch { }
            if (ex.InnerException != null)
                detail += "  ← " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
            Write("ERROR", context + "：" + detail);
        }
    }

    // ─────────────────────────────────────────────────────── 能力探测缓存
    internal static class Caps
    {
        // null = 尚未检测；true/false = 实测结论
        public static bool? FanBoost;
        // 探测时的供电类型（见 Mifs.AcTypeName）：上游驱动文档说明
        // 「性能/满速模式在电池与 Type-C(PD) 供电下被硬件禁用」，
        // 所以换了电源就必须重测，不能拿上次的结论跨电源状态复用。
        public static int FanBoostAcType = -1;

        private static string PathName { get { return Path.Combine(Log.Folder, "capabilities.txt"); } }

        public static void Load()
        {
            try
            {
                if (!File.Exists(PathName)) return;
                string[] lines = File.ReadAllLines(PathName);
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = lines[i].Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = s.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = s.Substring(eq + 1).Trim().ToLowerInvariant();
                    if (k == "fanboost")
                    {
                        if (v == "supported") FanBoost = true;
                        else if (v == "unsupported") FanBoost = false;
                        else FanBoost = null;
                    }
                    else if (k == "fanboost_actype")
                    {
                        int n;
                        if (int.TryParse(v, out n)) FanBoostAcType = n;
                    }
                }
                Log.Info("加载能力缓存：" + PathName + " → 风扇满速=" + FanBoostText()
                    + "（测试时供电=" + Mifs.AcTypeName(FanBoostAcType) + "）");
            }
            catch (Exception ex) { Log.Ex("读取能力缓存失败", ex); }
        }

        public static void Save()
        {
            try
            {
                if (!Directory.Exists(Log.Folder)) Directory.CreateDirectory(Log.Folder);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# OpenMIFS 能力探测缓存（程序自动生成，删掉即视为未检测）");
                sb.AppendLine("# fanboost_actype：探测时的供电类型（0 电池 / 1 Type-C / 2 圆口 DC）");
                sb.AppendLine("fanboost=" + FanBoostText());
                sb.AppendLine("fanboost_actype=" + FanBoostAcType.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(PathName, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Ex("写入能力缓存失败", ex); }
        }

        /// <summary>缓存结论只对"当时的供电类型"有效。</summary>
        public static bool ValidFor(int acType)
        {
            return FanBoost.HasValue && FanBoostAcType == acType;
        }

        private static string FanBoostText()
        {
            if (!FanBoost.HasValue) return "unknown";
            return FanBoost.Value ? "supported" : "unsupported";
        }

        public static void Reset() { FanBoost = null; Save(); Log.Info("能力缓存已重置，下次点击将重新探测"); }
    }

    // ─────────────────────────────────────────────────────────── 用户设置
    // 存 %LOCALAPPDATA%\OpenMIFS\settings.txt，key=value，删掉即恢复默认。
    internal static class Settings
    {
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>();
        private static bool _loaded;

        private static string PathName { get { return Path.Combine(Log.Folder, "settings.txt"); } }

        public static string Get(string key, string def)
        {
            Load();
            string v;
            return Map.TryGetValue(key, out v) ? v : def;
        }

        public static void Set(string key, string value)
        {
            Load();
            Map[key] = value;
            try
            {
                if (!Directory.Exists(Log.Folder)) Directory.CreateDirectory(Log.Folder);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# OpenMIFS 设置（key=value，删掉即恢复默认）");
                foreach (KeyValuePair<string, string> kv in Map) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(PathName, sb.ToString(), new UTF8Encoding(false));
                Log.Info("设置已保存：" + key + "=" + value + " → " + PathName);
            }
            catch (Exception ex) { Log.Ex("写入设置失败", ex); }
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(PathName)) return;
                string[] lines = File.ReadAllLines(PathName);
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = lines[i].Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    Map[s.Substring(0, eq).Trim()] = s.Substring(eq + 1).Trim();
                }
                Log.Info("已加载设置：" + PathName + "（" + Map.Count.ToString(CultureInfo.InvariantCulture) + " 项）");
            }
            catch (Exception ex) { Log.Ex("读取设置失败", ex); }
        }
    }

    // ──────────────────────────────────────────────────────────── MIFS 调用层
    internal static class Mifs
    {
        private const string WmiNamespace = "root\\wmi";
        private const string WmiClass     = "MICommonInterface";
        private const string WmiMethod    = "MiInterface";
        private const byte   CmdGet       = 250;
        private const byte   CmdSet       = 251;

        public const int FnPerMode       = 8;
        public const int FnGpuMode       = 9;
        public const int FnKbdType       = 10;
        public const int FnFnLock        = 11;
        public const int FnTpLock        = 12;
        public const int FnFanSpeeds     = 13;
        public const int FnRgbMode       = 16;
        public const int FnRgbColor      = 17;
        public const int FnRgbBright     = 18;
        public const int FnAcType        = 19;
        public const int FnMaxFanSwitch  = 20;
        public const int FnMaxFanSpeed   = 21;
        public const int FnCpuTemp       = 22;
        public const int FnCpuPower      = 23;

        /// <summary>供电类型（功能号 19）。语义来自上游 tongfang-mifs-wmi 驱动文档：
        /// 1 = Type-C(PD)，2 = 圆口 DC。本项目早期写成"1 外接电源"是不准确的。</summary>
        public static string AcTypeName(int v)
        {
            switch (v)
            {
                case 0: return "电池供电";
                case 1: return "Type-C 供电";
                case 2: return "圆口 DC 供电";
                case -1: return "未知";
            }
            return "原始值 " + v.ToString(CultureInfo.InvariantCulture);
        }

        private static ManagementObject _instance;
        private static string _lastError = "";

        public static string LastError { get { return _lastError; } }

        private static ManagementObject GetInstance()
        {
            if (_instance != null) return _instance;
            ManagementObjectSearcher searcher =
                new ManagementObjectSearcher(WmiNamespace, "SELECT * FROM " + WmiClass);
            foreach (ManagementBaseObject o in searcher.Get())
            {
                _instance = (ManagementObject)o;
                break;
            }
            if (_instance == null)
                throw new InvalidOperationException("未找到 " + WmiClass + " 实例，本机 BIOS 可能没有暴露 MIFS 接口");
            return _instance;
        }

        public static bool Available
        {
            get
            {
                try { GetInstance(); return true; }
                catch (Exception ex) { _lastError = ex.Message; return false; }
            }
        }

        public static byte[] Call(byte command, int function, byte[] payload)
        {
            ManagementObject inst = GetInstance();
            byte[] inData = new byte[32];
            inData[1] = command;
            inData[3] = (byte)function;
            if (payload != null)
            {
                int n = Math.Min(payload.Length, 28);
                Array.Copy(payload, 0, inData, 4, n);
            }
            ManagementBaseObject inParams = inst.GetMethodParameters(WmiMethod);
            inParams["InData"] = inData;
            ManagementBaseObject outParams = inst.InvokeMethod(WmiMethod, inParams, null);
            byte[] outData = (byte[])outParams["OutData"];
            if (outData == null) throw new InvalidOperationException("方法返回空数据（OutData 为 null）");
            return outData;
        }

        /// <summary>读取单字节数据；调用失败返回 null。</summary>
        public static int? GetByte(int function)
        {
            try { return Call(CmdGet, function, null)[4]; }
            catch (Exception ex) { _lastError = ex.Message; return null; }
        }

        public static void SetByte(int function, byte value)
        {
            Call(CmdSet, function, new byte[] { value });
        }

        /// <summary>风扇满速开关（参数 [4]=风扇组 0，[5]=0 正常 / 1 满速）。</summary>
        public static void SetFanBoost(byte value)
        {
            Call(CmdSet, FnMaxFanSwitch, new byte[] { 0, value });
        }

        /// <summary>在 ms 毫秒内轮询风扇转速，返回采样到的最大值。
        /// 用途：EC 可能不更新寄存器镜像却照做，所以"到底转没转起来"只能看转速。</summary>
        public static int FanPeak(int ms, int fallback)
        {
            int peak = fallback;
            int spent = 0;
            while (spent < ms)
            {
                int[] f = GetFans();
                if (f != null && f[0] > peak) peak = f[0];
                Thread.Sleep(400);
                System.Windows.Forms.Application.DoEvents();
                spent += 400;
            }
            return peak;
        }

        /// <summary>读取三个风扇转速（RPM），失败返回 null。</summary>
        public static int[] GetFans()
        {
            try
            {
                byte[] r = Call(CmdGet, FnFanSpeeds, null);
                return new int[]
                {
                    r[4] + (r[5] << 8),
                    r[6] + (r[7] << 8),
                    r[10] + (r[11] << 8)
                };
            }
            catch (Exception ex) { _lastError = ex.Message; return null; }
        }
    }

    // ──────────────────────────────────────────────────────── 性能模式映射表
    internal static class ModeMap
    {
        // 上游 Linux 驱动 tongfang-mifs-wmi 标注：0=均衡 1=性能 2=低功耗
        // 本机（无界 14 Pro 2023 / R7-7840HS）实测相反：0=性能 1=均衡 2=低功耗
        // 换机型若发现对不上，跑 .\src\mifs.ps1 bench 实测后改这里。
        public static readonly string[] Order = new string[] { "低功耗", "均衡", "性能" };

        public static int Value(string name)
        {
            switch (name)
            {
                case "低功耗": return 2;
                case "均衡":   return 1;
                case "性能":   return 0;
            }
            return -1;
        }

        public static string Label(int v)
        {
            switch (v)
            {
                case 0: return "性能";
                case 1: return "均衡";
                case 2: return "低功耗";
                case 3: return "满速";
            }
            return "未知(" + v.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }

    // ────────────────────────────────────────────────────── 外部命令执行助手
    internal static class Proc
    {
        public static int Run(string exe, string args, out string output, int timeoutMs)
        {
            output = "";
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            try { psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { }
            try { psi.StandardErrorEncoding = psi.StandardOutputEncoding; }
            catch { }
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                try { p.Start(); }
                catch (Exception ex) { output = ex.Message; return -1; }
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    output = "命令超时（" + timeoutMs.ToString(CultureInfo.InvariantCulture) + " ms）";
                    return -2;
                }
                output = ((so + se).Trim());
                return p.ExitCode;
            }
        }
    }

    // ──────────────────────────────────────────────────────── 开机自启（计划任务）
    internal static class Startup
    {
        public const string TaskName = "OpenMIFS";

        /// <summary>开机自启必须带的参数：静默进托盘，不弹主界面。
        /// 早期版本创建的任务没带这个参数，登录时会弹出窗口 —— NeedsRepair() 会检出并重建。</summary>
        public const string TrayArgument = "--tray";

        public static bool IsEnabled()
        {
            string o;
            int code = Proc.Run("schtasks.exe", "/Query /TN \"" + TaskName + "\"", out o, 15000);
            return code == 0;
        }

        /// <summary>读任务的命令行（用 /XML，元素名与系统语言无关）。</summary>
        public static string TaskCommand()
        {
            string o;
            int code = Proc.Run("schtasks.exe", "/Query /TN \"" + TaskName + "\" /XML", out o, 15000);
            return code == 0 ? o : "";
        }

        /// <summary>任务存在但命令行不含 --tray（老版本创建的）→ 需要重建。</summary>
        public static bool NeedsRepair()
        {
            if (!IsEnabled()) return false;
            string xml = TaskCommand();
            if (xml.Length == 0) return false;   // 读不到就不动它
            return xml.IndexOf(TrayArgument, StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>创建/删除开机自启任务。用计划任务而不是 Run 注册表项，
        /// 因为本程序声明了 requireAdministrator —— 注册表 Run 会在每次登录时弹 UAC，
        /// 而 /RL HIGHEST 的计划任务登录时静默以最高权限启动。
        /// 命令行里带 --tray：登录启动时只进托盘，不弹窗口。</summary>
        public static bool Set(bool enable)
        {
            string exe = Application.ExecutablePath;
            string o;
            if (enable)
            {
                string args = "/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe + "\\\" " + TrayArgument + "\""
                            + " /SC ONLOGON /RL HIGHEST /F /DELAY 0000:10";
                int code = Proc.Run("schtasks.exe", args, out o, 30000);
                Log.Info("开机自启：创建计划任务（exit=" + code.ToString(CultureInfo.InvariantCulture) + "）" + o.Replace("\r\n", " / "));
                if (code != 0) { Log.Warn("创建计划任务失败：" + o); return false; }
                return IsEnabled();
            }
            else
            {
                int code = Proc.Run("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F", out o, 30000);
                Log.Info("开机自启：删除计划任务（exit=" + code.ToString(CultureInfo.InvariantCulture) + "）" + o.Replace("\r\n", " / "));
                return !IsEnabled();
            }
        }

        /// <summary>老任务（不带 --tray）自动重建，避免登录时弹窗。启动时调用，幂等。</summary>
        public static void RepairIfNeeded()
        {
            try
            {
                if (!NeedsRepair()) return;
                Log.Warn("开机自启：现有计划任务不带 " + TrayArgument + "（老版本创建，登录会弹窗）→ 自动重建");
                Set(true);
                Log.Info("开机自启：重建完成，新命令行含 " + TrayArgument);
            }
            catch (Exception ex) { Log.Ex("修复开机自启任务失败", ex); }
        }
    }

    // ───────────────────────────────────────────────────────────── OSD 模块
    internal static class Osd
    {
        public const string ServiceName = "BLDHotKeyService";
        public const string UtilityExe  = "BLDFnHotkeyUtility.exe";
        public const string UtilityName = "BLDFnHotkeyUtility";
        public const string InstallDir  = @"C:\Program Files\OSD";

        // ── 官方 OSD 的悬浮窗探测 ────────────────────────────────────────
        // BLDFnHotkeyUtility.exe 的提示窗是一个小的分层工具窗，
        // 标题固定为 FloatingNativeWindow，显示时带 WS_VISIBLE。
        // 用它判断"官方 OSD 此刻正在画" → 自带的提示让位，避免两条提示重叠。
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private static bool _foundVisible;

        /// <summary>官方 OSD 的提示窗此刻是否可见（= 它正在显示提示）。</summary>
        public static bool FloatingWindowVisible()
        {
            _foundVisible = false;
            try { EnumWindows(new EnumWindowsProc(EnumProc), IntPtr.Zero); }
            catch (Exception ex) { Log.Ex("枚举 OSD 悬浮窗失败", ex); }
            return _foundVisible;
        }

        private static bool EnumProc(IntPtr hWnd, IntPtr lParam)
        {
            if (_foundVisible) return false;                 // 已找到，停止枚举
            if (!IsWindowVisible(hWnd)) return true;
            StringBuilder sb = new StringBuilder(128);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            if (sb.ToString() != "FloatingNativeWindow") return true;
            RECT r;
            if (!GetWindowRect(hWnd, out r)) return true;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0 || w > 800 || h > 800) return true;   // 尺寸也要对得上
            _foundVisible = true;
            return false;
        }

        public sealed class State
        {
            public string ServiceState = "查询失败";
            public string ServiceStart = "";
            public string ServicePath = "";
            public int ServicePid;
            public bool ServiceFound;
            public int UtilityCount;
            public string UtilityPids = "";
            public string UtilitySince = "";
            public string UtilityMemThreads = "";
            public string ServiceSince = "";
            public string Error = "";
        }

        private static string Dmtf(object o)
        {
            if (o == null) return "";
            try { return ManagementDateTimeConverter.ToDateTime(o.ToString()).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
            catch { return ""; }
        }

        private static long Int64Of(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public static State Query()
        {
            State s = new State();
            try
            {
                ManagementObjectSearcher q = new ManagementObjectSearcher(
                    "SELECT Name,State,StartMode,PathName,ProcessId,StartName FROM Win32_Service WHERE Name='" + ServiceName + "'");
                foreach (ManagementBaseObject o in q.Get())
                {
                    s.ServiceFound = true;
                    s.ServiceState = Str(o["State"]);
                    s.ServiceStart = Str(o["StartMode"]);
                    s.ServicePath = Str(o["PathName"]);
                    s.ServicePid = Int(o["ProcessId"]);
                    break;
                }
            }
            catch (Exception ex) { s.Error = ex.Message; Log.Ex("查询 OSD 服务失败", ex); }

            try
            {
                List<string> pids = new List<string>();
                ManagementObjectSearcher q2 = new ManagementObjectSearcher(
                    "SELECT ProcessId,Name,CreationDate,ThreadCount,WorkingSetSize FROM Win32_Process WHERE Name='" + UtilityExe + "'");
                foreach (ManagementBaseObject o in q2.Get())
                {
                    pids.Add(Str(o["ProcessId"]));
                    if (s.UtilitySince.Length == 0)
                    {
                        s.UtilitySince = Dmtf(o["CreationDate"]);
                        s.UtilityMemThreads = (Int64Of(o["WorkingSetSize"]) / (1024L * 1024L)).ToString(CultureInfo.InvariantCulture)
                            + " MB / " + Int(o["ThreadCount"]).ToString(CultureInfo.InvariantCulture) + " 线程";
                    }
                }
                s.UtilityCount = pids.Count;
                s.UtilityPids = string.Join(", ", pids.ToArray());
            }
            catch (Exception ex) { if (s.Error.Length == 0) s.Error = ex.Message; Log.Ex("查询 OSD 进程失败", ex); }

            try
            {
                ManagementObjectSearcher q3 = new ManagementObjectSearcher(
                    "SELECT ProcessId,CreationDate FROM Win32_Process WHERE Name='" + ServiceName + ".exe'");
                foreach (ManagementBaseObject o in q3.Get()) { s.ServiceSince = Dmtf(o["CreationDate"]); break; }
            }
            catch { }
            return s;
        }

        public static string StatusLine(State s)
        {
            if (!s.ServiceFound && s.Error.Length > 0) return "OSD：查询失败（" + s.Error + "）";
            string svc = s.ServiceFound ? ("服务 " + s.ServiceState) : "服务未安装";
            string app = s.UtilityCount > 0 ? ("进程运行中 " + s.UtilityCount + " 个") : "进程未运行";
            string flag = (s.ServiceFound && s.ServiceState == "Running" && s.UtilityCount > 0) ? " · " : " · 异常 · ";
            return "OSD：" + svc + flag + app;
        }

        public static bool Healthy(State s)
        {
            return s.ServiceFound && s.ServiceState == "Running" && s.UtilityCount > 0;
        }

        /// <summary>只查服务当前状态（"Running" / "Stopped" / "Start Pending" / "Stop Pending" …）。</summary>
        public static string ServiceState()
        {
            try
            {
                ManagementObjectSearcher q = new ManagementObjectSearcher(
                    "SELECT State FROM Win32_Service WHERE Name='" + ServiceName + "'");
                foreach (ManagementBaseObject o in q.Get()) return Str(o["State"]);
            }
            catch { }
            return "";
        }

        /// <summary>重启 OSD：服务 restart + OSD 界面进程重启。需要管理员。</summary>
        public static string Restart()
        {
            StringBuilder sb = new StringBuilder();
            string o;

            int c1 = Proc.Run("sc.exe", "stop " + ServiceName, out o, 30000);
            sb.AppendLine("sc stop  → exit " + c1.ToString(CultureInfo.InvariantCulture) + " " + OneLine(o));
            Log.Info("OSD 重启：sc stop exit=" + c1 + " " + OneLine(o));

            // 必须等服务真正停稳再 start，否则会得到「服务正在停止」而启动失败
            string st = "";
            for (int i = 0; i < 24; i++)
            {
                Thread.Sleep(500);
                st = ServiceState();
                if (st != "Stopping" && st != "Running" && st != "Stop Pending") break;
            }
            sb.AppendLine("停止后状态 → " + (st.Length > 0 ? st : "未知"));
            Log.Info("OSD 重启：停止后服务状态 = " + st);

            int c2 = Proc.Run("sc.exe", "start " + ServiceName, out o, 30000);
            sb.AppendLine("sc start → exit " + c2.ToString(CultureInfo.InvariantCulture) + " " + OneLine(o));
            Log.Info("OSD 重启：sc start exit=" + c2 + " " + OneLine(o));

            int c3 = Proc.Run("taskkill.exe", "/IM " + UtilityExe + " /F", out o, 20000);
            sb.AppendLine("taskkill → exit " + c3.ToString(CultureInfo.InvariantCulture) + " " + OneLine(o));
            Log.Info("OSD 重启：taskkill exit=" + c3 + " " + OneLine(o));

            Thread.Sleep(1200);
            string exePath = Path.Combine(InstallDir, UtilityExe);
            if (File.Exists(exePath))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exePath);
                    psi.UseShellExecute = true;
                    psi.WorkingDirectory = InstallDir;
                    Process.Start(psi);
                    sb.AppendLine("已重新启动 " + exePath);
                    Log.Info("OSD 重启：已启动 " + exePath);
                }
                catch (Exception ex) { sb.AppendLine("启动 OSD 失败：" + ex.Message); Log.Ex("启动 OSD 界面失败", ex); }
            }
            else
            {
                sb.AppendLine("未找到 " + exePath + "（官方 OSD 包没装？）");
                Log.Warn("OSD 重启：未找到 " + exePath);
            }
            return sb.ToString().Trim();
        }

        /// <summary>采集 OSD 诊断证据，写入 osd-diagnose.txt 并返回全文。</summary>
        public static string Diagnose()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("OpenMIFS OSD 诊断 —— " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("主机：" + Environment.MachineName + "  用户：" + Environment.UserName
                + "  管理员：" + (MifsApp.IsElevated ? "是" : "否"));
            sb.AppendLine();

            State s = Query();
            sb.AppendLine("== 服务 / 进程 ==");
            sb.AppendLine("服务名        : " + ServiceName);
            sb.AppendLine("是否存在      : " + (s.ServiceFound ? "是" : "否"));
            sb.AppendLine("状态 / 启动   : " + s.ServiceState + " / " + s.ServiceStart);
            sb.AppendLine("服务进程 PID  : " + s.ServicePid.ToString(CultureInfo.InvariantCulture) + (s.ServiceSince.Length > 0 ? "（启动于 " + s.ServiceSince + "）" : ""));
            sb.AppendLine("服务路径      : " + s.ServicePath);
            sb.AppendLine("OSD 界面进程  : " + s.UtilityCount.ToString(CultureInfo.InvariantCulture) + " 个"
                + (s.UtilityPids.Length > 0 ? "（PID " + s.UtilityPids + "）" : ""));
            if (s.UtilitySince.Length > 0)
                sb.AppendLine("界面进程启动于: " + s.UtilitySince + "  资源 " + s.UtilityMemThreads);
            if (s.Error.Length > 0) sb.AppendLine("查询错误      : " + s.Error);
            sb.AppendLine();

            sb.AppendLine("== 安装目录 ==");
            try
            {
                if (Directory.Exists(InstallDir))
                {
                    string[] files = Directory.GetFiles(InstallDir);
                    for (int i = 0; i < files.Length; i++)
                    {
                        FileInfo fi = new FileInfo(files[i]);
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-32} {1,10:N0} 字节  {2}",
                            fi.Name, fi.Length, fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
                    }
                }
                else sb.AppendLine("  目录不存在：" + InstallDir);
            }
            catch (Exception ex) { sb.AppendLine("  读取失败：" + ex.Message); }
            sb.AppendLine();

            sb.AppendLine("== 服务安装日志尾部（InstallLog / InstallState）==");
            try
            {
                string[] logs = Directory.Exists(InstallDir)
                    ? Directory.GetFiles(InstallDir, "*.InstallLog") : new string[0];
                if (logs.Length == 0) sb.AppendLine("  没有 InstallLog 文件");
                for (int i = 0; i < logs.Length; i++)
                {
                    sb.AppendLine("  --- " + Path.GetFileName(logs[i]) + " ---");
                    string[] tail = TailLines(logs[i], 25);
                    for (int j = 0; j < tail.Length; j++) sb.AppendLine("  " + tail[j]);
                }
            }
            catch (Exception ex) { sb.AppendLine("  读取失败：" + ex.Message); }
            sb.AppendLine();

            EventScan acc = new EventScan();
            try
            {
                DumpEvents(sb, "Application", 30, 20000, acc);
                DumpEvents(sb, "System", 30, 20000, acc);
                if (acc.Matched == 0 && acc.Heartbeat == 0 && acc.OsdEvents == 0)
                    sb.AppendLine("  没有匹配 BLD / OSD / Hotkey 的事件");
            }
            catch (Exception ex) { sb.AppendLine("  事件日志查询失败：" + ex.GetType().Name + ": " + ex.Message); }
            sb.AppendLine();

            sb.AppendLine("== OSD 事件投递（OSDEvents）—— Fn 事件到底有没有送到 OSD 进程 ==");
            if (acc.OsdEvents > 0)
            {
                sb.AppendLine("  条数          : " + acc.OsdEvents.ToString(CultureInfo.InvariantCulture) + "（最近优先扫描范围内）");
                sb.AppendLine("  最新一条      : " + acc.OsdNewest);
                sb.AppendLine("  样例负载      : " + string.Join(" / ", acc.OsdSamples.ToArray()));
                sb.AppendLine("  判读          : 有近期条目 = Fn 事件仍在送达 OSD 进程，接收环节正常，");
                sb.AppendLine("                  问题出在「画出来」这一步（DPI/分层窗口/合成），不是热键坏了。");
            }
            else
            {
                sb.AppendLine("  没有 OSDEvents 条目。");
                sb.AppendLine("  判读          : 要么这台机器不用这个日志源，要么 Fn 事件根本没送到 OSD 进程。");
                sb.AppendLine("                  按一次 Fn 组合键后再跑一次诊断，对比条数是否增加。");
            }
            sb.AppendLine();

            if (acc.Heartbeat > 0)
            {
                sb.AppendLine("== 服务心跳（BLDHotKeyServiceEvent）==");
                sb.AppendLine("  条数          : " + acc.Heartbeat.ToString(CultureInfo.InvariantCulture)
                    + (acc.HbCapped ? "（已截断，只统计最近 1200 条）" : ""));
                sb.AppendLine("  最新一条      : " + acc.HbNewest);
                sb.AppendLine("  最早一条      : " + acc.HbOldest);
                sb.AppendLine("  判读          : 最新一条距今很久 = 服务的这条链路早停了；");
                sb.AppendLine("                  心跳正常时应该每 5 秒一条。注意心跳在跑也不代表提示能显示出来。");
                sb.AppendLine();
            }

            sb.AppendLine("== 显示环境 / DPI（分层窗口画不出来的头号嫌疑）==");
            try
            {
                Screen[] screens = Screen.AllScreens;
                sb.AppendLine("  显示器数量    : " + screens.Length.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < screens.Length; i++)
                {
                    sb.AppendLine("    [" + i + "] "
                        + (screens[i].Primary ? "主屏 " : "副屏 ")
                        + screens[i].Bounds.Width.ToString(CultureInfo.InvariantCulture) + "x"
                        + screens[i].Bounds.Height.ToString(CultureInfo.InvariantCulture)
                        + " @ (" + screens[i].Bounds.Left.ToString(CultureInfo.InvariantCulture)
                        + "," + screens[i].Bounds.Top.ToString(CultureInfo.InvariantCulture) + ")"
                        + "  工作区 " + screens[i].WorkingArea.Width.ToString(CultureInfo.InvariantCulture) + "x"
                        + screens[i].WorkingArea.Height.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) { sb.AppendLine("  查询显示环境失败：" + ex.Message); }
            try
            {
                float dpiX = 96f;
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpiX = g.DpiX;
                int pct = (int)Math.Round(dpiX / 96f * 100f);
                sb.AppendLine("  系统 DPI      : " + dpiX.ToString("0", CultureInfo.InvariantCulture)
                    + "（约 " + pct.ToString(CultureInfo.InvariantCulture) + "% 缩放）");
                if (pct != 100)
                    sb.AppendLine("  判读          : 缩放不是 100%。OSD 界面进程是 DPI 不感知的，");
                else
                    sb.AppendLine("  判读          : 缩放 100%，DPI 这条嫌疑下降。");
            }
            catch (Exception ex) { sb.AppendLine("  查询 DPI 失败：" + ex.Message); }
            sb.AppendLine("  DPI 兼容标记  : " + (OsdDpi.IsEnabled()
                ? "已应用（" + OsdDpi.FlagValue + "）"
                : "未应用") + "   路径：" + OsdDpi.ExePath);
            sb.AppendLine();

            sb.AppendLine("== 结论提示（按嫌疑从高到低）==");
            sb.AppendLine("  1) DPI / 分层窗口：OSD 提示是 UpdateLayeredWindow 画的，进程又按 96 DPI 工作，");
            sb.AppendLine("     缩放 125%/150% 时可能静默失效（不报错、不崩溃、日志照写）。");
            sb.AppendLine("     试：勾选「DPI 兼容修复」→ 重启 OSD → 按 Fn 看提示；或把缩放临时改成 100% 对照。");
            sb.AppendLine("  2) Fn 事件是否送达：看上面 OSDEvents 有没有近期条目。有 = 接收正常，坏在显示。");
            sb.AppendLine("  3) 服务触发链路：看心跳最新一条距今多久。停写很久 = 服务这侧也断了。");
            sb.AppendLine("  4) 多显示器 / 缩放异常、第三方覆盖层（虚拟显示器、显卡 overlay）、杀软拦截。");
            sb.AppendLine("  最快的第一步仍是「重启 OSD」（服务 → 界面进程，可逆）。");

            string text = sb.ToString();
            try
            {
                if (!Directory.Exists(Log.Folder)) Directory.CreateDirectory(Log.Folder);
                File.WriteAllText(Path.Combine(Log.Folder, "osd-diagnose.txt"), text, new UTF8Encoding(false));
                Log.Info("OSD 诊断已写入 " + Path.Combine(Log.Folder, "osd-diagnose.txt"));
            }
            catch (Exception ex) { Log.Ex("写入 OSD 诊断文件失败", ex); }
            Log.Info("OSD 诊断摘要：" + StatusLine(Query()));
            return text;
        }

        /// <summary>事件日志统计累加器（Application / System 两个日志共用）。</summary>
        private sealed class EventScan
        {
            public int Matched;
            public int Heartbeat;
            public bool HbCapped;
            public string HbNewest = "";
            public string HbOldest = "";
            public int OsdEvents;
            public string OsdNewest = "";
            public List<string> OsdSamples = new List<string>();
        }

        /// <summary>按「最新优先」扫事件日志：心跳折叠成时间范围，OSDEvents 单独统计，
        /// 其余相关条目最多保留 20 条直接写进 sb。</summary>
        private static void DumpEvents(StringBuilder sb, string logName, int days, int maxScan, EventScan acc)
        {
            List<string> matched = new List<string>();
            int hbSkip = 0;
            int osdSeen = 0;
            string xpath = "*[System[TimeCreated[timediff(@SystemTime) <= "
                         + (days * 24L * 3600L * 1000L).ToString(CultureInfo.InvariantCulture) + "]]]";
            EventLogQuery q = new EventLogQuery(logName, PathType.LogName, xpath);
            q.ReverseDirection = true;   // 从最新往回读，避免被海量心跳吃掉扫描额度
            EventLogReader r = new EventLogReader(q);
            int scanned = 0;
            try
            {
                for (EventRecord rec = r.ReadEvent(); rec != null && scanned < maxScan; rec = r.ReadEvent())
                {
                    scanned++;
                    string provider = "";
                    string msg = "";
                    try { provider = rec.ProviderName; } catch { }
                    try { msg = rec.FormatDescription(); } catch { }
                    string probe = (provider + " " + msg);
                    bool hit = probe.IndexOf("BLD", StringComparison.OrdinalIgnoreCase) >= 0
                            || probe.IndexOf("FnHotkey", StringComparison.OrdinalIgnoreCase) >= 0
                            || probe.IndexOf("HotKeyService", StringComparison.OrdinalIgnoreCase) >= 0
                            || probe.IndexOf("OSD", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;

                    string time = "";
                    try { time = rec.TimeCreated.HasValue ? rec.TimeCreated.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : ""; } catch { }

                    acc.Matched++;

                    // ① 服务心跳：每 5 秒一条 "BLDFnHotkeyUtility.exe is running..." —— 只留时间范围
                    if (probe.IndexOf("is running", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (acc.HbNewest.Length == 0) acc.HbNewest = time;   // 逆序读，第一条就是最新的
                        acc.HbOldest = time;
                        acc.Heartbeat++;
                        hbSkip++;
                        if (hbSkip >= 1200) { acc.HbCapped = true; break; }
                        continue;
                    }

                    // ② OSDEvents：OSD 进程收到的 Fn 事件原始负载，证明事件有没有送达
                    if (provider.IndexOf("OSDEvents", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        acc.OsdEvents++;
                        if (acc.OsdNewest.Length == 0) acc.OsdNewest = time;
                        string payload = msg == null ? "" : msg.Replace("\r", " ").Replace("\n", " ").Trim();
                        if (payload.Length > 80) payload = payload.Substring(0, 80);
                        if (payload.Length > 0 && !acc.OsdSamples.Contains(payload) && acc.OsdSamples.Count < 8)
                            acc.OsdSamples.Add(payload);
                        osdSeen++;
                        if (osdSeen >= 80) osdSeen = 0;   // 不因为 OSDEvents 太多而一直扫
                        continue;
                    }

                    // ③ 其他相关条目（崩溃、报错等）：最多 20 条，逆序读 = 最新的 20 条
                    if (matched.Count < 20)
                    {
                        string first = msg == null ? "" : msg.Replace("\r", " ").Replace("\n", " ");
                        if (first.Length > 300) first = first.Substring(0, 300) + "…";
                        matched.Add("  [" + logName + "] " + time + "  " + provider + "  id=" + rec.Id
                            + Environment.NewLine + "      " + first);
                    }
                    if (matched.Count >= 20 && acc.Heartbeat > 0) break;
                    if (scanned > 3000 && matched.Count >= 5) break;
                }
            }
            finally { if (r != null) r.Dispose(); }

            if (matched.Count > 0)
            {
                sb.AppendLine("== 其他相关事件（最新 " + matched.Count.ToString(CultureInfo.InvariantCulture) + " 条）==");
                for (int i = 0; i < matched.Count; i++) sb.AppendLine(matched[i]);
                sb.AppendLine();
            }
        }

        private static string[] TailLines(string file, int n)
        {
            byte[] bytes = File.ReadAllBytes(file);
            string text = DecodeBest(bytes);
            string[] all = text.Replace("\r\n", "\n").Split('\n');
            int start = all.Length > n ? all.Length - n : 0;
            List<string> outLines = new List<string>();
            for (int i = start; i < all.Length; i++)
            {
                string s = all[i].TrimEnd();
                if (s.Length > 0) outLines.Add(s);
            }
            return outLines.ToArray();
        }

        private static string DecodeBest(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            string utf8 = new UTF8Encoding(false).GetString(bytes);
            if (utf8.IndexOf('\uFFFD') < 0) return utf8;
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes); }
            catch { return utf8; }
        }

        private static string OneLine(string s)
        {
            if (s == null) return "";
            return s.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string Str(object o) { return o == null ? "" : o.ToString(); }

        private static int Int(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
    }

    // ──────────────────────────────────────────── OSD 的 DPI 兼容修复（可撤销）
    // OSD 界面进程 BLDFnHotkeyUtility.exe 是 DPI 不感知的（清单里只有 asInvoker）。
    // 在 125%/150% 缩放下，它用 UpdateLayeredWindow 画的提示可能静默失效。
    // Windows 自带的兼容性标记可以强制它按系统 DPI 渲染：
    //   HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers
    //   值名 = 程序完整路径，值 = "~ HIGHDPIAWARE"
    // 这是「兼容性 → 更改高 DPI 设置」界面写的同一条注册表项，随时可以删掉还原。
    internal static class OsdDpi
    {
        private const string SubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        public const string FlagValue = "~ HIGHDPIAWARE";

        public static string ExePath { get { return Path.Combine(Osd.InstallDir, Osd.UtilityExe); } }

        public static bool FileExists { get { return File.Exists(ExePath); } }

        /// <summary>返回当前已写入的标记值，没有则返回 ""。</summary>
        public static string Read()
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey k = baseKey.OpenSubKey(SubKey))
                {
                    if (k == null) return "";
                    object v = k.GetValue(ExePath);
                    return v == null ? "" : v.ToString();
                }
            }
            catch (Exception ex) { Log.Ex("读取 OSD DPI 兼容标记失败", ex); return ""; }
        }

        public static bool IsEnabled() { return Read().IndexOf("HIGHDPIAWARE", StringComparison.OrdinalIgnoreCase) >= 0; }

        /// <summary>写入或删除标记，返回给用户看的结果文本。</summary>
        public static string Apply(bool enable)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey k = baseKey.CreateSubKey(SubKey))
                {
                    if (k == null) return "无法打开注册表项（需要管理员权限）";
                    if (enable)
                    {
                        k.SetValue(ExePath, FlagValue, RegistryValueKind.String);
                        Log.Info("OSD DPI 兼容修复：写入 " + ExePath + " = " + FlagValue);
                        return "已写入 DPI 兼容标记：" + Environment.NewLine + "  " + ExePath + " = " + FlagValue;
                    }
                    k.DeleteValue(ExePath, false);
                    Log.Info("OSD DPI 兼容修复：已删除 " + ExePath + " 的标记");
                    return "已撤销 DPI 兼容标记（删除注册表值）";
                }
            }
            catch (Exception ex)
            {
                Log.Ex("写入 OSD DPI 兼容标记失败", ex);
                return "写入失败：" + ex.Message;
            }
        }
    }

    // ───────────────────────────────────────────────────────── 应用级小工具
    internal static class MifsApp
    {
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                        return new System.Security.Principal.WindowsPrincipal(id)
                            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        public static string VersionText
        {
            get
            {
                try
                {
                    Version v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v.Major.ToString(CultureInfo.InvariantCulture) + "."
                         + v.Minor.ToString(CultureInfo.InvariantCulture) + "."
                         + v.Build.ToString(CultureInfo.InvariantCulture);
                }
                catch { return "未知"; }
            }
        }
    }

    // ─────────────────────────────────────────────────────── 自带 OSD 提示层
    internal sealed class OsdOverlay : Form
    {
        private readonly Label _lbl = new Label();
        private readonly System.Windows.Forms.Timer _hide = new System.Windows.Forms.Timer();

        public OsdOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(28, 28, 32);
            Opacity = 0.9;
            Size = new Size(380, 88);
            Padding = new Padding(1);

            _lbl.Dock = DockStyle.Fill;
            _lbl.ForeColor = Color.White;
            _lbl.BackColor = Color.Transparent;
            _lbl.TextAlign = ContentAlignment.MiddleCenter;
            _lbl.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            Controls.Add(_lbl);

            _hide.Interval = 1600;
            _hide.Tick += delegate { _hide.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW（不进 Alt+Tab）
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Color.FromArgb(120, 122, 200), 1))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        public void ShowText(string text)
        {
            try
            {
                _lbl.Text = text;
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 96);
                if (!Visible) Show();
                BringToFront();
                _hide.Stop();
                _hide.Start();
            }
            catch (Exception ex) { Log.Ex("显示自带 OSD 失败", ex); }
        }
    }

    // ──────────────────────────────────────────────────────────────── 主窗口
    internal sealed class MainForm : Form
    {
        // ── 顶部状态条（N4：版本只在标题栏出现；徽标之间不用 "·" 串接）
        private readonly Label _lblBrand = new Label();
        private readonly Label _lblBadgeMode = new Label();
        private readonly Label _lblBadgeAc = new Label();
        private readonly Label _lblBadgeOsd = new Label();
        private readonly Label _lblClock = new Label();
        private readonly FlatButton _btnRefreshNow = new FlatButton();
        private readonly FlatButton _btnElevate = new FlatButton();

        // ── 左列：控制（平面；回读贴在控件旁）
        private readonly FlatButton[] _btnMode = new FlatButton[3];
        private readonly Label _lblModeNote = new Label();
        private readonly CheckBox _chkFn = new CheckBox();
        private readonly CheckBox _chkTp = new CheckBox();
        private readonly FlatButton[] _btnKbd = new FlatButton[4];
        private readonly Label _lblKbdNote = new Label();
        private readonly FlatButton _btnBoost = new FlatButton();
        private readonly Label _lblBoostNote = new Label();
        private readonly ComboBox _cmbOsdHint = new ComboBox();
        private readonly FlatButton _btnOsdRestart = new FlatButton();
        private readonly FlatButton _btnOsdDiag = new FlatButton();
        private readonly FlatButton _btnOsdDir = new FlatButton();
        private readonly FlatButton _btnRecap = new FlatButton();
        private readonly CheckBox _chkStartup = new CheckBox();
        private readonly CheckBox _chkDpi = new CheckBox();
        private readonly Label _lblDpi = new Label();

        // ── 左列底部：偏好设置（折叠；N11）
        private readonly FlatButton _btnPrefs = new FlatButton();
        private readonly Panel _pnlPrefs = new Panel();
        private readonly CheckedListBox _lstTray = new CheckedListBox();
        private readonly Label _lblTrayPreview = new Label();
        private readonly Label _lblTrayBudget = new Label();
        private readonly Label _lblTrayHint = new Label();
        private readonly FlatButton _btnTrayDefault = new FlatButton();
        private readonly Label _lblTrayIcon = new Label();
        private readonly ComboBox _cmbTrayIcon = new ComboBox();
        private readonly Label _lblTrayTh = new Label();
        private readonly NumericUpDown[] _numTrayTh = new NumericUpDown[] { new NumericUpDown(), new NumericUpDown(), new NumericUpDown() };
        private readonly FlatButton _btnSensorProbe = new FlatButton();

        // ── 布局缩放与状态
        private Panel _barLine;
        private Panel _topLine;
        private readonly Panel[] _clusterLines = new Panel[] { new Panel(), new Panel() };
        private readonly Label[] _clusterLabels = new Label[] { new Label(), new Label(), new Label() };
        private readonly Label _lblHintMode = new Label();
        private SectionTitle _secMode, _secSw, _secKbd, _secFan, _secOsd;
        private double _s = 1.0;          // 布局缩放系数（LayoutAll 计算）
        private bool _layoutBusy;         // 防重入：改尺寸会触发 Resize
        private bool _prefsOpen;

        // ── 右列：读数（凹陷面板；关键三项 + 三簇明细）
        private RecessedPanel _pnlReadout;
        private readonly BigReadout[] _big = new BigReadout[3];
        private readonly List<ReadoutRow> _rows = new List<ReadoutRow>();
        private readonly List<string> _rowNames = new List<string>();
        private readonly Label _lblEnv = new Label();
        private readonly Label _lblUnavail = new Label();

        // ── 底部状态行
        private readonly Label _lblSensorHint = new Label();
        private readonly CheckBox _chkAuto = new CheckBox();
        private readonly ComboBox _cmbInterval = new ComboBox();

        // ── 定时器与运行态
        private readonly System.Windows.Forms.Timer _hintTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _osdWatch = new System.Windows.Forms.Timer();
        private readonly ToolTip _tips = new ToolTip();
        private OsdOverlay _overlay;

        private string _osdHintMode = "auto";
        private string _pendingHint;
        private bool _sensorBusy;
        private int _acTypeNow = -1;
        private int _fanRpmBefore = -1;
        private bool _mifsTempPowerChecked;
        private bool _trayUiSync;
        private bool _visibilityBusy;     // HideToTray/Restore 互斥，防止与 Resize 互递归
        private bool _suppress;
        private bool _fanBoostOn;
        private bool _fanBoostUsable;
        private bool _trayHintShown;
        private bool _reallyClose;
        private bool _firstRefreshDone;
        private string _lastLoggedError = "";

        private DateTime _lastOsdCheck = DateTime.MinValue;
        private string _osdStatus = "检测中…";
        private bool _osdOk;
        private DateTime _lastSensorRefresh = DateTime.MinValue;

        // 自带 OSD 的变化监视缓存（专门盯 Fn 键会改的那几个值）
        private int _wMode = -1;
        private int _wFn = -1;
        private int _wTp = -1;
        private int _wKbd = -1;
        private bool _wPrimed;

        public event EventHandler StateChanged;
        /// <summary>图标显示项改变 → 托盘立刻重画（不必等 5 秒定时器）。</summary>
        public event EventHandler TrayIconChanged;
        public MainForm()
        {
            Text = "OpenMIFS v" + MifsApp.VersionText + " — 同方 MIFS 控制台";
            // 布局缩放由 LayoutAll() 自己算（见 Ui.F/Ui.S）；关掉 WinForms 的字体自动缩放，
            // 否则 125%/150% DPI 下会被二次放大 → 中文下缘被截断、偏好设置撑出窗口。
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(940, 860);
            MinimumSize = new Size(720, 600);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;   // 可拖动缩放：右列数据区跟着窗口变大
            MaximizeBox = true;
            Font = Ui.FontUi;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            BuildUi();
            // 自动刷新：默认开（用户可关，选择记进 settings.txt）
            _chkAuto.Checked = Settings.Get("auto_refresh", "1") != "0";
            _chkAuto.Click += delegate
            {
                Settings.Set("auto_refresh", _chkAuto.Checked ? "1" : "0");
                Log.Info("自动刷新：" + (_chkAuto.Checked ? "开" : "关"));
            };
            _timer.Interval = 3000;
            _timer.Tick += delegate { if (_chkAuto.Checked) RefreshAll(); };
            _timer.Start();

            TrayText.Load();
            TrayIcon.Load();
            TrayIcon.LoadThresholds();
            SyncTrayUi();
            RefreshTrayThresholdRow();

            _osdHintMode = Settings.Get("osd_hint", "auto");
            _hintTimer.Interval = 350;   // 等官方 OSD 先画出来再决定要不要显示自带的
            _hintTimer.Tick += delegate { _hintTimer.Stop(); FlushPendingHint(); };

            // 专门盯 Fn 键会改的那几个 EC 值：官方 OSD 失效时由我们自己弹提示。
            // 1.5 秒一轮，只读 4 个功能号，开销很小。
            _osdWatch.Interval = 1500;
            _osdWatch.Tick += delegate { CheckOsdWatch(); };
            _osdWatch.Start();

            Shown += delegate { LayoutAll(); RefreshAll(); };
            // 注意：HideToTray/Restore 会改 ShowInTaskbar（触发句柄重建），从而再次引发 Resize。
            // 必须防重入，否则"最小化后收进托盘 → 点显示主界面"会无限互递归 → 栈溢出崩溃（0xC00000FD）。
            Resize += delegate
            {
                LayoutAll();   // 窗口大小变了 → 整体重排（字号/间距/面板一起缩放）
                if (_visibilityBusy) return;
                if (WindowState == FormWindowState.Minimized) HideToTray();
            };
            FormClosing += OnFormClosing;
        }

        public void ForceClose()
        {
            _reallyClose = true;
            _timer.Stop();
            _osdWatch.Stop();
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_reallyClose) return;
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Log.Info("窗口被关闭 → 最小化到托盘");
                HideToTray();
            }
        }

        internal void HideToTray()
        {
            if (_visibilityBusy) return;             // 防重入（见 Resize 处说明）
            _visibilityBusy = true;
            try { HideToTrayCore(); }
            finally { _visibilityBusy = false; }
        }

        private void HideToTrayCore()
        {
            Hide();
            ShowInTaskbar = false;
            // 关键：把 WindowState 归位。否则 Resize 的判定条件（== Minimized）会一直为真，
            // 任何一次重新布局都会再次调用 HideToTray，进而在句柄重建时无限递归。
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            if (!_trayHintShown && StateChanged != null)
            {
                _trayHintShown = true;
                StateChanged(this, EventArgs.Empty);
            }
        }

        private void Restore()
        {
            if (_visibilityBusy) return;
            _visibilityBusy = true;
            try { RestoreCore(); }
            finally { _visibilityBusy = false; }
            RefreshAll();                            // 放在互斥区外：刷新会间接触发布局
        }

        private void RestoreCore()
        {
            // 顺序很重要（0.5.5 血案）：必须 **先 Show() 再归位 WindowState**。
            // 反过来写（先 Normal 再 Show）会把窗口冻结在"最小化占位坐标"上 ——
            // Windows 给最小化窗口的矩形是 (-25600,-25600) 这类屏幕外坐标，
            // 而 Show() 只让窗口变"可见"，不会把它挪回屏幕 → 用户点了显示却什么都看不到。
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            EnsureOnScreen();
            Activate();
        }

        /// <summary>兜底：窗口矩形若完全落在所有屏幕之外（最小化占位坐标、显示器拔掉等），
        /// 就挪回主屏居中。没有这一步，一旦坐标跑到屏幕外，用户再也点不回来。</summary>
        private void EnsureOnScreen()
        {
            try
            {
                Rectangle b = Bounds;
                bool on = false;
                Screen[] all = Screen.AllScreens;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].WorkingArea.IntersectsWith(b)) { on = true; break; }
                }
                if (on) return;

                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int w = (Width > 200 && Width < wa.Width + 100) ? Width : 940;
                int h = (Height > 200 && Height < wa.Height + 100) ? Height : 815;
                int x = wa.X + Math.Max(0, (wa.Width - w) / 2);
                int y = wa.Y + Math.Max(0, (wa.Height - h) / 3);
                Location = new Point(x, y);
                Log.Warn("窗口位置异常（" + b.ToString() + "）→ 已挪回主屏 (" + x + "," + y + ")");
            }
            catch (Exception ex) { Log.Ex("窗口位置兜底失败", ex); }
        }

        public void ToggleVisible()
        {
            if (Visible && WindowState != FormWindowState.Minimized) HideToTray();
            else Restore();
        }

        /// <summary>自带屏幕提示的统一入口。
        /// 模式：auto = 官方 OSD 正在画就让位（默认）/ always = 总是显示 / off = 关闭。
        /// defer=true 用于外部（Fn 键）改动：等 350 ms 让官方 OSD 先画出来再判断，避免两条提示重叠。</summary>
        public void OsdHint(string text, bool defer)
        {
            if (_osdHintMode == "off") return;
            if (defer)
            {
                _pendingHint = text;
                _hintTimer.Stop();
                _hintTimer.Start();
                return;
            }
            ShowHintIfAllowed(text);
        }

        /// <summary>兼容旧调用：立刻显示（内部按钮触发的动作）。</summary>
        public void ShowOsd(string text)
        {
            OsdHint(text, false);
        }

        private void FlushPendingHint()
        {
            string t = _pendingHint;
            _pendingHint = null;
            if (t != null) ShowHintIfAllowed(t);
        }

        private void ShowHintIfAllowed(string text)
        {
            if (_osdHintMode == "auto" && Osd.FloatingWindowVisible())
            {
                Log.Info("官方 OSD 正在显示，跳过自带提示：" + text);
                return;
            }
            if (_overlay == null) _overlay = new OsdOverlay();
            _overlay.ShowText(text);
        }

        /// <summary>把监视缓存对齐到当前 EC 值，且不弹提示（自己刚改过值时调用，避免重复弹）。</summary>
        public void PrimeOsdWatch()
        {
            try
            {
                int? m = Mifs.GetByte(Mifs.FnPerMode);
                if (m.HasValue) _wMode = m.Value;
                int? f = Mifs.GetByte(Mifs.FnFnLock);
                if (f.HasValue) _wFn = f.Value;
                int? t = Mifs.GetByte(Mifs.FnTpLock);
                if (t.HasValue) _wTp = t.Value;
                int? k = Mifs.GetByte(Mifs.FnRgbBright);
                if (k.HasValue) _wKbd = k.Value;
                _wPrimed = true;
            }
            catch (Exception ex) { Log.Ex("对齐 OSD 监视缓存失败", ex); }
        }

        /// <summary>轮询 Fn 键会改的 EC 值，发现外部变化就弹自带 OSD。</summary>
        private void CheckOsdWatch()
        {
            if (_osdHintMode == "off") return;
            try
            {
                int? m = Mifs.GetByte(Mifs.FnPerMode);
                if (m.HasValue)
                {
                    if (_wPrimed && _wMode != m.Value)
                    {
                        Log.Info("检测到外部改动：性能模式 → " + ModeMap.Label(m.Value) + "（弹自带 OSD）");
                        OsdHint("性能模式 · " + ModeMap.Label(m.Value), true);
                    }
                    _wMode = m.Value;
                }
                int? f = Mifs.GetByte(Mifs.FnFnLock);
                if (f.HasValue)
                {
                    if (_wPrimed && _wFn != f.Value) { Log.Info("检测到外部改动：Fn 锁 → " + f.Value); OsdHint("Fn 锁 · " + (f.Value == 1 ? "开" : "关"), true); }
                    _wFn = f.Value;
                }
                int? t = Mifs.GetByte(Mifs.FnTpLock);
                if (t.HasValue)
                {
                    if (_wPrimed && _wTp != t.Value) { Log.Info("检测到外部改动：触控板锁 → " + t.Value); OsdHint("触控板 · " + (t.Value == 1 ? "已锁定" : "正常"), true); }
                    _wTp = t.Value;
                }
                int? k = Mifs.GetByte(Mifs.FnRgbBright);
                if (k.HasValue)
                {
                    if (_wPrimed && _wKbd != k.Value) { Log.Info("检测到外部改动：键盘背光 → " + k.Value); OsdHint("键盘背光 · 等级 " + k.Value, true); }
                    _wKbd = k.Value;
                }
                _wPrimed = true;
            }
            catch (Exception ex) { Log.Ex("OSD 监视轮询失败", ex); }
        }

        // ────────────────────────────────────────────────────── UI 构建
        // 布局：左列 = 硬件控制（固定宽度），右列 = 状态 + 传感器（随窗口缩放）
        private const int LeftColX = 12;
        private const int LeftColW = 456;
        private const int RightColX = 480;
        private const int RightColGap = 12;

        // ═══════════════════════════════════════════════════════════════
        //  界面构建（v0.6.0「仪表台」三区布局，见 docs/UI-DESIGN.md §2）
        //  左列控制（平面）/ 右列读数（凹陷）/ 顶部状态条 / 底部状态行
        // ═══════════════════════════════════════════════════════════════
        // ═══════════════════════════════════════════════════════════════
        //  界面：控件创建（BuildUi / BuildPrefsUi）+ 几何重排（LayoutAll）
        //  几何全部按"设计单位 × 缩放系数 s"计算，s 由窗口大小决定：
        //  拖大/拖小窗口时字号、行高、面板一起缩放（AutoScaleMode=None，不吃 WinForms 的二次缩放）
        // ═══════════════════════════════════════════════════════════════
        private SectionTitle NewTitle(string text)
        {
            SectionTitle s = new SectionTitle();
            s.Text = text;
            Controls.Add(s);
            return s;
        }

        private void WireCheck(CheckBox c, string text, EventHandler onClick, int tab)
        {
            c.Text = text;
            c.ForeColor = Ui.Ink;
            c.FlatStyle = FlatStyle.System;
            c.TabIndex = tab;
            c.AccessibleName = text;
            if (onClick != null) c.Click += onClick;
            Controls.Add(c);
        }

        private void BuildUi()
        {
            BackColor = Ui.Surface;
            Font = Ui.FontUi;

            // ── 顶部状态条
            _lblBrand.AutoSize = false;
            _lblBrand.Text = "OpenMIFS";
            _lblBrand.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_lblBrand);

            Label[] badges = new Label[] { _lblBadgeMode, _lblBadgeAc, _lblBadgeOsd };
            for (int i = 0; i < badges.Length; i++)
            {
                badges[i].AutoSize = false;
                badges[i].TextAlign = ContentAlignment.MiddleLeft;
                Controls.Add(badges[i]);
            }

            _lblClock.AutoSize = false;
            _lblClock.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_lblClock);

            _btnRefreshNow.Text = "立即刷新";
            _btnRefreshNow.AccessibleName = "立即刷新";
            _btnRefreshNow.Click += delegate { Log.Info("手动刷新"); RefreshAll(); };
            Controls.Add(_btnRefreshNow);

            _btnElevate.Text = "以管理员重启";
            _btnElevate.AccessibleName = "以管理员身份重启";
            _btnElevate.Visible = false;
            _btnElevate.Click += OnElevateClick;
            Controls.Add(_btnElevate);

            _barLine = new Panel();
            _barLine.BackColor = Ui.Hairline;
            Controls.Add(_barLine);

            // ── 左列：控制
            _secMode = NewTitle("性能模式");
            _secSw = NewTitle("硬件开关");
            _secKbd = NewTitle("键盘背光");
            _secFan = NewTitle("风扇");
            _secOsd = NewTitle("OSD 与启动");

            for (int i = 0; i < ModeMap.Order.Length; i++)
            {
                FlatButton b = new FlatButton();
                b.Tag = ModeMap.Order[i];
                b.TabIndex = 10 + i;
                b.AccessibleName = "性能模式：" + ModeMap.Order[i];
                b.Click += OnModeClick;
                Controls.Add(b);
                _btnMode[i] = b;
            }

            WireCheck(_chkFn, "Fn 锁", OnFnLockClick, 20);
            WireCheck(_chkTp, "触控板锁定", OnTpLockClick, 21);

            for (int i = 0; i < 4; i++)
            {
                FlatButton b = new FlatButton();
                b.Tag = i;
                b.TabIndex = 22 + i;
                b.AccessibleName = "键盘背光等级 " + i.ToString(CultureInfo.InvariantCulture);
                b.Click += OnKbdClick;
                Controls.Add(b);
                _btnKbd[i] = b;
            }

            _btnBoost.Text = "风扇满速";
            _btnBoost.TabIndex = 30;
            _btnBoost.AccessibleName = "风扇满速开关";
            _btnBoost.Click += OnFanBoostClick;
            Controls.Add(_btnBoost);

            Label[] notes = new Label[] { _lblModeNote, _lblKbdNote, _lblBoostNote, _lblDpi, _lblHintMode };
            for (int i = 0; i < notes.Length; i++)
            {
                notes[i].AutoSize = false;
                notes[i].TextAlign = ContentAlignment.MiddleLeft;
                notes[i].ForeColor = Ui.Label;
                Controls.Add(notes[i]);
            }
            _lblHintMode.Text = "屏幕提示";
            _lblModeNote.Text = "检测中…";
            _lblSensorHint.AutoSize = false;
            _lblSensorHint.TextAlign = ContentAlignment.MiddleLeft;
            _lblSensorHint.ForeColor = Ui.Label;
            Controls.Add(_lblSensorHint);

            _cmbOsdHint.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbOsdHint.TabIndex = 40;
            _cmbOsdHint.AccessibleName = "自带屏幕提示模式";
            _cmbOsdHint.Items.AddRange(new object[] { "自动（不重复）", "总是显示", "关闭" });
            _cmbOsdHint.SelectedIndex = 0;
            _cmbOsdHint.SelectedIndexChanged += OnOsdHintModeChanged;
            Controls.Add(_cmbOsdHint);

            FlatButton[] osdBtns = new FlatButton[] { _btnOsdRestart, _btnOsdDiag, _btnOsdDir, _btnRecap };
            string[] osdText = new string[] { "重启 OSD", "诊断 OSD", "打开目录", "重测功能" };
            EventHandler[] osdHandlers = new EventHandler[] { OnOsdRestartClick, OnOsdDiagClick, OnOpenOsdDirClick, OnRecapClick };
            for (int i = 0; i < osdBtns.Length; i++)
            {
                osdBtns[i].Text = osdText[i];
                osdBtns[i].AccessibleName = osdText[i];
                osdBtns[i].TabIndex = 41 + i;
                osdBtns[i].Click += osdHandlers[i];
                Controls.Add(osdBtns[i]);
            }

            WireCheck(_chkStartup, "开机自启（登录时静默进托盘）", OnStartupClick, 45);
            WireCheck(_chkDpi, "DPI 兼容修复（实验，可撤销）", OnDpiFixClick, 46);

            _btnPrefs.Text = "\u25B8 偏好设置";
            _btnPrefs.TabIndex = 60;
            _btnPrefs.AccessibleName = "展开或收起偏好设置";
            _btnPrefs.Click += delegate { TogglePrefs(); };
            Controls.Add(_btnPrefs);

            _pnlPrefs.BackColor = Ui.Surface;
            _pnlPrefs.AutoScroll = true;
            Controls.Add(_pnlPrefs);
            BuildPrefsUi();

            // ── 右列：读数面板（凹陷方角 = 只读）
            _pnlReadout = new RecessedPanel();
            Controls.Add(_pnlReadout);
            _tips.SetToolTip(_pnlReadout, "只读读数区：窗口可拖动缩放，这里会跟着变大。");

            for (int i = 0; i < 3; i++) _big[i] = new BigReadout(_pnlReadout);

            _topLine = new Panel();
            _topLine.BackColor = Ui.Hairline;
            _pnlReadout.Controls.Add(_topLine);

            string[] cluster = new string[] { "热与功耗", "频率与负载", "存储与电池" };
            string[][] clusterRows = new string[][]
            {
                new string[] { "GPU 温度", "GPU 功耗" },
                new string[] { "CPU 频率", "CPU 负载", "GPU 利用率", "GPU 频率", "GPU 显存" },
                new string[] { "内存占用", "内存规格", "磁盘温度", "磁盘", "电池" }
            };
            for (int c = 0; c < cluster.Length; c++)
            {
                _clusterLabels[c].AutoSize = false;
                _clusterLabels[c].Text = cluster[c];
                _clusterLabels[c].ForeColor = Ui.Label;
                _clusterLabels[c].BackColor = Color.Transparent;
                _clusterLabels[c].TextAlign = ContentAlignment.MiddleLeft;
                _pnlReadout.Controls.Add(_clusterLabels[c]);
                if (c > 0)
                {
                    _clusterLines[c - 1].BackColor = Ui.Hairline;
                    _pnlReadout.Controls.Add(_clusterLines[c - 1]);
                }
                for (int r = 0; r < clusterRows[c].Length; r++)
                {
                    _rows.Add(new ReadoutRow(_pnlReadout, clusterRows[c][r]));
                    _rowNames.Add(clusterRows[c][r]);
                }
            }

            _lblEnv.AutoSize = false;
            _lblEnv.BackColor = Color.Transparent;
            _lblEnv.ForeColor = Ui.Label;
            _lblEnv.TextAlign = ContentAlignment.MiddleLeft;
            _pnlReadout.Controls.Add(_lblEnv);

            _lblUnavail.AutoSize = false;
            _lblUnavail.BackColor = Color.Transparent;
            _lblUnavail.ForeColor = Ui.Muted;
            _lblUnavail.TextAlign = ContentAlignment.MiddleLeft;
            _pnlReadout.Controls.Add(_lblUnavail);

            // ── 底部状态行（自动刷新 + 读数提示；刷新动作在状态条右侧）
            _chkAuto.Text = "自动刷新";
            _chkAuto.ForeColor = Ui.Ink;
            _chkAuto.FlatStyle = FlatStyle.System;
            _chkAuto.TabIndex = 70;
            _chkAuto.AccessibleName = "自动刷新开关";
            Controls.Add(_chkAuto);

            _cmbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbInterval.TabIndex = 71;
            _cmbInterval.AccessibleName = "自动刷新间隔";
            _cmbInterval.Items.AddRange(new object[] { "2 秒", "3 秒", "5 秒", "10 秒" });
            _cmbInterval.SelectedIndex = 1;
            _cmbInterval.SelectedIndexChanged += OnIntervalChanged;
            Controls.Add(_cmbInterval);
        }

        /// <summary>偏好设置区（折叠内容）：托盘提示 + 图标阈值 + 数据源探测。</summary>
        private void BuildPrefsUi()
        {
            _lstTray.CheckOnClick = true;
            _lstTray.IntegralHeight = false;
            _lstTray.TabIndex = 61;
            _lstTray.AccessibleName = "托盘悬停提示包含的项目";
            _lstTray.ItemCheck += OnTrayItemCheck;
            for (int i = 0; i < TrayText.All.Length; i++) _lstTray.Items.Add(TrayText.All[i].Label);
            _pnlPrefs.Controls.Add(_lstTray);
            _tips.SetToolTip(_lstTray, "勾选要显示在托盘悬停提示里的项目（顺序固定）。\r\n"
                + "悬停提示最多 62 字符：勾到上限后，再加项会被拒绝 —— 先取消一项再勾。");

            _lblTrayPreview.AutoSize = false;
            _lblTrayPreview.BackColor = Ui.Recessed;
            _lblTrayPreview.ForeColor = Ui.Ink;
            _lblTrayPreview.BorderStyle = BorderStyle.FixedSingle;
            _lblTrayPreview.TextAlign = ContentAlignment.TopLeft;
            _pnlPrefs.Controls.Add(_lblTrayPreview);

            _lblTrayBudget.AutoSize = false;
            _lblTrayBudget.ForeColor = Ui.Label;
            _lblTrayBudget.TextAlign = ContentAlignment.MiddleLeft;
            _pnlPrefs.Controls.Add(_lblTrayBudget);

            _btnTrayDefault.Text = "恢复默认";
            _btnTrayDefault.TabIndex = 62;
            _btnTrayDefault.AccessibleName = "恢复默认提示项";
            _btnTrayDefault.Click += OnTrayDefaultClick;
            _pnlPrefs.Controls.Add(_btnTrayDefault);

            _lblTrayIcon.AutoSize = false;
            _lblTrayIcon.Text = "图标显示";
            _lblTrayIcon.ForeColor = Ui.Label;
            _lblTrayIcon.TextAlign = ContentAlignment.MiddleLeft;
            _pnlPrefs.Controls.Add(_lblTrayIcon);

            _cmbTrayIcon.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbTrayIcon.TabIndex = 63;
            _cmbTrayIcon.AccessibleName = "托盘图标显示内容";
            for (int i = 0; i < TrayIcon.Kinds.Length; i++) _cmbTrayIcon.Items.Add(TrayIcon.KindLabels[i]);
            _cmbTrayIcon.SelectedIndex = 0;
            _cmbTrayIcon.SelectedIndexChanged += OnTrayIconKindChanged;
            _pnlPrefs.Controls.Add(_cmbTrayIcon);

            _lblTrayTh.AutoSize = false;
            _lblTrayTh.Text = "变色阈值";
            _lblTrayTh.ForeColor = Ui.Label;
            _lblTrayTh.TextAlign = ContentAlignment.MiddleLeft;
            _pnlPrefs.Controls.Add(_lblTrayTh);

            for (int i = 0; i < 3; i++)
            {
                _numTrayTh[i].DecimalPlaces = 0;
                _numTrayTh[i].TextAlign = HorizontalAlignment.Right;
                _numTrayTh[i].TabIndex = 64 + i;
                _numTrayTh[i].AccessibleName = "变色阈值第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 档";
                _numTrayTh[i].ValueChanged += OnTrayThresholdValueChanged;
                _pnlPrefs.Controls.Add(_numTrayTh[i]);
            }

            _lblTrayHint.AutoSize = false;
            _lblTrayHint.ForeColor = Ui.Label;
            _lblTrayHint.TextAlign = ContentAlignment.MiddleLeft;
            _pnlPrefs.Controls.Add(_lblTrayHint);

            _btnSensorProbe.Text = "探测数据源";
            _btnSensorProbe.TabIndex = 68;
            _btnSensorProbe.AccessibleName = "探测传感器数据源";
            _btnSensorProbe.Click += OnSensorProbeClick;
            _pnlPrefs.Controls.Add(_btnSensorProbe);
        }

        // ────────────────────────────────────────────────────── 几何重排
        /// <summary>按窗口大小重排全部控件。Resize / Shown / 折叠切换时调用。</summary>
        private void LayoutAll()
        {
            if (_layoutBusy || _pnlReadout == null) return;
            _layoutBusy = true;
            try
            {
                int W = ClientSize.Width, H = ClientSize.Height;
                double s = Math.Min(W / 940.0, H / 860.0);      // 设计画布 940×860
                if (s < 0.78) s = 0.78;
                if (s > 2.20) s = 2.20;
                _s = s;

                Font fUi = Ui.F(9F, false, false, s), fBold = Ui.F(9F, true, false, s), f8 = Ui.F(8F, false, false, s);
                Font fVal = Ui.F(11F, true, true, s), fBig = Ui.F(17F, true, true, s);
                int X = Ui.S(12, s), leftW = Ui.S(Ui.LeftW, s), pad = Ui.S(Ui.Pad, s), gap = Ui.S(Ui.Gap, s);
                int ctrlH = Ui.S(Ui.CtrlH, s), rowH = Ui.S(Ui.RowH, s), h8 = Ui.TextH(f8, s);
                int topY = Ui.S(52, s), bottomY = H - Ui.S(32, s);

                // ── 顶部状态条
                _lblBrand.Font = fBold;
                _lblBrand.Location = new Point(X, Ui.S(8, s));
                _lblBrand.Size = new Size(Ui.S(92, s), Ui.S(24, s));
                Label[] badges = new Label[] { _lblBadgeMode, _lblBadgeAc, _lblBadgeOsd };
                int[] bw = new int[] { Ui.S(72, s), Ui.S(112, s), Ui.S(170, s) };
                int bx = X + Ui.S(86, s);
                for (int i = 0; i < badges.Length; i++)
                {
                    badges[i].Font = fUi;
                    badges[i].Location = new Point(bx, Ui.S(8, s));
                    badges[i].Size = new Size(bw[i], Ui.S(24, s));
                    bx += bw[i] + gap;
                }
                _btnRefreshNow.Font = fUi;
                _btnRefreshNow.Size = new Size(Ui.S(94, s), Ui.S(26, s));
                _btnRefreshNow.Location = new Point(W - Ui.S(106, s), Ui.S(7, s));
                _btnElevate.Font = fUi;
                _btnElevate.Size = new Size(Ui.S(112, s), Ui.S(26, s));
                _btnElevate.Location = new Point(W - Ui.S(226, s), Ui.S(7, s));
                _lblClock.Font = f8;
                _lblClock.Location = new Point(W - Ui.S(348, s), Ui.S(11, s));
                _lblClock.Size = new Size(Ui.S(80, s), h8);
                _barLine.Location = new Point(0, Ui.S(Ui.StatusBarH, s) - 1);
                _barLine.Size = new Size(W, 1);

                // ── 底部状态行：永远贴窗口底（偏好设置展开也不会压住它）
                _chkAuto.Font = fUi;
                _chkAuto.Location = new Point(X, bottomY);
                _chkAuto.Size = new Size(Ui.S(92, s), Ui.S(22, s));
                _cmbInterval.Font = fUi;
                _cmbInterval.Location = new Point(X + Ui.S(96, s), bottomY - Ui.S(1, s));
                _cmbInterval.Size = new Size(Ui.S(70, s), Ui.S(22, s));
                _lblSensorHint.Font = f8;
                _lblSensorHint.Location = new Point(X + Ui.S(176, s), bottomY + Ui.S(2, s));
                _lblSensorHint.Size = new Size(W - X - Ui.S(188, s), h8);

                // ── 左列
                int y = topY;
                y = LayoutTitle(_secMode, X, leftW, y, s, fBold);
                int segW = (leftW - gap * 2) / 3;
                for (int i = 0; i < _btnMode.Length; i++) Place(_btnMode[i], X + i * (segW + gap), y, segW, ctrlH, fUi);
                y += ctrlH + Ui.S(4, s);
                y = LayoutNote(_lblModeNote, X, leftW, y, s, f8);

                y = LayoutTitle(_secSw, X, leftW, y, s, fBold);
                Place(_chkFn, X, y + Ui.S(2, s), Ui.S(150, s), Ui.S(22, s), fUi);
                Place(_chkTp, X + Ui.S(160, s), y + Ui.S(2, s), Ui.S(210, s), Ui.S(22, s), fUi);
                y += Ui.S(30, s);

                y = LayoutTitle(_secKbd, X, leftW, y, s, fBold);
                int kw = Ui.S(62, s), kg = Ui.S(14, s);
                for (int i = 0; i < _btnKbd.Length; i++) Place(_btnKbd[i], X + i * (kw + kg), y, kw, ctrlH, fUi);
                y += ctrlH + Ui.S(4, s);
                y = LayoutNote(_lblKbdNote, X, leftW, y, s, f8);

                y = LayoutTitle(_secFan, X, leftW, y, s, fBold);
                Place(_btnBoost, X, y, Ui.S(240, s), ctrlH, fUi);
                y += ctrlH + Ui.S(4, s);
                y = LayoutNote(_lblBoostNote, X, leftW, y, s, f8);

                y = LayoutTitle(_secOsd, X, leftW, y, s, fBold);
                Place(_lblHintMode, X, y + Ui.S(3, s), Ui.S(76, s), h8, f8);
                Place(_cmbOsdHint, X + Ui.S(78, s), y, Ui.S(140, s), Ui.S(24, s), fUi);
                y += Ui.S(30, s);
                int ow = (leftW - gap * 3) / 4;
                FlatButton[] ob = new FlatButton[] { _btnOsdRestart, _btnOsdDiag, _btnOsdDir, _btnRecap };
                for (int i = 0; i < ob.Length; i++) Place(ob[i], X + i * (ow + gap), y, ow, ctrlH, fUi);
                y += ctrlH + Ui.S(4, s);
                Place(_chkStartup, X, y + Ui.S(2, s), Ui.S(300, s), Ui.S(22, s), fUi);
                y += Ui.S(30, s);
                Place(_chkDpi, X, y + Ui.S(2, s), Ui.S(250, s), Ui.S(22, s), fUi);
                Place(_lblDpi, X + Ui.S(256, s), y + Ui.S(3, s), leftW - Ui.S(256, s), h8, f8);
                y += Ui.S(32, s);

                // ── 偏好设置：面板高度自动收在底部状态行之上；内容放不下就内部滚动
                Place(_btnPrefs, X, y, leftW, ctrlH, fUi);
                y += ctrlH + Ui.S(4, s);
                int prefsH = Ui.S(252, s);
                int room = bottomY - y - gap;
                if (prefsH > room) prefsH = room;
                if (prefsH < Ui.S(80, s)) prefsH = Ui.S(80, s);
                _pnlPrefs.Location = new Point(X, y);
                _pnlPrefs.Size = new Size(leftW, prefsH);
                _pnlPrefs.Visible = _prefsOpen;
                LayoutPrefs(s);

                // ── 右列：读数面板
                int rx = X + leftW + Ui.S(12, s);
                int rw = W - rx - X;
                if (rw < Ui.S(240, s)) rw = Ui.S(240, s);
                int rh = bottomY - topY - Ui.S(8, s);
                _pnlReadout.Location = new Point(rx, topY);
                _pnlReadout.Size = new Size(rw, rh);
                _pnlReadout.Padding = new Padding(pad);

                int innerW = rw - pad * 2 - 4;
                int colW = innerW / 3;
                int by = pad + Ui.S(4, s);
                for (int i = 0; i < 3; i++) _big[i].Layout(pad + i * colW, by, colW, s, fBig, fUi, f8);

                int ry = by + fBig.Height + Ui.S(30, s);
                _topLine.Location = new Point(pad, ry);
                _topLine.Size = new Size(innerW, 1);
                ry += Ui.S(10, s);

                int[] rowStart = new int[] { 0, 2, 7 };
                int[] rowCount = new int[] { 2, 5, 5 };
                for (int c = 0; c < rowStart.Length; c++)
                {
                    if (c > 0)
                    {
                        _clusterLines[c - 1].Location = new Point(pad, ry);
                        _clusterLines[c - 1].Size = new Size(innerW, 1);
                        ry += Ui.S(10, s);
                    }
                    _clusterLabels[c].Font = f8;
                    _clusterLabels[c].Location = new Point(pad, ry);
                    _clusterLabels[c].Size = new Size(innerW, h8);
                    ry += h8 + Ui.S(2, s);
                    for (int r = 0; r < rowCount[c]; r++)
                    {
                        _rows[rowStart[c] + r].Layout(ry, innerW + 4, rowH, s, f8, fVal, f8);
                        ry += rowH;
                    }
                }

                Place(_lblEnv, pad, rh - pad - h8 * 2 - Ui.S(4, s), innerW, h8, f8);
                Place(_lblUnavail, pad, rh - pad - h8, innerW, h8, f8);
            }
            finally { _layoutBusy = false; }
        }

        private int LayoutTitle(SectionTitle t, int x, int w, int y, double s, Font f)
        {
            t.Font = f;
            t.Location = new Point(x, y);
            t.Size = new Size(w, Ui.S(24, s));
            return y + Ui.S(28, s);
        }

        private int LayoutNote(Label l, int x, int w, int y, double s, Font f)
        {
            int h = Ui.TextH(f, s);
            l.Font = f;
            l.Location = new Point(x, y);
            l.Size = new Size(w, h);
            return y + h + Ui.S(4, s);
        }

        private static void Place(Control c, int x, int y, int w, int h, Font f)
        {
            c.Font = f;
            c.Location = new Point(x, y);
            c.Size = new Size(w, h);
        }

        private void LayoutPrefs(double s)
        {
            Font fUi = Ui.F(9F, false, false, s), f8 = Ui.F(8F, false, false, s);
            int h8 = Ui.TextH(f8, s);
            int listW = Ui.S(196, s), listH = Ui.S(146, s), colX = listW + Ui.S(Ui.Gap, s);
            Place(_lstTray, 0, 0, listW, listH, fUi);
            Place(_lblTrayPreview, colX, 0, Ui.S(228, s), Ui.S(84, s), f8);
            Place(_lblTrayBudget, colX, Ui.S(88, s), Ui.S(228, s), h8, f8);
            Place(_btnTrayDefault, colX, Ui.S(110, s), Ui.S(104, s), Ui.S(26, s), fUi);
            int rowY = listH + Ui.S(12, s);
            Place(_lblTrayIcon, 0, rowY, Ui.S(72, s), h8, f8);
            Place(_cmbTrayIcon, Ui.S(76, s), rowY - Ui.S(2, s), Ui.S(140, s), Ui.S(22, s), fUi);
            Place(_lblTrayTh, Ui.S(222, s), rowY, Ui.S(68, s), h8, f8);
            for (int i = 0; i < 3; i++)
                Place(_numTrayTh[i], Ui.S(292, s) + i * Ui.S(50, s), rowY - Ui.S(2, s), Ui.S(46, s), Ui.S(22, s), fUi);
            Place(_lblTrayHint, 0, rowY + h8 + Ui.S(6, s), Ui.S(432, s), h8, f8);
            Place(_btnSensorProbe, 0, rowY + h8 * 2 + Ui.S(10, s), Ui.S(104, s), Ui.S(26, s), fUi);
        }
        private void TogglePrefs()
        {
            _prefsOpen = !_prefsOpen;
            _btnPrefs.Text = (_prefsOpen ? "\u25BE 偏好设置" : "\u25B8 偏好设置");
            _btnPrefs.Selected = _prefsOpen;
            LayoutAll();
            Log.Info("偏好设置：" + (_prefsOpen ? "展开" : "收起"));
        }

        /// <summary>未提权时的一键提权重启（状态条右侧）。</summary>
        private void OnElevateClick(object sender, EventArgs e)
        {
            try
            {
                Log.Info("用户请求以管理员身份重启");
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                ForceClose();
            }
            catch (Exception ex) { Log.Ex("提权重启失败", ex); Warn("提权重启失败：" + ex.Message); }
        }

        // ────────────────────────────────────────────────────── 读数渲染辅助
        private static Reading FindReading(List<Reading> list, string name)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].Name == name) return list[i];
            return null;
        }

        /// <summary>温度类读数 → 档位（颜色之外还要给档位字，见 docs/UI-DESIGN.md §1.2）。</summary>
        private static int LevelOf(Reading r)
        {
            if (r == null || !r.Ok || r.Name.IndexOf("温度") < 0) return 0;
            string v, u;
            Ui.SplitUnit(r.Value, out v, out u);
            double d;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return 0;
            return Ui.LevelFor(d);
        }

        private static void ApplyBig(BigReadout b, List<Reading> list, string name, string caption, bool isTemp)
        {
            Reading r = FindReading(list, name);
            if (r == null || !r.Ok) { b.Set("", "", caption, 0, false); return; }
            string v, u;
            Ui.SplitUnit(Ui.Tidy(r.Value), out v, out u);
            int lv = 0;
            if (isTemp)
            {
                double d;
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) lv = Ui.LevelFor(d);
            }
            b.Set(v, u, caption, lv, true);
        }
        private void SyncTrayUi()
        {
            _trayUiSync = true;
            try
            {
                for (int i = 0; i < TrayText.All.Length; i++)
                    _lstTray.SetItemChecked(i, TrayText.IsSelected(TrayText.All[i].Id));
                for (int i = 0; i < TrayIcon.Kinds.Length; i++)
                    if (TrayIcon.Kinds[i] == TrayIcon.Kind) _cmbTrayIcon.SelectedIndex = i;
            }
            finally { _trayUiSync = false; }
            RefreshTrayPreview();
        }

        private void RefreshTrayPreview()
        {
            string s = TrayText.Build();
            _lblTrayPreview.Text = s.Length > 0 ? s : "（当前没有可显示的项目）";
            _lblTrayBudget.Text = s.Length.ToString(CultureInfo.InvariantCulture) + "/"
                + TrayText.MaxChars.ToString(CultureInfo.InvariantCulture) + " 字符";
            _tips.SetToolTip(_lblTrayBudget, "悬停提示最多 62 字符（Win32 单行）。"
                + "当前勾选项最坏情况 " + TrayText.WorstTotal().ToString(CultureInfo.InvariantCulture) + " 字符。");
        }

        private void OnTrayItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_trayUiSync) return;
            TrayText.Item it = TrayText.All[e.Index];
            bool want = e.NewValue == CheckState.Checked;
            if (want && !TrayText.CanAdd(it.Id))
            {
                e.NewValue = CheckState.Unchecked;      // 放不下就不让勾
                int withIt = TrayText.WorstTotal() + it.Worst + TrayText.Separator.Length;
                _lblTrayHint.ForeColor = Color.FromArgb(176, 36, 36);
                _lblTrayHint.Text = "加不上「" + it.Label + "」：会到 " + withIt.ToString(CultureInfo.InvariantCulture)
                    + " 字符，超过上限 " + TrayText.MaxChars.ToString(CultureInfo.InvariantCulture) + " —— 先取消一项再勾。";
                Log.Info("托盘提示：拒绝勾选 " + it.Id + "（最坏 " + withIt.ToString(CultureInfo.InvariantCulture) + " 字符）");
                return;
            }
            if (!TrayText.TrySelect(it.Id, want))
            {
                e.NewValue = CheckState.Unchecked;
                return;
            }
            _lblTrayHint.ForeColor = Color.FromArgb(110, 110, 116);
            _lblTrayHint.Text = "图标显示选「无」时不使用阈值。";
            RefreshTrayPreview();
        }

        private void OnTrayIconKindChanged(object sender, EventArgs e)
        {
            if (_trayUiSync) return;
            int i = _cmbTrayIcon.SelectedIndex;
            if (i < 0 || i >= TrayIcon.Kinds.Length) return;
            TrayIcon.Save(TrayIcon.Kinds[i]);
            Log.Info("托盘图标：改为 " + TrayIcon.KindLabel() + "（" + TrayIcon.Kinds[i] + "）");
            RefreshTrayThresholdRow();                                             // 阈值行跟随切换
            if (TrayIconChanged != null) TrayIconChanged(this, EventArgs.Empty);   // 托盘立刻重画
        }

        /// <summary>三个数字框任一改变：自动把后面的格子顺推成严格递增，再保存。</summary>
        private void OnTrayThresholdValueChanged(object sender, EventArgs e)
        {
            if (_trayUiSync) return;
            string kind = TrayIcon.Kind;
            if (kind == "none") return;

            // 顺推：保证 t1 < t2 < t3（改哪一格都不会出现"顺序错误"弹窗）
            _trayUiSync = true;
            bool pushed = false;
            try
            {
                for (int i = 1; i < 3; i++)
                {
                    if (_numTrayTh[i].Value <= _numTrayTh[i - 1].Value)
                    {
                        decimal v = _numTrayTh[i - 1].Value + _numTrayTh[i].Increment;
                        if (v > _numTrayTh[i].Maximum) v = _numTrayTh[i].Maximum;
                        if (v != _numTrayTh[i].Value) { _numTrayTh[i].Value = v; pushed = true; }
                    }
                }
            }
            finally { _trayUiSync = false; }

            double a = (double)_numTrayTh[0].Value, b = (double)_numTrayTh[1].Value, c = (double)_numTrayTh[2].Value;
            string reason;
            if (!TrayIcon.SaveThresholds(kind, Fmt(a) + "," + Fmt(b) + "," + Fmt(c), out reason))
            {
                _lblTrayHint.ForeColor = Color.FromArgb(176, 36, 36);
                _lblTrayHint.Text = "没保存：" + reason;
                return;
            }
            RefreshTrayThresholdHint();
            if (TrayIconChanged != null) TrayIconChanged(this, EventArgs.Empty);
            RefreshTrayPreview();
            if (pushed) Log.Info("托盘图标：阈值自动顺推为 " + Fmt(a) + "," + Fmt(b) + "," + Fmt(c));
        }

        /// <summary>阈值行跟着图标数据源走：只显示当前指标的那三格；图标=无 时整行隐藏。</summary>
        private void RefreshTrayThresholdRow()
        {
            string kind = TrayIcon.Kind;
            bool none = kind == "none";
            _lblTrayTh.Visible = !none;
            for (int i = 0; i < 3; i++) _numTrayTh[i].Visible = !none;

            if (none)
            {
                _lblTrayHint.ForeColor = Color.FromArgb(110, 110, 116);
                _lblTrayHint.Text = "图标显示选「无」时不使用阈值。";
                return;
            }

            double[] th = TrayIcon.ThresholdsOf(kind);
            _trayUiSync = true;
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    _numTrayTh[i].Minimum = kind == "cput" ? 20 : 1;
                    _numTrayTh[i].Maximum = kind == "cput" ? 120 : (kind == "cpup" ? 150 : 100);
                    _numTrayTh[i].Increment = 1;
                    decimal v = (decimal)Math.Round(th[i]);
                    if (v < _numTrayTh[i].Minimum) v = _numTrayTh[i].Minimum;
                    if (v > _numTrayTh[i].Maximum) v = _numTrayTh[i].Maximum;
                    _numTrayTh[i].Value = v;
                }
            }
            finally { _trayUiSync = false; }
            RefreshTrayThresholdHint();
        }

        /// <summary>只更新底部说明（不动三格数值，避免打断连续点箭头）。</summary>
        private void RefreshTrayThresholdHint()
        {
            string kind = TrayIcon.Kind;
            if (kind == "none") { _lblTrayHint.ForeColor = Color.FromArgb(110, 110, 116); _lblTrayHint.Text = "图标显示选「无」时不使用阈值。"; return; }
            double[] th = TrayIcon.ThresholdsOf(kind);
            string unit = kind == "cput" ? "℃" : (kind == "cpup" ? "W" : "%");
            _lblTrayHint.ForeColor = Color.FromArgb(110, 110, 116);
            _lblTrayHint.Text = TrayIcon.KindShort(kind) + "阈值（" + unit + "）：≤" + Fmt(th[0]) + " 绿 · ≤"
                + Fmt(th[1]) + " 琥珀 · ≤" + Fmt(th[2]) + " 橙 · 更高红";
        }

        private static string Fmt(double d)
        {
            return d.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private void OnTrayDefaultClick(object sender, EventArgs e)
        {
            TrayText.ResetDefault();
            TrayIcon.ResetThresholds();
            SyncTrayUi();
            RefreshTrayThresholdRow();
            Log.Info("托盘提示：已恢复默认显示项（版本 + 性能模式 + 风扇）");
        }

        // ────────────────────────────────────────────────────── 传感器
        /// <summary>窗口隐藏时，托盘若勾了 CPU 项就需要数据 —— 只采快照，不碰任何控件。</summary>
        public void RequestHiddenSensorSnapshot()
        {
            if (_sensorBusy) return;
            _sensorBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { TrayText.CaptureSensorText(Sensors.Render(Sensors.ReadAll())); }
                catch (Exception ex) { Log.Ex("隐藏态传感器快照失败", ex); }
                finally { _sensorBusy = false; }
            });
        }

        /// <summary>读取并渲染传感器（首页直接展示；窗口隐藏到托盘后跳过，省开销）。
        /// 读数放在线程池里做：首次读取要预热 PDH + 走 WMI，约 1~2 秒，
        /// 放 UI 线程上会把窗口卡住，所以读→回到 UI 线程渲染。</summary>
        public void RefreshSensors()
        {
            if (!Visible) return;
            if (_sensorBusy) return;
            if ((DateTime.Now - _lastSensorRefresh).TotalSeconds < 2) return;
            _lastSensorRefresh = DateTime.Now;
            _sensorBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Reading> list = null;
                string render = null;
                string err = null;
                double ms = 0;
                int ok = 0, fail = 0;
                try
                {
                    DateTime t0 = DateTime.Now;
                    list = Sensors.ReadAll();
                    ms = (DateTime.Now - t0).TotalMilliseconds;
                    render = Sensors.Render(list);
                    for (int i = 0; i < list.Count; i++) { if (list[i].Ok) ok++; else fail++; }
                }
                catch (Exception ex) { err = ex.Message; Log.Ex("读取传感器失败", ex); }

                if (ms > 400) Log.Warn("传感器：本轮读取耗时 " + ms.ToString("0", CultureInfo.InvariantCulture) + " ms（后台线程，不卡界面）");

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _sensorBusy = false;
                        if (err != null)
                        {
                            _lblSensorHint.ForeColor = Ui.Scald;
                            _lblSensorHint.Text = "读取失败：" + err + "（详见日志）";
                            return;
                        }
                        ApplyBig(_big[0], list, "CPU 温度", "温度", true);
                        ApplyBig(_big[1], list, "CPU 功耗", "功耗", false);
                        for (int i = 0; i < _rowNames.Count; i++)
                        {
                            Reading r = FindReading(list, _rowNames[i]);
                            if (r == null) { _rows[i].Set("—", "", false, 0); continue; }
                            if (!r.Ok)
                            {
                                // 不可用 → 值列只放 "—"，原因进 ToolTip（不再把"未实现"当数值渲染）
                                _rows[i].Set("—", "", false, 0);
                                _tips.SetToolTip(_rows[i].ValueLabel,
                                    r.Name + " 不可用" + (r.Note.Length > 0 ? "：" + r.Note : "") + "（" + r.Group + "）");
                                continue;
                            }
                            _rows[i].Set(Ui.Tidy(r.Value), true, LevelOf(r));
                            if (r.Note.Length > 0)
                                _tips.SetToolTip(_rows[i].ValueLabel, r.Note + "（" + r.Group + "）");
                        }
                        _lblEnv.Text = "读数 " + ok.ToString(CultureInfo.InvariantCulture) + " / "
                            + (ok + fail).ToString(CultureInfo.InvariantCulture) + " 项可用";
                        _lblSensorHint.ForeColor = Ui.Label;
                        _lblSensorHint.Text = fail > 0
                            ? "有 " + fail.ToString(CultureInfo.InvariantCulture) + " 项未实现（读数区显示为 —）"
                            : "";
                        TrayText.CaptureSensorText(render);
                    });
                }
                catch { _sensorBusy = false; }
            });
        }        private void OnSensorProbeClick(object sender, EventArgs e)
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                string text = Sensors.Probe();
                string file = Path.Combine(Log.Folder, "sensors-probe.txt");
                try
                {
                    if (!Directory.Exists(Log.Folder)) Directory.CreateDirectory(Log.Folder);
                    File.WriteAllText(file, text, new UTF8Encoding(false));
                    Log.Info("传感器探测结果已写入 " + file);
                }
                catch (Exception ex2) { Log.Ex("写入传感器探测结果失败", ex2); }
                RefreshSensors();
                if (MessageBox.Show(this,
                        "探测完成。本机可用的通道已生效，完整报告（含计数器集、实例、单位、判据）写在："
                        + Environment.NewLine + file + Environment.NewLine + Environment.NewLine + "现在打开看吗？",
                        "探测数据源", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    try { Process.Start("notepad.exe", "\"" + file + "\""); } catch { }
                }
            }
            catch (Exception ex) { Log.Ex("传感器探测失败", ex); Warn("探测失败：" + ex.Message); }
            finally { Cursor = Cursors.Default; }
        }

        private void OnOsdHintModeChanged(object sender, EventArgs e)
        {
            if (_suppress) return;
            _osdHintMode = _cmbOsdHint.SelectedIndex == 1 ? "always" : (_cmbOsdHint.SelectedIndex == 2 ? "off" : "auto");
            Settings.Set("osd_hint", _osdHintMode);
            Log.Info("屏幕提示模式：" + _osdHintMode + "（" + _cmbOsdHint.Text + "）");
            if (_osdHintMode != "off")
                OsdHint("屏幕提示 · " + _cmbOsdHint.Text, false);
        }

        private void OnIntervalChanged(object sender, EventArgs e)
        {
            if (_suppress) return;
            int[] secs = new int[] { 2, 3, 5, 10 };
            int idx = _cmbInterval.SelectedIndex;
            if (idx >= 0 && idx < secs.Length)
            {
                _timer.Interval = secs[idx] * 1000;
                Log.Info("自动刷新间隔改为 " + secs[idx] + " 秒");
            }
        }

        // ────────────────────────────────────────────────────── 交互
        private void OnModeClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            string name = (string)((Control)sender).Tag;
            int value = ModeMap.Value(name);
            try
            {
                Log.Info("切换性能模式 → " + name + "（写 fn=8 值=" + value + "）");
                Mifs.SetByte(Mifs.FnPerMode, (byte)value);
                Thread.Sleep(250);
                int? back = Mifs.GetByte(Mifs.FnPerMode);
                Log.Info("性能模式读回 = " + (back.HasValue ? ModeMap.Label(back.Value) : "读取失败"));
                RefreshAll();
                PrimeOsdWatch();
                if (back.HasValue && back.Value == value) ShowOsd("性能模式 · " + name);
                else if (back.HasValue) ShowOsd("性能模式 · " + ModeMap.Label(back.Value) + "（写入未生效）");
            }
            catch (Exception ex) { Log.Ex("切换性能模式失败", ex); Warn("切换失败：" + ex.Message); }
        }

        /// <summary>在 ms 毫秒内轮询风扇转速，返回采样到的最大值（用于判断"到底转没转起来"）。</summary>
        private int FanPeak(int ms, int fallback)
        {
            int peak = fallback;
            int spent = 0;
            while (spent < ms)
            {
                int[] f = Mifs.GetFans();
                if (f != null && f[0] > peak) peak = f[0];
                Thread.Sleep(400);
                Application.DoEvents();
                spent += 400;
            }
            return peak;
        }

        private void OnFanBoostClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            try
            {
                int[] f0 = Mifs.GetFans();
                _fanRpmBefore = (f0 != null && f0[0] > 0) ? f0[0] : -1;
                if (!_fanBoostUsable)
                {
                    Log.Info("风扇满速：按钮不可用（能力探测判定本机未实现）");
                    return;
                }
                byte target = (byte)(_fanBoostOn ? 0 : 1);
                Log.Info("风扇满速 → " + (target == 1 ? "开" : "关") + "（写 fn=20）");
                Mifs.SetFanBoost(target);
                Thread.Sleep(400);
                int? back = Mifs.GetByte(Mifs.FnMaxFanSwitch);
                Log.Info("风扇满速读回 = " + (back.HasValue ? back.Value.ToString(CultureInfo.InvariantCulture) : "读取失败"));

                if (back.HasValue && back.Value != target)
                {
                    // 寄存器读回没变 —— 但 EC 可能"不更新寄存器镜像却照做"，真正的判据是风扇转速。
                    // 实测：开 4 秒看转速有没有起来，再决定是不是真的不支持。
                    int before = _fanRpmBefore;
                    Log.Info("风扇满速：读回值没变，改用转速验证（基线 " + before + " RPM，采样 4 秒）");
                    _btnBoost.Text = "风扇满速：验证中…";
                    Application.DoEvents();
                    int after = Mifs.FanPeak(4000, before);

                    if (target == 1 && after - before >= 250)
                    {
                        Caps.FanBoost = true;
                        Caps.FanBoostAcType = _acTypeNow;
                        Caps.Save();
                        Log.Info("风扇满速：转速 " + before + " → " + after + " RPM → 判定可用（寄存器读回不跟随）");
                        RefreshAll();
                        PrimeOsdWatch();
                        ShowOsd("风扇满速 · 开");
                        Warn("风扇确实加速了：" + before + " → " + after + " RPM。"
                            + "\r\n\r\n这台的 EC 不把状态写回寄存器（读回恒为 0），但动作生效 —— "
                            + "所以之前用「读回值」判定「未实现」是错的，已改为按转速判定。");
                        return;
                    }

                    Caps.FanBoost = false;
                    Caps.FanBoostAcType = _acTypeNow;
                    Caps.Save();
                    Log.Warn("风扇满速：读回没变且转速没起来（" + before + " → " + after + " RPM）→ 不可用（供电="
                        + Mifs.AcTypeName(_acTypeNow) + "）");
                    RefreshAll();
                    Warn("EC 没有执行这次写入：转速几乎没变（" + before + " → " + after + " RPM），寄存器读回也没变。"
                        + "\r\n\r\n当前供电：" + Mifs.AcTypeName(_acTypeNow)
                        + "\r\n上游驱动文档说该状态（电池 / Type-C）下满速被硬件禁用；"
                        + "\r\n如果这台机器只有 USB-C 供电口，那 MIFS 这条路走不通，"
                        + "\r\n只能走 EC RAM（需要 PawnIO 这类签名驱动），见 docs/FAN-CONTROL.md。");
                    return;
                }
                if (!Caps.ValidFor(_acTypeNow)) { Caps.FanBoost = true; Caps.FanBoostAcType = _acTypeNow; Caps.Save(); }
                RefreshAll();
                PrimeOsdWatch();
                ShowOsd("风扇满速 · " + (target == 1 ? "开" : "关"));
            }
            catch (Exception ex) { Log.Ex("设置风扇满速失败", ex); Warn("设置失败：" + ex.Message); }
        }

        private void OnFnLockClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            try
            {
                Log.Info("Fn 锁 → " + (_chkFn.Checked ? "开" : "关"));
                Mifs.SetByte(Mifs.FnFnLock, (byte)(_chkFn.Checked ? 1 : 0));
                Thread.Sleep(200); RefreshAll(); PrimeOsdWatch();
                ShowOsd("Fn 锁 · " + (_chkFn.Checked ? "开" : "关"));
            }
            catch (Exception ex) { Log.Ex("Fn 锁设置失败", ex); Warn("Fn 锁设置失败：" + ex.Message); }
        }

        private void OnTpLockClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            try
            {
                Log.Info("触控板锁 → " + (_chkTp.Checked ? "锁定" : "正常"));
                Mifs.SetByte(Mifs.FnTpLock, (byte)(_chkTp.Checked ? 1 : 0));
                Thread.Sleep(200); RefreshAll(); PrimeOsdWatch();
                ShowOsd("触控板 · " + (_chkTp.Checked ? "已锁定" : "正常"));
            }
            catch (Exception ex) { Log.Ex("触控板锁设置失败", ex); Warn("触控板锁设置失败：" + ex.Message); }
        }

        private void OnKbdClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            try
            {
                int lvl = (int)((Control)sender).Tag;
                Log.Info("键盘背光 → 等级 " + lvl);
                Mifs.SetByte(Mifs.FnRgbBright, (byte)lvl);
                Thread.Sleep(150); RefreshAll(); PrimeOsdWatch();
                ShowOsd("键盘背光 · 等级 " + lvl);
            }
            catch (Exception ex) { Log.Ex("背光设置失败", ex); Warn("背光设置失败：" + ex.Message); }
        }

        private void OnStartupClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            bool want = _chkStartup.Checked;
            Log.Info("开机自启 → " + (want ? "开" : "关") + "（计划任务 " + Startup.TaskName + "）");
            Cursor = Cursors.WaitCursor;
            try
            {
                bool ok = Startup.Set(want);
                if (!ok)
                {
                    Warn("开机自启设置失败，详见日志：\r\n" + Log.FilePath);
                }
                RefreshStartupState();
                ShowOsd("开机自启 · " + (_chkStartup.Checked ? "开" : "关"));
            }
            catch (Exception ex) { Log.Ex("设置开机自启失败", ex); Warn("设置开机自启失败：" + ex.Message); }
            finally { Cursor = Cursors.Default; }
        }

        private void OnOsdRestartClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            if (MessageBox.Show(this,
                    "将依次执行：\r\n  1) 停止并启动服务 " + Osd.ServiceName + "\r\n  2) 结束并重新启动 " + Osd.UtilityExe + "\r\n\r\n" +
                    "OSD 界面进程只会被重启，不改动任何系统设置。继续？",
                    "重启 OSD", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                Log.Info("用户取消了 OSD 重启");
                return;
            }
            Cursor = Cursors.WaitCursor;
            try
            {
                Log.Info("开始重启 OSD");
                string result = Osd.Restart();
                _lastOsdCheck = DateTime.MinValue;
                RefreshAll();
                Log.Info("OSD 重启完成：" + result.Replace("\r\n", " / "));
                MessageBox.Show(this, result + "\r\n\r\n详情见日志：\r\n" + Log.FilePath,
                    "重启 OSD", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Log.Ex("重启 OSD 失败", ex); Warn("重启 OSD 失败：" + ex.Message); }
            finally { Cursor = Cursors.Default; }
        }

        private void OnDpiFixClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            bool want = _chkDpi.Checked;
            if (!OsdDpi.FileExists)
            {
                Warn("找不到 " + OsdDpi.ExePath + "，官方 OSD 组件没装，无法应用这个修复。");
                RefreshDpiState();
                return;
            }
            string tip = want
                ? "将写入注册表（需要管理员，可随时撤销）：\r\n  HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers\r\n  "
                  + OsdDpi.ExePath + " = " + OsdDpi.FlagValue + "\r\n\r\n作用：让 DPI 不感知的 OSD 界面进程按系统 DPI 渲染。"
                  + "\r\n\r\n已观察到本机缩放为 125%，而该进程按 96 DPI 工作 —— 这是它画不出提示的头号嫌疑。\r\n继续？"
                : "将删除上面那条注册表值，把 OSD 恢复原样。继续？";
            if (MessageBox.Show(this, tip, want ? "应用 DPI 兼容修复" : "撤销 DPI 兼容修复",
                    MessageBoxButtons.OKCancel, want ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK)
            {
                Log.Info("用户取消了 DPI 兼容修复操作");
                RefreshDpiState();
                return;
            }
            Cursor = Cursors.WaitCursor;
            try
            {
                string result = OsdDpi.Apply(want);
                RefreshDpiState();
                MessageBox.Show(this,
                    result + "\r\n\r\n下一步：在管理员 PowerShell 里重启 OSD 让新设置生效（或点「重启 OSD」按钮），"
                    + "然后按一次 Fn 组合键看提示是否出现。\r\n\r\n日志：" + Log.FilePath,
                    "DPI 兼容修复", MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (want) ShowOsd("OSD DPI 兼容修复 · 已写入");
            }
            catch (Exception ex) { Log.Ex("DPI 兼容修复失败", ex); Warn("操作失败：" + ex.Message); }
            finally { Cursor = Cursors.Default; }
        }

        private void RefreshDpiState()
        {
            bool prev = _suppress;
            _suppress = true;
            try
            {
                bool on = OsdDpi.IsEnabled();
                _chkDpi.Checked = on;
                _chkDpi.Enabled = OsdDpi.FileExists;
                _lblDpi.Text = !OsdDpi.FileExists ? "（未安装官方 OSD）"
                    : (on ? "已应用 ~ HIGHDPIAWARE" : "未应用");
                _lblDpi.ForeColor = on ? Ui.Cold : Ui.Muted;
            }
            catch (Exception ex) { Log.Ex("刷新 DPI 修复状态失败", ex); }
            _suppress = prev;
        }

        private void OnOsdDiagClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                Log.Info("开始 OSD 诊断");
                Osd.State st = Osd.Query();
                string text = Osd.Diagnose();
                string file = Path.Combine(Log.Folder, "osd-diagnose.txt");
                // 首页不再放文本转储：只把结论塞进状态行，完整报告写文件
            Log.Info("OSD 诊断：" + (Osd.Healthy(st) ? "服务与进程正常，见报告" : "异常，见报告"));
                MessageBox.Show(this,
                    "诊断完成。" + Environment.NewLine + Environment.NewLine
                    + "服务：" + st.ServiceState + "    界面进程：" + st.UtilityCount + " 个" + Environment.NewLine
                    + "完整证据（事件日志、心跳时间范围、显示环境、系统 DPI）已写入：" + Environment.NewLine + file,
                    "诊断 OSD", MessageBoxButtons.OK, MessageBoxIcon.Information);
                try { Process.Start("notepad.exe", "\"" + file + "\""); } catch { }
            }
            catch (Exception ex) { Log.Ex("OSD 诊断失败", ex); Warn("OSD 诊断失败：" + ex.Message); }
            finally { Cursor = Cursors.Default; }
        }

        private void OnOpenOsdDirClick(object sender, EventArgs e)
        {
            try
            {
                string dir = Directory.Exists(Osd.InstallDir) ? Osd.InstallDir : Log.Folder;
                Log.Info("打开目录：" + dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { Log.Ex("打开目录失败", ex); }
        }

        private void OnRecapClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            Log.Info("用户点击重测功能：清空能力缓存并重新探测");
            Caps.Reset();
            _lastLoggedError = "";
            RefreshAll();
            MessageBox.Show(this,
                "能力缓存已清空。下次点击「风扇满速」时会重新实测（写入后读回校验）。\r\n\r\n日志：" + Log.FilePath,
                "重测功能", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Warn(string msg)
        {
            MessageBox.Show(this, msg, "OpenMIFS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ────────────────────────────────────────────────────── 刷新
        private void RefreshStartupState()
        {
            try
            {
                bool on = Startup.IsEnabled();
                bool prev = _suppress;
                _suppress = true;
                _chkStartup.Checked = on;
                _chkStartup.Text = on ? "开机自启（已启用，静默进托盘）" : "开机自启（登录时静默进托盘）";
                _suppress = prev;
            }
            catch (Exception ex) { Log.Ex("查询开机自启状态失败", ex); }
        }

        private void RefreshOsdState(bool force)
        {
            if (!force && (DateTime.Now - _lastOsdCheck).TotalSeconds < 8) return;
            _lastOsdCheck = DateTime.Now;
            Osd.State s = Osd.Query();
            _osdOk = Osd.Healthy(s);
            _osdStatus = _osdOk ? "正常" : Osd.StatusLine(s);
            RefreshDpiState();
        }

        public void RefreshAll()
        {
            _suppress = true;
            StringBuilder snap = new StringBuilder();     // 只喂托盘快照，界面渲染全部走结构化控件
            bool anyOk = false;
            List<string> unavailable = new List<string>();

            // ── 性能模式（回读贴在分段控件旁；镜像状态面板已取消）
            int? pm = Mifs.GetByte(Mifs.FnPerMode);
            if (pm.HasValue)
            {
                anyOk = true;
                int mv = pm.Value;
                for (int i = 0; i < _btnMode.Length; i++)
                {
                    bool cur = ModeMap.Value(ModeMap.Order[i]) == mv;
                    _btnMode[i].Enabled = true;
                    _btnMode[i].Selected = cur;
                }
                _lblModeNote.ForeColor = Ui.Label;
                _lblModeNote.Text = "已生效：" + ModeMap.Label(mv);
                _lblBadgeMode.Text = ModeMap.Label(mv);
                _lblBadgeMode.ForeColor = Ui.Ink;
                snap.AppendLine("性能模式 : " + ModeMap.Label(mv));
            }
            else
            {
                for (int i = 0; i < _btnMode.Length; i++) { _btnMode[i].Enabled = false; _btnMode[i].Selected = false; }
                _lblModeNote.ForeColor = Ui.Muted;
                _lblModeNote.Text = "不可用：接口未响应";
                _lblBadgeMode.Text = "模式不可用";
                _lblBadgeMode.ForeColor = Ui.Muted;
                unavailable.Add("性能模式");
                snap.AppendLine("性能模式 : 未实现");
            }

            // ── 风扇转速：全场**只读这一次**（以前在三处显示、两个采样时刻，数值还会互相打架）
            int[] fans = Mifs.GetFans();
            if (fans != null)
            {
                anyOk = true;
                string cap = "风扇1";
                if (fans[1] > 0) cap = "风扇1（风扇2 " + fans[1].ToString(CultureInfo.InvariantCulture) + "）";
                _big[2].Set(fans[0].ToString(CultureInfo.InvariantCulture), "RPM", cap, 0, true);
                snap.AppendLine("风扇1        : " + fans[0].ToString(CultureInfo.InvariantCulture) + " RPM");
                if (fans[1] > 0) snap.AppendLine("风扇2        : " + fans[1].ToString(CultureInfo.InvariantCulture) + " RPM");
            }
            else
            {
                _big[2].Set("", "", "风扇", 0, false);
                unavailable.Add("风扇转速");
                snap.AppendLine("风扇转速     : 未实现");
            }

            // ── 供电（徽标）
            int acType = Mifs.GetByte(Mifs.FnAcType) ?? -1;
            _acTypeNow = acType;
            _lblBadgeAc.Text = acType >= 0 ? Ui.AcText(acType) : "供电未知";
            _lblBadgeAc.ForeColor = acType >= 0 ? Ui.Label : Ui.Muted;

            // ── 风扇满速：按钮文案短；受限原因就地写在按钮下方（N3）
            bool boostReadable = false;
            int? mxs = Mifs.GetByte(Mifs.FnMaxFanSwitch);
            if (mxs.HasValue) { boostReadable = true; _fanBoostOn = mxs.Value == 1; }
            bool capsValid = Caps.ValidFor(acType);
            if (!capsValid)
            {
                _fanBoostUsable = boostReadable;
                _btnBoost.Enabled = boostReadable;
                _btnBoost.Selected = _fanBoostOn;
                _btnBoost.Text = "风扇满速" + (boostReadable ? " · " + (_fanBoostOn ? "开" : "关") : "");
                _lblBoostNote.ForeColor = Ui.Label;
                _lblBoostNote.Text = boostReadable ? "尚未实测：点一次即按转速判定" : "不可用";
            }
            else if (Caps.FanBoost.Value && boostReadable)
            {
                _fanBoostUsable = true;
                _btnBoost.Enabled = true;
                _btnBoost.Selected = _fanBoostOn;
                _btnBoost.Text = "风扇满速 · " + (_fanBoostOn ? "开" : "关");
                _lblBoostNote.ForeColor = Ui.Label;
                _lblBoostNote.Text = "已实测可用";
            }
            else
            {
                _fanBoostUsable = false;
                _btnBoost.Enabled = false;
                _btnBoost.Selected = false;
                _btnBoost.Text = "风扇满速";
                _lblBoostNote.ForeColor = Ui.Warm;
                _lblBoostNote.Text = acType == 1
                    ? "受 Type-C 供电限制（插圆口电源可解锁）"
                    : "本机 EC 忽略该写入";
            }

            // ── 硬件开关（复选框本身就是回读）
            int? fn = Mifs.GetByte(Mifs.FnFnLock);
            if (fn.HasValue) { anyOk = true; _chkFn.Enabled = true; _chkFn.Checked = fn.Value == 1; }
            else { _chkFn.Enabled = false; _chkFn.Checked = false; unavailable.Add("Fn 锁"); }

            int? tp = Mifs.GetByte(Mifs.FnTpLock);
            if (tp.HasValue) { anyOk = true; _chkTp.Enabled = true; _chkTp.Checked = tp.Value == 1; }
            else { _chkTp.Enabled = false; _chkTp.Checked = false; unavailable.Add("触控板锁"); }

            // ── 键盘背光
            int? kbd = Mifs.GetByte(Mifs.FnRgbBright);
            for (int i = 0; i < _btnKbd.Length; i++)
            {
                if (kbd.HasValue) { _btnKbd[i].Enabled = true; _btnKbd[i].Selected = kbd.Value == i; }
                else { _btnKbd[i].Enabled = false; _btnKbd[i].Selected = false; }
            }
            if (kbd.HasValue)
            {
                anyOk = true;
                _lblKbdNote.ForeColor = Ui.Label;
                _lblKbdNote.Text = "当前等级 " + kbd.Value.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                _lblKbdNote.ForeColor = Ui.Muted;
                _lblKbdNote.Text = "不可用";
                unavailable.Add("键盘背光");
            }

            // ── MIFS 功能号 22/23（恒为 0，只探测一次写日志；不计入"不可用"，因为面板温度走 AMD 通道）
            if (!_mifsTempPowerChecked)
            {
                _mifsTempPowerChecked = true;
                int? ct = Mifs.GetByte(Mifs.FnCpuTemp);
                int? cp = Mifs.GetByte(Mifs.FnCpuPower);
                bool ctOk = ct.HasValue && ct.Value > 0;
                bool cpOk = cp.HasValue && cp.Value > 0;
                Log.Info("MIFS 功能号 22/23（CPU 温度/功率）："
                    + (ct.HasValue ? ct.Value.ToString(CultureInfo.InvariantCulture) : "读取失败") + " / "
                    + (cp.HasValue ? cp.Value.ToString(CultureInfo.InvariantCulture) : "读取失败")
                    + (ctOk && cpOk ? " → 可用" : " → 该功能号在本机未实现；面板改用 AMD 通道（ADL PMlog）+ PDH"));
            }

            // ── OSD 徽标（N14：正常时只说"正常"）
            RefreshOsdState(false);
            _lblBadgeOsd.Text = "OSD " + _osdStatus;
            _lblBadgeOsd.ForeColor = _osdOk ? Ui.Cold : Ui.Warm;

            // ── 开机自启（复选框即回读）
            if ((DateTime.Now - _lastStartupCheck).TotalSeconds > 20)
            {
                _lastStartupCheck = DateTime.Now;
                RefreshStartupState();
            }

            // ── 状态条右侧：时间；未提权时给一键提权重启
            _lblClock.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            _btnElevate.Visible = !MifsApp.IsElevated;
            if (!anyOk)
            {
                _lblBadgeMode.Text = "接口不可用";
                _lblBadgeMode.ForeColor = Ui.Scald;
            }

            // ── 不可用清单（只在真的缺功能时出现）
            if (unavailable.Count == 0) _lblUnavail.Text = "";
            else
            {
                string miss = string.Join("、", unavailable.ToArray());
                if (miss.Length > 40) miss = miss.Substring(0, 40) + "…";
                _lblUnavail.Text = "不可用：" + miss;
            }

            TrayText.CaptureStatusText(snap.ToString());
            RefreshTrayPreview();
            _suppress = false;

            if (!_firstRefreshDone)
            {
                _firstRefreshDone = true;
                Log.Info("首次刷新完成：接口可用=" + anyOk + " 不可用项=" + (unavailable.Count == 0 ? "无" : string.Join("、", unavailable.ToArray()))
                    + " OSD=" + _osdStatus);
            }
            RefreshSensors();
        }
        private DateTime _lastStartupCheck = DateTime.MinValue;
    }

    // ─────────────────────────────────────────────────────────── 托盘宿主
    // ───────────────────────────────────────────── 托盘悬停提示（白名单 + 最坏情况预算）
    /// <summary>托盘悬停提示是 Win32 单行字符串，.NET 的 NotifyIcon.Text 上限 63 字符。
    /// 所以这里**不提供自由模板**，只允许一组"最坏情况下也放得下"的项目：
    /// 每项登记一个 Worst（该值可能达到的最大宽度），勾选时按预算逐个放行，
    /// 放不下的项直接拒绝勾选 —— 任何组合都不会溢出，也就不需要对数字做截断。
    /// 想看的项放不下时，用右键菜单里的详细摘要（无长度限制）看。</summary>
    internal static class TrayText
    {
        public const int MaxChars = 62;          // NotifyIcon.Text 上限 63，留 1 位余量
        public const string Separator = " · ";

        internal sealed class Item
        {
            public string Id;
            public string Label;
            public int Worst;                    // 最坏宽度（含前缀与单位）
            public string Sample;
        }

        // 只放"单项短、组合可控"的项目；会超长的（内存/磁盘温度/GPU 等）一律不提供
        public static readonly Item[] All = new Item[]
        {
            new Item { Id = "ver",  Label = "版本",     Worst = 8,  Sample = "v0.4.5" },
            new Item { Id = "mode", Label = "性能模式", Worst = 3,  Sample = "低功耗" },
            new Item { Id = "fan",  Label = "风扇转速", Worst = 17, Sample = "风扇 65535/65535" },
            new Item { Id = "cput", Label = "CPU 温度", Worst = 7,  Sample = "CPU 105℃" },
            new Item { Id = "cpup", Label = "CPU 功耗", Worst = 9,  Sample = "CPU 105.5W" },
            new Item { Id = "cpuf", Label = "CPU 频率", Worst = 9,  Sample = "CPU 5.55G" },
            new Item { Id = "cpul", Label = "CPU 负载", Worst = 7,  Sample = "CPU 100%" },
            new Item { Id = "bat",  Label = "电池电量", Worst = 7,  Sample = "电池 100%" },
        };

        // 值快照：主窗体刷新时写入，托盘只读（避免两边重复采集）
        private static readonly Dictionary<string, string> _snap = new Dictionary<string, string>();
        public static DateTime SnapTime = DateTime.MinValue;      // 任何快照更新（状态或传感器）
        public static DateTime SensorTime = DateTime.MinValue;    // 仅传感器快照更新（隐藏态按需采集用它做节流）

        private static readonly List<string> _selected = new List<string> { "ver", "mode", "fan" };

        public static Item Find(string id)
        {
            for (int i = 0; i < All.Length; i++) if (All[i].Id == id) return All[i];
            return null;
        }

        /// <summary>已选项目，按 All 的固定顺序（顺序不可调，换取"永不溢出"的保证）。</summary>
        public static List<string> Selected()
        {
            List<string> r = new List<string>();
            for (int i = 0; i < All.Length; i++) if (_selected.Contains(All[i].Id)) r.Add(All[i].Id);
            return r;
        }

        public static bool IsSelected(string id) { return _selected.Contains(id); }

        /// <summary>当前勾选项里是否有依赖传感器采集的（CPU 温度/功耗/频率/负载、电池）。</summary>
        public static bool NeedsSensors()
        {
            return IsSelected("cput") || IsSelected("cpup") || IsSelected("cpuf")
                || IsSelected("cpul") || IsSelected("bat");
        }

        /// <summary>这些项目的**最坏情况**总宽（含分隔符）。</summary>
        public static int WorstTotal(List<string> ids)
        {
            if (ids == null || ids.Count == 0) return 0;
            int n = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                Item it = Find(ids[i]);
                if (it != null) n += it.Worst;
            }
            return n + (ids.Count - 1) * Separator.Length;
        }

        public static int WorstTotal() { return WorstTotal(Selected()); }

        /// <summary>加入这一项后是否仍在预算内（界面上用来拒绝勾选）。</summary>
        public static bool CanAdd(string id)
        {
            if (IsSelected(id)) return true;
            List<string> t = Selected();
            t.Add(id);
            return WorstTotal(t) <= MaxChars;
        }

        public static bool TrySelect(string id, bool on)
        {
            if (on)
            {
                if (!CanAdd(id)) return false;
                if (!_selected.Contains(id)) _selected.Add(id);
            }
            else _selected.Remove(id);
            Save();
            return true;
        }

        public static void ResetDefault()
        {
            _selected.Clear();
            _selected.Add("ver"); _selected.Add("mode"); _selected.Add("fan");
            Save();
        }

        public static void Load()
        {
            try
            {
                string s = Settings.Get("tray_items", "");
                if (s.Length == 0) return;
                List<string> ids = new List<string>();
                string[] parts = s.Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    string id = parts[i].Trim().ToLowerInvariant();
                    if (id.Length == 0) continue;
                    if (Find(id) == null) { Log.Warn("托盘提示：忽略未知项目 " + id); continue; }
                    if (!ids.Contains(id)) ids.Add(id);
                }
                if (WorstTotal(ids) > MaxChars)
                {
                    Log.Warn("托盘提示：配置的组合最坏 " + WorstTotal(ids).ToString(CultureInfo.InvariantCulture)
                        + " 字符，超过 " + MaxChars.ToString(CultureInfo.InvariantCulture) + " → 回落默认");
                    return;
                }
                _selected.Clear();
                _selected.AddRange(ids);
            }
            catch (Exception ex) { Log.Ex("读取托盘提示配置失败", ex); }
        }

        public static void Save()
        {
            try { Settings.Set("tray_items", string.Join(",", Selected().ToArray())); }
            catch (Exception ex) { Log.Ex("保存托盘提示配置失败", ex); }
        }

        // ── 值快照的写入（主窗体调用）
        private static void Put(string id, string v)
        {
            if (string.IsNullOrEmpty(v)) { _snap.Remove(id); return; }
            Item it = Find(id);
            if (it != null && v.Length > it.Worst) v = v.Substring(0, it.Worst);   // 兜底，防意外超宽
            _snap[id] = v;
        }

        private static bool Bad(string v)
        {
            return string.IsNullOrEmpty(v) || v.StartsWith("未实现") || v.StartsWith("不支持")
                || v.StartsWith("需要管理员") || v == "—" || v == "未知" || v.StartsWith("读取失败");
        }

        /// <summary>解析"键 : 值"文本（状态面板 / 传感器面板通用）。</summary>
        private static Dictionary<string, string> Parse(string text)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(text)) return d;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string t = raw.Trim();
                if (t.Length == 0 || t.StartsWith("==") || t.StartsWith("└")) continue;
                int c = raw.IndexOf(':');
                if (c <= 0) continue;
                string k = raw.Substring(0, c).Trim();
                string v = raw.Substring(c + 1).Trim();
                if (k.Length > 0) d[k] = v;
            }
            return d;
        }

        private static double Num(string v)
        {
            double d;
            if (double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return double.NaN;
        }

        /// <summary>状态面板文本 → 版本 / 模式 / 风扇。</summary>
        public static void CaptureStatusText(string text)
        {
            try
            {
                Dictionary<string, string> d = Parse(text);
                Put("ver", "v" + MifsApp.VersionText);

                string mode;
                if (d.TryGetValue("性能模式", out mode) && !Bad(mode)) Put("mode", mode); else Put("mode", null);

                string f1, f2;
                d.TryGetValue("风扇1", out f1);
                d.TryGetValue("风扇2", out f2);
                double n1 = Num((f1 ?? "").Replace("RPM", "")), n2 = Num((f2 ?? "").Replace("RPM", ""));
                if (!double.IsNaN(n1) && n1 > 0 && !double.IsNaN(n2) && n2 > 0)
                    Put("fan", "风扇 " + n1.ToString("0", CultureInfo.InvariantCulture) + "/" + n2.ToString("0", CultureInfo.InvariantCulture));
                else Put("fan", null);

                SnapTime = DateTime.Now;
            }
            catch (Exception ex) { Log.Ex("托盘快照（状态）失败", ex); }
        }

        /// <summary>传感器面板文本 → CPU 温度/功耗/频率/负载 + 电池（都压缩成短写法）。</summary>
        public static void CaptureSensorText(string text)
        {
            try
            {
                Dictionary<string, string> d = Parse(text);

                string v;
                if (d.TryGetValue("CPU 温度", out v) && !Bad(v))
                    Put("cput", "CPU " + v.Replace(" ", ""));
                else Put("cput", null);

                if (d.TryGetValue("CPU 功耗", out v) && !Bad(v))
                {
                    double w = Num(v.Replace("W", ""));
                    Put("cpup", double.IsNaN(w) ? null : "CPU " + w.ToString("0.0", CultureInfo.InvariantCulture) + "W");
                }
                else Put("cpup", null);

                if (d.TryGetValue("CPU 频率", out v) && !Bad(v))
                {
                    double g = Num(v.Replace("GHz", "").Replace("MHz", ""));
                    Put("cpuf", double.IsNaN(g) ? null : "CPU " + g.ToString("0.00", CultureInfo.InvariantCulture) + "G");
                }
                else Put("cpuf", null);

                if (d.TryGetValue("CPU 负载", out v) && !Bad(v))
                {
                    double p = Num(v.Replace("%", ""));
                    Put("cpul", double.IsNaN(p) ? null : "CPU " + p.ToString("0", CultureInfo.InvariantCulture) + "%");
                }
                else Put("cpul", null);

                if (d.TryGetValue("电池", out v) && !Bad(v))
                {
                    string head = v.Split('·')[0].Replace(" ", "");
                    Put("bat", head.Length > 0 ? "电池 " + head : null);
                }
                else Put("bat", null);

                SnapTime = DateTime.Now;
                SensorTime = DateTime.Now;
            }
            catch (Exception ex) { Log.Ex("托盘快照（传感器）失败", ex); }
        }

        /// <summary>拼出悬停提示。已被最坏情况预算保证不溢出；这里再做一次稳妥的整项丢弃兜底。</summary>
        public static string Build()
        {
            List<string> parts = new List<string>();
            List<string> ids = Selected();
            for (int i = 0; i < ids.Count; i++)
            {
                string v;
                if (_snap.TryGetValue(ids[i], out v) && !string.IsNullOrEmpty(v)) parts.Add(v);
            }
            string s = string.Join(Separator, parts.ToArray());
            while (s.Length > MaxChars && parts.Count > 1)
            {
                parts.RemoveAt(parts.Count - 1);          // 整项丢弃，不切数字
                s = string.Join(Separator, parts.ToArray());
            }
            if (s.Length > MaxChars && parts.Count == 1) s = parts[0].Substring(0, MaxChars);
            return s;
        }

        /// <summary>当前各项目的实际值（界面预览用）。</summary>
        public static string PreviewFor(string id)
        {
            string v;
            return _snap.TryGetValue(id, out v) ? v : "";
        }
    }

    // ───────────────────────────────────────────────── 托盘图标上画数值（动态图标）
    /// <summary>把当前值画进托盘图标：整块填色 + 2~3 个字符。
    /// 16×16 的物理极限就是 2~3 个字符，所以只提供短数值（温度/功耗/负载），
    /// 风扇 RPM（4 位数）不适合，故不提供。
    /// 注意：GDI 句柄必须释放 —— Icon.FromHandle 不接管所有权，
    /// 旧 Icon 与本轮 Bitmap 都要 Dispose，否则每次刷新泄漏一个 GDI 对象。</summary>
    internal static class TrayIcon
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        private const int SM_CXSMICON = 49;

        /// <summary>托盘小图标的**物理**像素尺寸。用 GetDpiForSystem 自己算，
        /// 不依赖进程的 DPI 感知状态（否则 125% 缩放下会按 16px 画再被系统拉伸，字就糊了）。</summary>
        private static int IconSize()
        {
            int a = 16, b = 16;
            try
            {
                uint dpi = GetDpiForSystem();
                if (dpi >= 96 && dpi <= 480) a = (int)Math.Round(16.0 * dpi / 96.0);
            }
            catch { }
            try { b = GetSystemMetrics(SM_CXSMICON); } catch { b = 16; }

            // 两个来源取较大者；再兜一个下限 20。
            // 理由：托盘实际尺寸若比我们画的大，系统会**放大**（明显糊）；比我们画的小则是**缩小**（只轻微软）。
            // 实测本机 GetDpiForSystem 返回 96（注册表 AppliedDPI=120），只信一个来源会画成 16px。
            int size = Math.Max(a, b);
            if (size < 20) size = 20;
            if (size > 32) size = 32;
            return size;
        }

        /// <summary>可选数据源（id 与 TrayText 的项目 id 一致；none = 保持程序图标）。</summary>
        public static readonly string[] Kinds = new string[] { "none", "cput", "cpup", "cpul" };
        public static readonly string[] KindLabels = new string[] { "无（程序图标）", "CPU 温度", "CPU 功耗", "CPU 负载" };

        // 三档阈值可配置（settings.txt 里的 tray_icon_t / _p / _l，逗号分隔三个升序数值）
        private static double[] _tTemp = new double[] { 55, 70, 85 };
        private static double[] _tPower = new double[] { 15, 30, 45 };
        private static double[] _tLoad = new double[] { 25, 50, 80 };
        public static readonly double[] DefaultTemp = new double[] { 55, 70, 85 };
        public static readonly double[] DefaultPower = new double[] { 15, 30, 45 };
        public static readonly double[] DefaultLoad = new double[] { 25, 50, 80 };

        private static string _kind = "cput";
        private static string _lastKey = "";
        private static bool _loggedFirst;
        private static int _lastLoggedLevel = -1;
        private static bool _loggedNoData;
        private static Icon _ownIcon;          // 自己创建的图标，换新时释放

        public static string Kind { get { return _kind; } }

        public static void Load()
        {
            try
            {
                string s = Settings.Get("tray_icon", "cput").Trim().ToLowerInvariant();
                for (int i = 0; i < Kinds.Length; i++) if (Kinds[i] == s) { _kind = s; return; }
                Log.Warn("托盘图标：未知取值 " + s + " → 回落 cput");
                _kind = "cput";
            }
            catch (Exception ex) { Log.Ex("读取托盘图标配置失败", ex); }
        }

        /// <summary>读三档阈值。格式："a,b,c"（三个升序正数）；不合法则保留原值并记日志。</summary>
        private static double[] ParseThresholds(string key, double[] fallback)
        {
            try
            {
                string s = Settings.Get(key, "");
                if (s.Trim().Length == 0) return fallback;
                string[] parts = s.Split(new char[] { ',' });
                if (parts.Length != 3)
                {
                    Log.Warn("托盘图标：阈值 " + key + " 需要 3 个数，收到 " + parts.Length + " 个 → 回落默认 " + Text(fallback) + "（已写回）");
                    try { Settings.Set(key, Text(fallback)); } catch { }
                    return fallback;
                }
                double[] r = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r[i]) || r[i] <= 0)
                    {
                        Log.Warn("托盘图标：阈值 " + key + " 含非法数值「" + parts[i].Trim() + "」→ 回落默认 " + Text(fallback) + "（已写回）");
                        try { Settings.Set(key, Text(fallback)); } catch { }
                        return fallback;
                    }
                }
                if (!(r[0] < r[1] && r[1] < r[2]))
                {
                    Log.Warn("托盘图标：阈值 " + key + " 必须严格递增（" + s.Trim() + "）→ 回落默认 " + Text(fallback) + "（已写回）");
                    try { Settings.Set(key, Text(fallback)); } catch { }
                    return fallback;
                }
                return r;
            }
            catch (Exception ex) { Log.Ex("读取托盘图标阈值失败 " + key, ex); return fallback; }
        }

        public static void LoadThresholds()
        {
            _tTemp = ParseThresholds("tray_icon_t", DefaultTemp);
            _tPower = ParseThresholds("tray_icon_p", DefaultPower);
            _tLoad = ParseThresholds("tray_icon_l", DefaultLoad);
            Log.Info("托盘图标：变色阈值 温度=" + Text(_tTemp) + " 功耗=" + Text(_tPower) + " 负载=" + Text(_tLoad));
        }

        private static string Text(double[] a)
        {
            return a[0].ToString("0.##", CultureInfo.InvariantCulture) + ","
                 + a[1].ToString("0.##", CultureInfo.InvariantCulture) + ","
                 + a[2].ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>指标短名（界面标签用）。</summary>
        public static string KindShort(string kind)
        {
            return kind == "cput" ? "温度" : (kind == "cpup" ? "功耗" : (kind == "cpul" ? "负载" : "—"));
        }

        /// <summary>把某个指标恢复成默认阈值。</summary>
        public static void ResetThresholdFor(string kind)
        {
            if (kind == "cput") _tTemp = new double[] { DefaultTemp[0], DefaultTemp[1], DefaultTemp[2] };
            else if (kind == "cpup") _tPower = new double[] { DefaultPower[0], DefaultPower[1], DefaultPower[2] };
            else if (kind == "cpul") _tLoad = new double[] { DefaultLoad[0], DefaultLoad[1], DefaultLoad[2] };
            else return;
            try { Settings.Set(kind == "cput" ? "tray_icon_t" : (kind == "cpup" ? "tray_icon_p" : "tray_icon_l"), ThresholdText(kind)); }
            catch (Exception ex) { Log.Ex("保存默认阈值失败", ex); }
        }

        /// <summary>取某个指标的当前阈值（副本，供界面生成动态说明）。</summary>
        public static double[] ThresholdsOf(string kind)
        {
            double[] src = kind == "cput" ? _tTemp : (kind == "cpup" ? _tPower : _tLoad);
            return new double[] { src[0], src[1], src[2] };
        }

        /// <summary>某个指标的当前阈值文本（界面显示用）。</summary>
        public static string ThresholdText(string kind)
        {
            if (kind == "cput") return Text(_tTemp);
            if (kind == "cpup") return Text(_tPower);
            return Text(_tLoad);
        }

        /// <summary>保存某个指标的阈值；不合法返回 false（界面据此回滚输入框）。</summary>
        public static bool SaveThresholds(string kind, string text, out string reason)
        {
            reason = "";
            string key = kind == "cput" ? "tray_icon_t" : (kind == "cpup" ? "tray_icon_p" : "tray_icon_l");
            double[] cur = kind == "cput" ? _tTemp : (kind == "cpup" ? _tPower : _tLoad);
            double[] fallback = kind == "cput" ? DefaultTemp : (kind == "cpup" ? DefaultPower : DefaultLoad);
            string[] parts = (text ?? "").Split(new char[] { ',' });
            if (parts.Length != 3) { reason = "需要 3 个数（逗号分隔）"; return false; }
            double[] r = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r[i]) || r[i] <= 0)
                { reason = "「" + parts[i].Trim() + "」不是正数"; return false; }
            }
            if (!(r[0] < r[1] && r[1] < r[2])) { reason = "三个数必须严格递增"; return false; }
            if (kind == "cput") _tTemp = r; else if (kind == "cpup") _tPower = r; else _tLoad = r;
            try { Settings.Set(key, Text(r)); } catch (Exception ex) { Log.Ex("保存托盘图标阈值失败", ex); }
            Log.Info("托盘图标：阈值 " + kind + " 改为 " + Text(r) + (cur == fallback ? "" : ""));
            return true;
        }

        /// <summary>恢复默认阈值。</summary>
        public static void ResetThresholds()
        {
            _tTemp = DefaultTemp; _tPower = DefaultPower; _tLoad = DefaultLoad;
            try
            {
                Settings.Set("tray_icon_t", Text(_tTemp));
                Settings.Set("tray_icon_p", Text(_tPower));
                Settings.Set("tray_icon_l", Text(_tLoad));
            }
            catch (Exception ex) { Log.Ex("保存默认阈值失败", ex); }
        }

        public static void Save(string kind)
        {
            _kind = kind;
            try { Settings.Set("tray_icon", kind); } catch (Exception ex) { Log.Ex("保存托盘图标配置失败", ex); }
        }

        public static string KindLabel()
        {
            for (int i = 0; i < Kinds.Length; i++) if (Kinds[i] == _kind) return KindLabels[i];
            return KindLabels[0];
        }

        /// <summary>取出用于画图标的数字文本（不含单位，2~3 字符）。取不到返回 null。</summary>
        public static string Number(string kind, out int level)
        {
            level = 0;
            string raw = TrayText.PreviewFor(kind);
            if (string.IsNullOrEmpty(raw)) return null;

            // cput: "CPU 58℃"；cpup: "CPU 24.7W"；cpul: "CPU 18%"
            string num = raw;
            int sp = num.LastIndexOf(' ');
            if (sp >= 0) num = num.Substring(sp + 1);
            num = num.Replace("℃", "").Replace("W", "").Replace("%", "").Replace("G", "");
            double d;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return null;

            double[] th = kind == "cput" ? _tTemp : (kind == "cpup" ? _tPower : _tLoad);
            level = 1;
            if (d >= th[0]) level = 2;
            if (d >= th[1]) level = 3;
            if (d >= th[2]) level = 4;
            return ((int)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
        }

        private static Color LevelColor(int level)
        {
            switch (level)
            {
                case 1: return Color.FromArgb(38, 118, 66);     // 凉：绿
                case 2: return Color.FromArgb(158, 118, 20);    // 温：琥珀
                case 3: return Color.FromArgb(176, 74, 24);     // 热：橙
                default: return Color.FromArgb(168, 36, 36);    // 烫：红
            }
        }

        /// <summary>画一枚图标（size×size）。--icon-preview 与运行时共用，保证预览就是运行时那枚。</summary>
        public static Bitmap Render(string text, int level, int size)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                g.Clear(Color.Transparent);

                int r = Math.Max(2, size / 6);
                using (System.Drawing.Drawing2D.GraphicsPath p = RoundedRect(new Rectangle(0, 0, size, size), r))
                using (SolidBrush b = new SolidBrush(text == null ? Color.FromArgb(96, 96, 104) : LevelColor(level)))
                    g.FillPath(b, p);

                if (text != null)
                {
                    // 2 字符用大字号，3 字符自动缩一档
                    float px = text.Length <= 2 ? size * 0.60f : size * 0.44f;
                    using (Font f = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush fg = new SolidBrush(Color.White))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        sf.FormatFlags = StringFormatFlags.NoWrap;
                        g.DrawString(text, f, fg, new RectangleF(0, 0, size, size), sf);
                    }
                }
            }
            return bmp;
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle b, int r)
        {
            System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = r * 2;
            p.AddArc(b.X, b.Y, d, d, 180, 90);
            p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
            p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
            p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>托盘刷新时调用：值变了才重画（避免无谓的 GDI 抖动）。</summary>
        public static void Update(NotifyIcon tray)
        {
            try
            {
                if (_kind == "none")
                {
                    if (_lastKey == "none") return;
                    _lastKey = "none";
                    if (_ownIcon != null) { _ownIcon.Dispose(); _ownIcon = null; }
                    try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                    return;
                }

                int level;
                string text = Number(_kind, out level);
                if (text == null)
                {
                    // 没有值就保持程序图标（画个空白色块会像坏了）；数据回来后会自动换回数字图标
                    if (!_loggedNoData) { _loggedNoData = true; Log.Info("托盘图标：暂时取不到 " + KindLabel() + " 的值，先保持程序图标"); }
                    if (_lastKey != "app")
                    {
                        _lastKey = "app";
                        if (_ownIcon != null) { _ownIcon.Dispose(); _ownIcon = null; }
                        try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                    }
                    return;
                }
                _loggedNoData = false;
                string key = _kind + "|" + text + "|" + level;      // 档位变化也要重画并留痕
                if (key == _lastKey) return;
                _lastKey = key;

                int size = IconSize();

                using (Bitmap bmp = Render(text, level, size))
                {
                    IntPtr h = bmp.GetHicon();          // 必须 DestroyIcon，否则泄漏
                    try
                    {
                        using (Icon tmp = Icon.FromHandle(h))
                        {
                            Icon clone = (Icon)tmp.Clone();
                            if (_ownIcon != null) _ownIcon.Dispose();
                            _ownIcon = clone;
                            tray.Icon = clone;
                            if (!_loggedFirst || level != _lastLoggedLevel)
                            {
                                _loggedFirst = true;
                                _lastLoggedLevel = level;
                                Log.Info("托盘图标：已用 " + KindLabel() + " 绘制（" + size.ToString(CultureInfo.InvariantCulture)
                                    + "px，当前 " + text + "，档位 " + level.ToString(CultureInfo.InvariantCulture) + "）");
                            }
                        }
                    }
                    finally { DestroyIcon(h); }
                }
            }
            catch (Exception ex) { Log.Ex("更新托盘图标失败", ex); }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly MainForm _form;
        private readonly ToolStripMenuItem[] _miMode = new ToolStripMenuItem[3];
        private readonly ToolStripMenuItem _miFanBoost = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _miStartup = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _miStatus = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _miVersion = new ToolStripMenuItem();
        private readonly System.Windows.Forms.Timer _trayTimer = new System.Windows.Forms.Timer();
        private bool _suppress;
        private bool _exiting;

        public TrayContext() : this(false) { }

        /// <summary>startHidden=true 时只驻留托盘，不弹主界面（开机自启走这条路）。</summary>
        public TrayContext(bool startHidden)
        {
            _form = new MainForm();
            _form.StateChanged += delegate { RefreshTray(); };
            _form.TrayIconChanged += delegate { TrayIcon.Update(_tray); RefreshTray(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9F);

            _miVersion.Text = "OpenMIFS v" + MifsApp.VersionText + "（同方 MIFS 控制台）";
            _miVersion.Enabled = false;
            _miVersion.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            menu.Items.Add(_miVersion);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miShow = new ToolStripMenuItem("显示主界面(&O)");
            miShow.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            miShow.Click += delegate { _form.ToggleVisible(); };
            menu.Items.Add(miShow);
            menu.Items.Add(new ToolStripSeparator());

            _miStatus.Enabled = false;
            menu.Items.Add(_miStatus);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miModeRoot = new ToolStripMenuItem("性能模式");
            for (int i = 0; i < ModeMap.Order.Length; i++)
            {
                ToolStripMenuItem mi = new ToolStripMenuItem(ModeMap.Order[i]);
                mi.Tag = ModeMap.Order[i];
                mi.Click += OnModeClick;
                miModeRoot.DropDownItems.Add(mi);
                _miMode[i] = mi;
            }
            menu.Items.Add(miModeRoot);

            _miFanBoost.Text = "风扇满速";
            _miFanBoost.Click += OnFanBoostClick;
            menu.Items.Add(_miFanBoost);

            _miStartup.Text = "开机自启";
            _miStartup.Click += OnStartupClick;
            menu.Items.Add(_miStartup);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miLog = new ToolStripMenuItem("打开日志(&L)");
            miLog.Click += delegate
            {
                try
                {
                    Log.Info("从托盘菜单打开日志");
                    if (File.Exists(Log.FilePath)) Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
                    else Process.Start("explorer.exe", "\"" + Log.Folder + "\"");
                }
                catch (Exception ex) { Log.Ex("打开日志失败", ex); }
            };
            menu.Items.Add(miLog);

            ToolStripMenuItem miDataDir = new ToolStripMenuItem("打开数据目录(&D)");
            miDataDir.Click += delegate
            {
                try
                {
                    Log.Info("从托盘菜单打开数据目录");
                    Process.Start("explorer.exe", "\"" + Log.Folder + "\"");
                }
                catch (Exception ex) { Log.Ex("打开数据目录失败", ex); }
            };
            menu.Items.Add(miDataDir);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出(&X)");
            miExit.Click += delegate { ExitApp(); };
            menu.Items.Add(miExit);

            _tray.ContextMenuStrip = menu;
            _tray.Text = "OpenMIFS v" + MifsApp.VersionText + " — 同方 MIFS 控制台";
            _tray.Visible = true;
            try { _tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            _tray.DoubleClick += delegate { _form.ToggleVisible(); };
            _tray.BalloonTipTitle = "OpenMIFS 仍在后台运行";
            _tray.BalloonTipText = "程序已最小化到通知区域，双击图标可重新打开。";
            _tray.BalloonTipIcon = ToolTipIcon.Info;

            _trayTimer.Interval = 5000;
            _trayTimer.Tick += delegate { RefreshTray(); };
            _trayTimer.Start();

            if (!Mifs.Available)
            {
                _tray.ShowBalloonTip(5000, "OpenMIFS",
                    "未检测到 MIFS 接口：" + Mifs.LastError, ToolTipIcon.Warning);
                Log.Error("启动时未检测到 MIFS 接口：" + Mifs.LastError);
            }

            if (startHidden)
            {
                // 必须让控件创建句柄（否则传感器那边的 BeginInvoke 会抛异常），
                // 做法：不显示在任务栏 + 完全透明地 Show 一次，再收进托盘。
                // 注意**不要**用 WindowState=Minimized：那会让窗口拿到"最小化占位坐标"(-25600,-25600)，
                // 之后 Show() 只让它变可见、不会挪回屏幕（0.5.5 修的正是这个）。
                _form.ShowInTaskbar = false;
                _form.Opacity = 0;
                _form.Show();
                _form.HideToTray();
                _form.Opacity = 1;
                Log.Info("以 --tray 启动：只驻留托盘，不显示主界面");
            }
            else
            {
                _form.Show();
            }
            RefreshTray();
            Log.Info("托盘已就绪，主窗口已显示");
        }

        private void OnModeClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            string name = (string)((ToolStripMenuItem)sender).Tag;
            try
            {
                Log.Info("托盘切换性能模式 → " + name);
                Mifs.SetByte(Mifs.FnPerMode, (byte)ModeMap.Value(name));
                Thread.Sleep(250);
                RefreshTray();
                _form.RefreshAll();
                int? back = Mifs.GetByte(Mifs.FnPerMode);
                Log.Info("托盘切换读回 = " + (back.HasValue ? ModeMap.Label(back.Value) : "读取失败"));
                _form.PrimeOsdWatch();
                if (back.HasValue) _form.ShowOsd("性能模式 · " + ModeMap.Label(back.Value));
            }
            catch (Exception ex)
            {
                Log.Ex("托盘切换性能模式失败", ex);
                _tray.ShowBalloonTip(4000, "OpenMIFS", "切换失败：" + ex.Message, ToolTipIcon.Warning);
            }
        }

        private void OnFanBoostClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            try
            {
                int acNow = Mifs.GetByte(Mifs.FnAcType) ?? -1;
                int? cur = Mifs.GetByte(Mifs.FnMaxFanSwitch);
                if (!cur.HasValue) { _tray.ShowBalloonTip(4000, "OpenMIFS", "风扇满速：本机未实现", ToolTipIcon.Info); return; }
                byte target = (byte)(cur.Value == 1 ? 0 : 1);
                Log.Info("托盘风扇满速 → " + (target == 1 ? "开" : "关"));
                Mifs.SetFanBoost(target);
                Thread.Sleep(400);
                int? back = Mifs.GetByte(Mifs.FnMaxFanSwitch);
                if (back.HasValue && back.Value != target)
                {
                    Caps.FanBoost = false;
                    Caps.FanBoostAcType = acNow;
                    Caps.Save();
                    Log.Warn("托盘风扇满速：写 " + target + " 读回 " + back.Value + " → 不可用（供电=" + Mifs.AcTypeName(acNow) + "）");
                    _tray.ShowBalloonTip(6000, "OpenMIFS", acNow == 1
                        ? "EC 忽略了写入 —— Type-C 供电下风扇满速被硬件禁用，请插圆口电源后重试。"
                        : "EC 忽略风扇满速写入，已标记为不可用。", ToolTipIcon.Warning);
                }
                else
                {
                    if (!Caps.ValidFor(acNow)) { Caps.FanBoost = true; Caps.FanBoostAcType = acNow; Caps.Save(); }
                    _form.ShowOsd("风扇满速 · " + (target == 1 ? "开" : "关"));
                }
                RefreshTray();
                _form.RefreshAll();
            }
            catch (Exception ex)
            {
                Log.Ex("托盘设置风扇满速失败", ex);
                _tray.ShowBalloonTip(4000, "OpenMIFS", "设置失败：" + ex.Message, ToolTipIcon.Warning);
            }
        }

        private void OnStartupClick(object sender, EventArgs e)
        {
            bool want = !Startup.IsEnabled();
            Log.Info("托盘切换开机自启 → " + (want ? "开" : "关"));
            bool ok = Startup.Set(want);
            if (!ok) _tray.ShowBalloonTip(5000, "OpenMIFS", "开机自启设置失败，见日志：" + Log.FilePath, ToolTipIcon.Warning);
            else _tray.ShowBalloonTip(3000, "OpenMIFS", "开机自启已" + (want ? "启用" : "关闭"), ToolTipIcon.Info);
            RefreshTray();
            _form.RefreshAll();
        }

        private void RefreshTray()
        {
            _suppress = true;
            try
            {
                int? pm = Mifs.GetByte(Mifs.FnPerMode);
                if (pm.HasValue)
                {
                    for (int i = 0; i < _miMode.Length; i++)
                        _miMode[i].Checked = ModeMap.Value(ModeMap.Order[i]) == pm.Value;
                    _miStatus.Text = "当前：" + ModeMap.Label(pm.Value);
                }
                else
                {
                    _miStatus.Text = "当前：接口不可用";
                }

                int acNow2 = Mifs.GetByte(Mifs.FnAcType) ?? -1;
                int? mxs = Mifs.GetByte(Mifs.FnMaxFanSwitch);
                bool boostUsable = mxs.HasValue && (!Caps.ValidFor(acNow2) || Caps.FanBoost.Value);
                _miFanBoost.Checked = mxs.HasValue && mxs.Value == 1;
                _miFanBoost.Enabled = boostUsable;
                _miFanBoost.Text = boostUsable ? "风扇满速" : "风扇满速（未实现）";

                _miStartup.Checked = Startup.IsEnabled();

                // 窗口隐藏时主窗体不刷新传感器 → 勾了 CPU 项就按需后台采一轮快照
                // 提示项或图标任一方需要传感器 → 隐藏时都要采快照
                bool needSensors = TrayText.NeedsSensors() || TrayIcon.Kind != "none";
                if (!_form.Visible && needSensors && (DateTime.Now - TrayText.SensorTime).TotalSeconds > 8)
                    _form.RequestHiddenSensorSnapshot();
                string tip = TrayText.Build();
                if (tip.Length == 0) tip = "OpenMIFS v" + MifsApp.VersionText;
                _tray.Text = tip;
                TrayIcon.Update(_tray);
            }
            catch (Exception ex) { Log.Ex("刷新托盘失败", ex); }
            _suppress = false;
        }

        internal void ToggleForTest() { _form.ToggleVisible(); }

        /// <summary>回归用：描述当前窗口状态（可见性 / 状态 / 矩形 / 是否在屏幕内）。</summary>
        internal string DescribeForTest()
        {
            Rectangle b = _form.Bounds;
            bool on = false;
            Screen[] all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++) if (all[i].WorkingArea.IntersectsWith(b)) { on = true; break; }
            return "Visible=" + _form.Visible + " State=" + _form.WindowState
                 + " Bounds=" + b.ToString() + " 在屏幕内=" + on;
        }

        internal void MinimizeForTest()
        {
            // 关键前置条件：WindowState=Minimized（用户点过最小化）且已收进托盘
            _form.WindowState = FormWindowState.Minimized;
            _form.HideToTray();
            Log.Info("回归：已置为 最小化+隐藏（WindowState=" + _form.WindowState + " Visible=" + _form.Visible + "）");
        }

        internal void ExitForTest() { ExitApp(); }

        private void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            Log.Info("用户从托盘退出程序");
            _trayTimer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            _form.ForceClose();
            ExitThread();
        }
    }

    // ─────────────────────────────────────────────────────────────── 入口
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool diagnose = false;
            bool sensors = false;
            bool tray = false;
            bool iconPreview = false;
            int toggleTest = 0;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--diagnose" || a == "-d" || a == "/diagnose") diagnose = true;
                if (a == "--sensors" || a == "-s" || a == "/sensors") sensors = true;
                if (a == "--tray" || a == "-t" || a == "/tray" || a == "--silent" || a == "--minimized") tray = true;
                if (a == "--icon-preview" || a == "--icon") iconPreview = true;
                if (a.StartsWith("--toggle-test"))
                {
                    string num = a.Substring("--toggle-test".Length).TrimStart('=');
                    int n2;
                    toggleTest = int.TryParse(num, out n2) && n2 > 0 ? n2 : 3;
                }
            }

            // 无界面模式：把托盘图标的各种取值画出来（放大 6 倍拼成一张图），用于确认 16×16 下是否可读
            if (iconPreview)
            {
                Log.Info("================ OpenMIFS " + MifsApp.VersionText + " 托盘图标预览 ================");
                try
                {
                    int[] sizes = new int[] { 16, 20, 24 };
                    string[] samples = new string[] { "42", "58", "85", "100" };
                    int cell = 24 * 6;
                    int w = cell * 4 + 40, h = cell * sizes.Length + 40;
                    using (Bitmap canvas = new Bitmap(w, h))
                    {
                        using (Graphics g = Graphics.FromImage(canvas))
                        {
                            g.Clear(Color.FromArgb(32, 32, 32));
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                            for (int s = 0; s < sizes.Length; s++)
                            {
                                for (int v = 0; v < samples.Length; v++)
                                {
                                    int lv = v + 1;
                                    using (Bitmap bmp = TrayIcon.Render(samples[v], lv, sizes[s]))
                                    {
                                        int x = 20 + v * cell + (cell - sizes[s] * 6) / 2;
                                        int y = 20 + s * cell + (cell - sizes[s] * 6) / 2;
                                        g.DrawImage(bmp, new Rectangle(x, y, sizes[s] * 6, sizes[s] * 6));
                                    }
                                }
                            }
                        }
                        if (!Directory.Exists(Log.Folder)) Directory.CreateDirectory(Log.Folder);
                        string file = Path.Combine(Log.Folder, "tray-icon-preview.png");
                        canvas.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                        Log.Info("图标预览已写入 " + file + "（三行分别为 16/20/24 px，四列为 42/58/85/100）");
                    }
                }
                catch (Exception ex) { Log.Ex("图标预览失败", ex); }
                return;
            }

            // 无界面模式：探测传感器数据源，写进 %LOCALAPPDATA%\OpenMIFS\sensors-probe.txt 后退出
            if (sensors)
            {
                Log.Info("================ OpenMIFS " + MifsApp.VersionText + " 传感器探测 ================");
                Log.Info("数据目录     : " + Log.Folder);
                try
                {
                    string text = Sensors.Probe();
                    if (!Directory.Exists(Log.Folder)) Directory.CreateDirectory(Log.Folder);
                    string file = Path.Combine(Log.Folder, "sensors-probe.txt");
                    File.WriteAllText(file, text, new UTF8Encoding(false));
                    Log.Info("传感器探测结果已写入 " + file);
                }
                catch (Exception ex) { Log.Ex("传感器探测失败", ex); }
                return;
            }

            // 无界面模式：采集 OSD 证据写进 %LOCALAPPDATA%\OpenMIFS\osd-diagnose.txt 后退出。
            // 便于支持排障，也便于自动化验证。
            if (diagnose)
            {
                Log.Info("================ OpenMIFS " + MifsApp.VersionText + " 命令行诊断 ================");
                Log.Info("模式         : --diagnose（无界面）");
                Log.Info("数据目录     : " + Log.Folder);
                try { Osd.Diagnose(); }
                catch (Exception ex) { Log.Ex("OSD 诊断失败", ex); }
                Log.Info("诊断结束，输出： " + System.IO.Path.Combine(Log.Folder, "osd-diagnose.txt"));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 回归模式：复现"最小化 → 收进托盘 → 点显示主界面"这条路径（历史上有过栈溢出崩溃）
            if (toggleTest > 0)
            {
                Log.Info("================ OpenMIFS " + MifsApp.VersionText + " 可见性切换回归（" + toggleTest + " 次）================ ");
                TrayContext tc = new TrayContext(true);
                tc.MinimizeForTest();
                int done = 0;
                System.Windows.Forms.Timer reg = new System.Windows.Forms.Timer();
                reg.Interval = 1500;
                reg.Tick += delegate
                {
                    done++;
                    Log.Info("回归：第 " + done + " 次切换可见性");
                    tc.ToggleForTest();
                    Log.Info("回归：切换后 " + tc.DescribeForTest());
                    if (done >= toggleTest) { reg.Stop(); Log.Info("回归：全部完成，未崩溃"); tc.ExitForTest(); }
                };
                reg.Start();
                try { Application.Run(tc); }
                catch (Exception ex) { Log.Ex("回归模式异常", ex); }
                return;
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Global\\OpenMIFS_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    if (tray)
                    {
                        // 开机自启与手动启动撞车时不要弹框打扰
                        Log.Info("检测到已有实例在运行（--tray 启动），静默退出");
                        return;
                    }
                    Log.Info("检测到已有实例在运行，本次启动退出");
                    MessageBox.Show("OpenMIFS 已在运行。\r\n请在任务栏右下角通知区域找到它的图标。",
                        "OpenMIFS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Log.Info("================ OpenMIFS " + MifsApp.VersionText + " 启动 ================");
                Log.Info("程序路径     : " + Application.ExecutablePath);
                Log.Info("用户 / 管理员: " + Environment.UserDomainName + "\\" + Environment.UserName + " / " + (MifsApp.IsElevated ? "是" : "否"));
                Log.Info("系统         : " + Environment.OSVersion.VersionString + "  CLR " + Environment.Version.ToString());
                Log.Info("数据目录     : " + Log.Folder);

                Caps.Load();
                Log.Info("MIFS 接口    : " + (Mifs.Available ? "可用" : "不可用 - " + Mifs.LastError));
                Log.Info("启动方式     : " + (tray ? "--tray（只驻留托盘）" : "常规（显示主界面）"));
                if (MifsApp.IsElevated) Startup.RepairIfNeeded();   // 老任务不带 --tray 会自动重建

                try { Application.Run(new TrayContext(tray)); }
                catch (Exception ex) { Log.Ex("主循环异常退出", ex); if (ex.StackTrace != null) Log.Info("异常堆栈：" + ex.StackTrace.Replace("\r\n", " | ")); throw; }
                finally { Log.Info("================ OpenMIFS 退出 ================"); }
            }
        }
    }
}
