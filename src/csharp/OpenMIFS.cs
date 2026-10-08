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
[assembly: AssemblyVersion("0.5.4.0")]
[assembly: AssemblyFileVersion("0.5.4.0")]

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
        private static readonly Color ColOk   = Color.FromArgb(22, 128, 61);
        private static readonly Color ColWarn = Color.FromArgb(185, 28, 28);
        private static readonly Color ColDim  = Color.FromArgb(130, 130, 130);

        private readonly Label _lblHeader = new Label();
        private readonly Button[] _btnMode = new Button[3];
        private readonly Label _lblFan = new Label();
        private readonly Button _btnBoost = new Button();
        private readonly CheckBox _chkFn = new CheckBox();
        private readonly CheckBox _chkTp = new CheckBox();
        private readonly Button[] _btnKbd = new Button[4];
        private GroupBox _gbKbd;
        private readonly CheckBox _chkStartup = new CheckBox();
        private readonly ComboBox _cmbOsdHint = new ComboBox();
        private readonly System.Windows.Forms.Timer _hintTimer = new System.Windows.Forms.Timer();
        private string _osdHintMode = "auto";
        private string _pendingHint;
        private bool _sensorBusy;
        private int _acTypeNow = -1;
        private int _fanRpmBefore = -1;
        private readonly Label _lblOsd = new Label();
        private readonly Button _btnOsdRestart = new Button();
        private readonly Button _btnOsdDiag = new Button();
        private readonly Button _btnOsdDir = new Button();
        private readonly Button _btnRecap = new Button();
        private readonly CheckBox _chkDpi = new CheckBox();
        private readonly Label _lblDpi = new Label();
        private readonly Panel _pnlStatusRows = new Panel();
        private readonly Panel _pnlSensorRows = new Panel();
        private GroupBox _gbStatus;
        private readonly Panel _pnlSensorBar = new Panel();
        private readonly Button _btnSensorProbe = new Button();
        private readonly Label _lblResizeHint = new Label();
        private GroupBox _gbSensors;
        private readonly Label _lblSensorHint = new Label();
        private DateTime _lastSensorRefresh = DateTime.MinValue;
        private readonly CheckBox _chkAuto = new CheckBox();
        private readonly ComboBox _cmbInterval = new ComboBox();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _osdWatch = new System.Windows.Forms.Timer();

        private OsdOverlay _overlay;

        private bool _suppress;
        private bool _fanBoostOn;
        private bool _fanBoostUsable;
        private bool _trayHintShown;
        private bool _reallyClose;
        private bool _firstRefreshDone;
        private string _lastLoggedError = "";

        private DateTime _lastOsdCheck = DateTime.MinValue;
        private string _osdStatus = "OSD：检测中…";

        // 自带 OSD 的变化监视缓存（专门盯 Fn 键会改的那几个值）
        private int _wMode = -1;
        private int _wFn = -1;
        private int _wTp = -1;
        private int _wKbd = -1;
        private bool _wPrimed;

        private readonly Font _fontUi  = new Font("Microsoft YaHei UI", 9F);
        private readonly Font _fontBold = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        private readonly Font _fontUi8 = new Font("Microsoft YaHei UI", 8F);

        public event EventHandler StateChanged;
        /// <summary>图标显示项改变 → 托盘立刻重画（不必等 5 秒定时器）。</summary>
        public event EventHandler TrayIconChanged;

        public MainForm()
        {
            Text = "OpenMIFS v" + MifsApp.VersionText + " — 同方 MIFS 控制台";
            ClientSize = new Size(940, 815);
            MinimumSize = new Size(700, 560);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;   // 可拖动缩放：右列数据区跟着窗口变大
            MaximizeBox = true;
            Font = _fontUi;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            BuildUi();
            _timer.Interval = 3000;
            _timer.Tick += delegate { if (_chkAuto.Checked) RefreshAll(); };
            _timer.Start();

            TrayText.Load();
            TrayIcon.Load();
            TrayIcon.LoadThresholds();
            SyncTrayUi();
            SyncTrayThresholds();

            _osdHintMode = Settings.Get("osd_hint", "auto");
            _hintTimer.Interval = 350;   // 等官方 OSD 先画出来再决定要不要显示自带的
            _hintTimer.Tick += delegate { _hintTimer.Stop(); FlushPendingHint(); };

            // 专门盯 Fn 键会改的那几个 EC 值：官方 OSD 失效时由我们自己弹提示。
            // 1.5 秒一轮，只读 4 个功能号，开销很小。
            _osdWatch.Interval = 1500;
            _osdWatch.Tick += delegate { CheckOsdWatch(); };
            _osdWatch.Start();

            Shown += delegate { RefreshAll(); };
            // 注意：HideToTray/Restore 会改 ShowInTaskbar（触发句柄重建），从而再次引发 Resize。
            // 必须防重入，否则"最小化后收进托盘 → 点显示主界面"会无限互递归 → 栈溢出崩溃（0xC00000FD）。
            Resize += delegate
            {
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
            // 顺序很重要：先把 WindowState 归位，再做"会引发句柄重建/重新布局"的操作，
            // 这样 Resize 处理器看到的永远是 Normal，不会反过来调 HideToTray。
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            Show();
            Activate();
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

        private GroupBox NewGroup(string text, int y, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.Location = new Point(LeftColX, y);
            g.Size = new Size(LeftColW, h);
            g.Font = _fontUi8;
            g.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Controls.Add(g);
            return g;
        }

        private void BuildUi()
        {
            // 顶部状态条：两行文字，高度给足，避免第二行被下面的分组框压掉
            _lblHeader.Location = new Point(14, 6);
            _lblHeader.Size = new Size(ClientSize.Width - 26, 46);
            _lblHeader.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lblHeader.Font = _fontUi8;
            _lblHeader.Text = "正在检测接口…";
            _lblHeader.AutoSize = false;
            _lblHeader.TextAlign = ContentAlignment.TopLeft;
            Controls.Add(_lblHeader);

            // 性能模式
            GroupBox gMode = NewGroup("性能模式", 58, 68);
            int x = 12;
            for (int i = 0; i < ModeMap.Order.Length; i++)
            {
                Button b = new Button();
                b.Text = ModeMap.Order[i];
                b.Location = new Point(x, 24);
                b.Size = new Size(140, 30);
                b.Tag = ModeMap.Order[i];
                b.Font = new Font("Microsoft YaHei UI", 9F);
                b.Click += OnModeClick;
                gMode.Controls.Add(b);
                _btnMode[i] = b;
                x += 148;
            }

            // 风扇
            GroupBox gFan = NewGroup("风扇", 132, 84);
            _lblFan.Location = new Point(12, 22);
            _lblFan.Size = new Size(432, 20);
            _lblFan.Font = _fontUi8;
            _lblFan.Text = "读取中…";
            _lblFan.AutoSize = false;
            gFan.Controls.Add(_lblFan);

            _btnBoost.Location = new Point(12, 46);
            _btnBoost.Size = new Size(200, 28);
            _btnBoost.Font = new Font("Microsoft YaHei UI", 9F);
            _btnBoost.Text = "风扇满速：关";
            _btnBoost.Click += OnFanBoostClick;
            gFan.Controls.Add(_btnBoost);

            // 硬件开关
            GroupBox gSw = NewGroup("硬件开关", 222, 60);
            _chkFn.Text = "Fn 锁";
            _chkFn.Location = new Point(14, 24);
            _chkFn.Size = new Size(150, 22);
            _chkFn.Font = new Font("Microsoft YaHei UI", 9F);
            _chkFn.Click += OnFnLockClick;
            gSw.Controls.Add(_chkFn);

            _chkTp.Text = "触控板锁定";
            _chkTp.Location = new Point(200, 24);
            _chkTp.Size = new Size(190, 22);
            _chkTp.Font = new Font("Microsoft YaHei UI", 9F);
            _chkTp.Click += OnTpLockClick;
            gSw.Controls.Add(_chkTp);

            // 键盘背光
            _gbKbd = NewGroup("键盘背光亮度", 288, 60);
            GroupBox gKbd = _gbKbd;
            x = 12;
            for (int i = 0; i < 4; i++)
            {
                Button b = new Button();
                b.Text = i.ToString(CultureInfo.InvariantCulture);
                b.Location = new Point(x, 24);
                b.Size = new Size(62, 28);
                b.Tag = i;
                b.Font = new Font("Microsoft YaHei UI", 9F);
                b.Click += OnKbdClick;
                gKbd.Controls.Add(b);
                _btnKbd[i] = b;
                x += 76;
            }

            // 启动与 OSD
            GroupBox gOsd = NewGroup("启动与 OSD 屏幕提示", 354, 152);

            _chkStartup.Text = "开机自启（计划任务 · 免 UAC · 静默进托盘）";
            _chkStartup.Location = new Point(14, 22);
            _chkStartup.Size = new Size(232, 22);
            _chkStartup.Font = new Font("Microsoft YaHei UI", 9F);
            _chkStartup.Click += OnStartupClick;
            gOsd.Controls.Add(_chkStartup);

            Label lblHintMode = new Label();
            lblHintMode.Text = "屏幕提示";
            lblHintMode.Location = new Point(248, 25);
            lblHintMode.Size = new Size(60, 18);
            lblHintMode.Font = _fontUi8;
            lblHintMode.AutoSize = false;
            gOsd.Controls.Add(lblHintMode);

            _cmbOsdHint.Location = new Point(308, 22);
            _cmbOsdHint.Size = new Size(136, 22);
            _cmbOsdHint.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbOsdHint.Font = _fontUi8;
            _cmbOsdHint.Items.AddRange(new object[] { "自动（不重复）", "总是显示", "关闭" });
            _cmbOsdHint.SelectedIndex = 0;
            _cmbOsdHint.SelectedIndexChanged += OnOsdHintModeChanged;
            gOsd.Controls.Add(_cmbOsdHint);

            _lblOsd.Location = new Point(12, 48);
            _lblOsd.Size = new Size(432, 20);
            _lblOsd.Font = _fontUi8;
            _lblOsd.Text = "OSD：检测中…";
            _lblOsd.AutoSize = false;
            gOsd.Controls.Add(_lblOsd);

            _btnOsdRestart.Text = "重启 OSD";
            _btnOsdRestart.Location = new Point(12, 74);
            _btnOsdRestart.Size = new Size(104, 28);
            _btnOsdRestart.Font = new Font("Microsoft YaHei UI", 9F);
            _btnOsdRestart.Click += OnOsdRestartClick;
            gOsd.Controls.Add(_btnOsdRestart);

            _btnOsdDiag.Text = "诊断 OSD";
            _btnOsdDiag.Location = new Point(122, 74);
            _btnOsdDiag.Size = new Size(104, 28);
            _btnOsdDiag.Font = new Font("Microsoft YaHei UI", 9F);
            _btnOsdDiag.Click += OnOsdDiagClick;
            gOsd.Controls.Add(_btnOsdDiag);

            _btnOsdDir.Text = "打开目录";
            _btnOsdDir.Location = new Point(232, 74);
            _btnOsdDir.Size = new Size(104, 28);
            _btnOsdDir.Font = new Font("Microsoft YaHei UI", 9F);
            _btnOsdDir.Click += OnOpenOsdDirClick;
            gOsd.Controls.Add(_btnOsdDir);

            _btnRecap.Text = "重测功能";
            _btnRecap.Location = new Point(342, 74);
            _btnRecap.Size = new Size(102, 28);
            _btnRecap.Font = new Font("Microsoft YaHei UI", 9F);
            _btnRecap.Click += OnRecapClick;
            gOsd.Controls.Add(_btnRecap);

            // DPI 兼容修复：OSD 界面进程是 DPI 不感知的，在 125%/150% 缩放下
            // 分层窗口可能静默画不出来。写一条 AppCompatFlags 标记让它按系统 DPI 渲染。
            _chkDpi.Text = "DPI 兼容修复（实验，可撤销）";
            _chkDpi.Location = new Point(14, 110);
            _chkDpi.Size = new Size(240, 22);
            _chkDpi.Font = new Font("Microsoft YaHei UI", 9F);
            _chkDpi.Click += OnDpiFixClick;
            gOsd.Controls.Add(_chkDpi);

            _lblDpi.Location = new Point(258, 110);
            _lblDpi.Size = new Size(186, 22);
            _lblDpi.Font = _fontUi8;
            _lblDpi.Text = "";
            _lblDpi.AutoSize = false;
            gOsd.Controls.Add(_lblDpi);

            // ── 托盘悬停提示：白名单勾选 + 最坏情况预算（放不下的直接拒绝勾选）
            _gbTray = NewGroup("托盘悬停提示（按最坏宽度限额）", 516, 218);
            _lstTray.CheckOnClick = true;
            _lstTray.Location = new Point(12, 20);
            _lstTray.Size = new Size(214, 116);
            _lstTray.Font = new Font("Microsoft YaHei UI", 9F);
            _lstTray.IntegralHeight = false;
            _lstTray.ItemCheck += OnTrayItemCheck;
            for (int i = 0; i < TrayText.All.Length; i++)
            {
                TrayText.Item it = TrayText.All[i];
                _lstTray.Items.Add(it.Label + "（≤" + it.Worst.ToString(CultureInfo.InvariantCulture) + "）");
            }
            _gbTray.Controls.Add(_lstTray);

            _lblTrayPreview.Location = new Point(234, 20);
            _lblTrayPreview.Size = new Size(210, 66);
            _lblTrayPreview.Font = _fontUi8;
            _lblTrayPreview.BackColor = Color.FromArgb(246, 246, 248);
            _lblTrayPreview.BorderStyle = BorderStyle.FixedSingle;
            _lblTrayPreview.TextAlign = ContentAlignment.TopLeft;
            _gbTray.Controls.Add(_lblTrayPreview);

            _lblTrayBudget.Location = new Point(234, 88);
            _lblTrayBudget.Size = new Size(210, 18);
            _lblTrayBudget.Font = _fontUi8;
            _lblTrayBudget.TextAlign = ContentAlignment.MiddleLeft;
            _gbTray.Controls.Add(_lblTrayBudget);

            _btnTrayDefault.Text = "恢复默认";
            _btnTrayDefault.Location = new Point(234, 108);
            _btnTrayDefault.Size = new Size(96, 28);
            _btnTrayDefault.Font = new Font("Microsoft YaHei UI", 9F);
            _btnTrayDefault.Click += OnTrayDefaultClick;
            _gbTray.Controls.Add(_btnTrayDefault);

            _lblTrayIcon.Location = new Point(12, 142);
            _lblTrayIcon.Size = new Size(58, 18);
            _lblTrayIcon.Font = _fontUi8;
            _lblTrayIcon.Text = "图标显示";
            _lblTrayIcon.TextAlign = ContentAlignment.MiddleLeft;
            _gbTray.Controls.Add(_lblTrayIcon);

            _cmbTrayIcon.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbTrayIcon.Location = new Point(72, 140);
            _cmbTrayIcon.Size = new Size(132, 22);
            _cmbTrayIcon.Font = _fontUi8;
            for (int i = 0; i < TrayIcon.Kinds.Length; i++) _cmbTrayIcon.Items.Add(TrayIcon.KindLabels[i]);
            _cmbTrayIcon.SelectedIndex = 0;
            _cmbTrayIcon.SelectedIndexChanged += OnTrayIconKindChanged;
            _gbTray.Controls.Add(_cmbTrayIcon);

            _lblTrayTh.Location = new Point(12, 168);
            _lblTrayTh.Size = new Size(58, 18);
            _lblTrayTh.Font = _fontUi8;
            _lblTrayTh.Text = "变色阈值";
            _lblTrayTh.TextAlign = ContentAlignment.MiddleLeft;
            _gbTray.Controls.Add(_lblTrayTh);

            string[] thUnit = new string[] { "温度", "功耗", "负载" };
            for (int i = 0; i < 3; i++)
            {
                _lblTrayThUnit[i].Location = new Point(72 + i * 124, 168);
                _lblTrayThUnit[i].Size = new Size(30, 18);
                _lblTrayThUnit[i].Font = _fontUi8;
                _lblTrayThUnit[i].Text = thUnit[i];
                _lblTrayThUnit[i].TextAlign = ContentAlignment.MiddleLeft;
                _gbTray.Controls.Add(_lblTrayThUnit[i]);

                _txtTrayTh[i].Location = new Point(102 + i * 124, 166);
                _txtTrayTh[i].Size = new Size(86, 21);
                _txtTrayTh[i].Font = _fontUi8;
                _txtTrayTh[i].Tag = TrayIcon.Kinds[i + 1];      // cput / cpup / cpul
                _txtTrayTh[i].Text = TrayIcon.ThresholdText(TrayIcon.Kinds[i + 1]);
                _txtTrayTh[i].Leave += OnTrayThresholdLeave;
                _gbTray.Controls.Add(_txtTrayTh[i]);
            }

            _lblTrayHint.Location = new Point(12, 192);
            _lblTrayHint.Size = new Size(432, 18);
            _lblTrayHint.Font = _fontUi8;
            _lblTrayHint.Text = "阈值：三个升序数值（逗号分隔），如 55,70,85 = 绿/琥珀/橙/红四档分界";
            _gbTray.Controls.Add(_lblTrayHint);
            // ── 右列：状态 + 传感器，直接摆在首页，键值行显示，随窗口缩放
            _pnlSensorBar.Dock = DockStyle.Bottom;
            _pnlSensorBar.Height = 34;
            _btnSensorProbe.Text = "探测数据源";
            _btnSensorProbe.Location = new Point(4, 3);
            _btnSensorProbe.Size = new Size(110, 28);
            _btnSensorProbe.Font = new Font("Microsoft YaHei UI", 9F);
            _btnSensorProbe.Click += OnSensorProbeClick;
            _pnlSensorBar.Controls.Add(_btnSensorProbe);

            _lblSensorHint.Location = new Point(122, 9);
            _lblSensorHint.Size = new Size(360, 18);
            _lblSensorHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lblSensorHint.Font = _fontUi8;
            _lblSensorHint.Text = "正在读取…";
            _lblSensorHint.AutoSize = false;
            _pnlSensorBar.Controls.Add(_lblSensorHint);

            int rightW = ClientSize.Width - RightColX - RightColGap;

            _gbStatus = new GroupBox();
            _gbStatus.Text = "状态";
            _gbStatus.Font = _fontUi8;
            _gbStatus.Location = new Point(RightColX, 58);
            _gbStatus.Size = new Size(rightW, 242);
            _gbStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _pnlStatusRows.AutoScroll = true;
            _pnlStatusRows.Location = new Point(8, 18);
            _pnlStatusRows.Size = new Size(rightW - 16, 216);
            _pnlStatusRows.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _gbStatus.Controls.Add(_pnlStatusRows);
            Controls.Add(_gbStatus);

            _gbSensors = new GroupBox();
            _gbSensors.Text = "传感器（只读 · 零驱动）";
            _gbSensors.Font = _fontUi8;
            _gbSensors.Location = new Point(RightColX, 308);
            _gbSensors.Size = new Size(rightW, ClientSize.Height - 308 - 52);
            _gbSensors.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _pnlSensorRows.AutoScroll = true;
            _pnlSensorRows.Location = new Point(8, 18);
            _pnlSensorRows.Size = new Size(rightW - 16, _gbSensors.Height - 56);
            _pnlSensorRows.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _gbSensors.Controls.Add(_pnlSensorRows);
            _gbSensors.Controls.Add(_pnlSensorBar);
            Controls.Add(_gbSensors);

            // 底部
            _chkAuto.Text = "自动刷新";
            _chkAuto.Location = new Point(14, ClientSize.Height - 36);
            _chkAuto.Size = new Size(92, 22);
            _chkAuto.Checked = true;
            _chkAuto.Font = _fontUi8;
            _chkAuto.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(_chkAuto);

            _cmbInterval.Location = new Point(110, ClientSize.Height - 37);
            _cmbInterval.Size = new Size(66, 22);
            _cmbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbInterval.Font = _fontUi8;
            _cmbInterval.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _cmbInterval.Items.AddRange(new object[] { "2 秒", "3 秒", "5 秒", "10 秒" });
            _cmbInterval.SelectedIndex = 1;
            _cmbInterval.SelectedIndexChanged += OnIntervalChanged;
            Controls.Add(_cmbInterval);

            Button btnRefresh = new Button();
            btnRefresh.Text = "刷新";
            btnRefresh.Location = new Point(ClientSize.Width - 106, ClientSize.Height - 41);
            btnRefresh.Size = new Size(94, 28);
            btnRefresh.Font = new Font("Microsoft YaHei UI", 9F);
            btnRefresh.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnRefresh.Click += delegate { Log.Info("手动刷新"); RefreshAll(); };
            Controls.Add(btnRefresh);

            _lblResizeHint.Location = new Point(188, ClientSize.Height - 33);
            _lblResizeHint.Size = new Size(220, 18);
            _lblResizeHint.Font = _fontUi8;
            _lblResizeHint.ForeColor = ColDim;
            _lblResizeHint.Text = "窗口可拖动缩放，右侧数据区会跟着变大";
            _lblResizeHint.AutoSize = false;
            _lblResizeHint.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(_lblResizeHint);
        }

        // ────────────────────────────────────────────────────── 数据行渲染
        private sealed class KvRow
        {
            public Label Key;
            public Label Val;
        }

        private const int RowH = 22;
        private static readonly string[] SensorOrder = new string[]
        {
            "CPU 温度", "CPU 功耗", "CPU 频率", "CPU 负载",
            "GPU 温度", "GPU 功耗", "GPU 利用率", "GPU 频率", "GPU 显存",
            "内存占用", "内存规格", "磁盘温度", "磁盘",
            "风扇1", "风扇2", "风扇3", "电池"
        };
        private readonly List<string> _statusOrder = new List<string>();
        private readonly Dictionary<string, KvRow> _statusRows = new Dictionary<string, KvRow>();
        private readonly List<string> _sensorOrder = new List<string>();
        private readonly Dictionary<string, KvRow> _sensorRows = new Dictionary<string, KvRow>();
        private GroupBox _gbTray;
        private readonly CheckedListBox _lstTray = new CheckedListBox();
        private readonly Label _lblTrayPreview = new Label();
        private readonly Label _lblTrayBudget = new Label();
        private readonly Label _lblTrayHint = new Label();
        private readonly Button _btnTrayDefault = new Button();
        private readonly Label _lblTrayIcon = new Label();
        private readonly ComboBox _cmbTrayIcon = new ComboBox();
        private readonly Label _lblTrayTh = new Label();
        private readonly Label[] _lblTrayThUnit = new Label[] { new Label(), new Label(), new Label() };
        private readonly TextBox[] _txtTrayTh = new TextBox[] { new TextBox(), new TextBox(), new TextBox() };
        private bool _trayUiSync;
        private bool _visibilityBusy;     // HideToTray/Restore 互斥，防止与 Resize 互递归
        private bool _mifsTempPowerChecked;
        private readonly ToolTip _tips = new ToolTip();

        /// <summary>按需创建一行（灰色小标签 + 加粗数值）。</summary>
        private KvRow EnsureRow(Panel host, List<string> order, Dictionary<string, KvRow> map, string key)
        {
            KvRow row;
            if (map.TryGetValue(key, out row)) return row;
            int y = order.Count * RowH;
            Label k = new Label();
            k.Text = key;
            k.Location = new Point(10, y + 2);
            k.Size = new Size(92, 20);
            k.Font = _fontUi;
            k.ForeColor = Color.FromArgb(96, 96, 102);
            k.TextAlign = ContentAlignment.MiddleLeft;
            k.AutoSize = false;
            Label v = new Label();
            v.Text = "—";
            v.Location = new Point(104, y);
            v.Size = new Size(host.Width - 112, 24);
            v.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            v.TextAlign = ContentAlignment.MiddleLeft;
            v.AutoSize = false;
            v.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            host.Controls.Add(k);
            host.Controls.Add(v);
            row = new KvRow();
            row.Key = k;
            row.Val = v;
            map[key] = row;
            order.Add(key);
            host.Height = 6 + order.Count * RowH;
            return row;
        }

        /// <summary>单独设置一行（不存在就创建）。</summary>
        private void SetRow(Panel host, List<string> order, Dictionary<string, KvRow> map, string key, string value)
        {
            KvRow r = EnsureRow(host, order, map, key);
            r.Key.Text = key;
            r.Val.Text = value;
            r.Val.ForeColor = Color.FromArgb(24, 24, 28);
        }

        /// <summary>把「名称 : 值」形式的多行文本渲染成一排排键值标签（不再用文本框）。
        /// "└" 开头的说明行不进面板，挂到该行的鼠标提示上；"== xx ==" 分组标题只看不显示。</summary>
        private void RenderKeyValues(Panel host, List<string> order, Dictionary<string, KvRow> map, string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            string lastKey = "";

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string t = raw.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("==")) continue;                    // 分组标题
                if (t.StartsWith("└"))                               // 数据源说明 → 挂到上一行的提示
                {
                    KvRow prev;
                    if (lastKey.Length > 0 && map.TryGetValue(lastKey, out prev))
                        _tips.SetToolTip(prev.Val, t.Substring(1).Trim());
                    continue;
                }
                int c = raw.IndexOf(':');
                if (c <= 0) continue;
                string key = raw.Substring(0, c).Trim();
                string val = raw.Substring(c + 1).Trim();
                if (key.Length == 0) continue;
                lastKey = key;

                KvRow row = EnsureRow(host, order, map, key);
                row.Key.Text = key;
                row.Val.Text = val;
                // 未实现/不支持的值用灰色，一眼能分辨
                bool dim = val.StartsWith("未实现") || val.StartsWith("不支持") || val.StartsWith("需要管理员")
                        || val == "—" || val == "未知";
                row.Val.ForeColor = dim ? Color.FromArgb(140, 140, 145) : Color.FromArgb(24, 24, 28);
                seen[key] = true;
            }

            // 本轮没出现过的行显示为 —
            for (int i = 0; i < order.Count; i++)
            {
                if (seen.ContainsKey(order[i])) continue;
                KvRow r = map[order[i]];
                r.Val.Text = "—";
                r.Val.ForeColor = Color.FromArgb(140, 140, 145);
            }
        }

        private void SetRows(Panel host, List<string> order, Dictionary<string, KvRow> map, string text)
        {
            RenderKeyValues(host, order, map, text);
        }

        // ── 托盘提示设置
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
            _lblTrayBudget.Text = "最坏情况 " + TrayText.WorstTotal().ToString(CultureInfo.InvariantCulture)
                + "/" + TrayText.MaxChars.ToString(CultureInfo.InvariantCulture) + " 字符"
                + "　实际 " + s.Length.ToString(CultureInfo.InvariantCulture) + " 字符";
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
                _lblTrayHint.Text = "「" + it.Label + "」最坏要 " + it.Worst.ToString(CultureInfo.InvariantCulture)
                    + " 字符，加上会到 " + withIt.ToString(CultureInfo.InvariantCulture)
                    + "（上限 " + TrayText.MaxChars.ToString(CultureInfo.InvariantCulture) + "）→ 先取消一项再勾。";
                Log.Info("托盘提示：拒绝勾选 " + it.Id + "（最坏 " + withIt.ToString(CultureInfo.InvariantCulture) + " 字符）");
                return;
            }
            if (!TrayText.TrySelect(it.Id, want))
            {
                e.NewValue = CheckState.Unchecked;
                return;
            }
            _lblTrayHint.Text = "上限 62 字符；图标上最多画 3 个字符";
            RefreshTrayPreview();
        }

        private void OnTrayIconKindChanged(object sender, EventArgs e)
        {
            if (_trayUiSync) return;
            int i = _cmbTrayIcon.SelectedIndex;
            if (i < 0 || i >= TrayIcon.Kinds.Length) return;
            TrayIcon.Save(TrayIcon.Kinds[i]);
            Log.Info("托盘图标：改为 " + TrayIcon.KindLabel() + "（" + TrayIcon.Kinds[i] + "）");
            if (TrayIconChanged != null) TrayIconChanged(this, EventArgs.Empty);   // 托盘立刻重画
        }

        private void OnTrayThresholdLeave(object sender, EventArgs e)
        {
            if (_trayUiSync) return;
            TextBox box = sender as TextBox;
            if (box == null) return;
            string kind = box.Tag as string;
            string reason;
            if (!TrayIcon.SaveThresholds(kind, box.Text, out reason))
            {
                _lblTrayHint.Text = "阈值没保存：" + reason + " → 已还原为 " + TrayIcon.ThresholdText(kind);
                box.Text = TrayIcon.ThresholdText(kind);
                return;
            }
            _lblTrayHint.Text = "阈值已保存：" + (kind == "cput" ? "温度" : (kind == "cpup" ? "功耗" : "负载"))
                + " = " + TrayIcon.ThresholdText(kind) + "（图标下次刷新即生效）";
            if (TrayIconChanged != null) TrayIconChanged(this, EventArgs.Empty);
            RefreshTrayPreview();
        }

        private void SyncTrayThresholds()
        {
            for (int i = 0; i < 3; i++) _txtTrayTh[i].Text = TrayIcon.ThresholdText(TrayIcon.Kinds[i + 1]);
        }

        private void OnTrayDefaultClick(object sender, EventArgs e)
        {
            TrayText.ResetDefault();
            TrayIcon.ResetThresholds();
            SyncTrayUi();
            SyncTrayThresholds();
            Log.Info("托盘提示：已恢复默认（版本 + 性能模式 + 风扇）");
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
                string text = null;
                string err = null;
                string hint = null;
                double ms = 0;
                try
                {
                    DateTime t0 = DateTime.Now;
                    List<Reading> list = Sensors.ReadAll();
                    ms = (DateTime.Now - t0).TotalMilliseconds;

                    // 面板上的固定阅读顺序：温度 → 功耗 → 频率 → 负载 …（按用户要求）
                    List<Reading> ordered = new List<Reading>();
                    for (int i = 0; i < SensorOrder.Length; i++)
                        for (int j = 0; j < list.Count; j++)
                            if (list[j].Name == SensorOrder[i]) { ordered.Add(list[j]); break; }
                    for (int j = 0; j < list.Count; j++) if (!ordered.Contains(list[j])) ordered.Add(list[j]);

                    StringBuilder sb = new StringBuilder();
                    int ok = 0, fail = 0;
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        sb.AppendLine(ordered[i].Name + " : " + ordered[i].Value);
                        if (ordered[i].Note.Length > 0) sb.AppendLine("└ " + ordered[i].Note);
                        if (ordered[i].Ok) ok++; else fail++;
                    }
                    text = sb.ToString();
                    hint = "可用 " + ok.ToString(CultureInfo.InvariantCulture)
                         + " 项 · 未实现 " + fail.ToString(CultureInfo.InvariantCulture)
                         + " 项 · " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                         + " · 只读（PDH + WMI + MIFS）";
                }
                catch (Exception ex) { err = ex.Message; Log.Ex("读取传感器失败", ex); }

                if (ms > 400) Log.Warn("传感器：本轮读取耗时 " + ms.ToString("0", CultureInfo.InvariantCulture) + " ms（后台线程，不卡界面）");

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _sensorBusy = false;
                        if (err != null) { _lblSensorHint.Text = "读取失败：" + err + "（详见日志）"; return; }
                        SetRows(_pnlSensorRows, _sensorOrder, _sensorRows, text);
                        TrayText.CaptureSensorText(text);
                        _lblSensorHint.Text = hint;
                    });
                }
                catch { _sensorBusy = false; }
            });
        }

        private void OnSensorProbeClick(object sender, EventArgs e)
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
            string name = (string)((Button)sender).Tag;
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
                int lvl = (int)((Button)sender).Tag;
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
                _lblDpi.ForeColor = on ? ColOk : ColDim;
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
                SetRow(_pnlStatusRows, _statusOrder, _statusRows, "OSD 诊断", Osd.Healthy(st) ? "服务与进程正常" : "异常，见报告");
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
                _chkStartup.Text = on ? "开机自启（已启用 · 静默进托盘）" : "开机自启（计划任务 · 免 UAC · 静默进托盘）";
                _suppress = prev;
            }
            catch (Exception ex) { Log.Ex("查询开机自启状态失败", ex); }
        }

        private void RefreshOsdState(bool force)
        {
            if (!force && (DateTime.Now - _lastOsdCheck).TotalSeconds < 8) return;
            _lastOsdCheck = DateTime.Now;
            Osd.State s = Osd.Query();
            _osdStatus = Osd.StatusLine(s);
            _lblOsd.Text = _osdStatus;
            _lblOsd.ForeColor = Osd.Healthy(s) ? ColOk : ColWarn;
            RefreshDpiState();
        }

        public void RefreshAll()
        {
            _suppress = true;
            StringBuilder sb = new StringBuilder();
            bool anyOk = false;
            List<string> missing = new List<string>();

            // ── 性能模式
            int? pm = Mifs.GetByte(Mifs.FnPerMode);
            if (pm.HasValue)
            {
                anyOk = true;
                int mv = pm.Value;
                for (int i = 0; i < _btnMode.Length; i++)
                {
                    bool cur = ModeMap.Value(ModeMap.Order[i]) == mv;
                    _btnMode[i].Enabled = true;
                    _btnMode[i].Text = (cur ? "\u25CF " : "\u25CB ") + ModeMap.Order[i];
                    _btnMode[i].Font = cur ? _fontBold : _fontUi;
                }
                sb.AppendLine("性能模式     : " + ModeMap.Label(mv));
            }
            else
            {
                for (int i = 0; i < _btnMode.Length; i++)
                {
                    _btnMode[i].Enabled = false;
                    _btnMode[i].Text = ModeMap.Order[i] + "（未实现）";
                }
                missing.Add("性能模式");
                sb.AppendLine("性能模式     : 未实现");
            }

            // ── 风扇转速
            int[] fans = Mifs.GetFans();
            if (fans != null)
            {
                anyOk = true;
                _lblFan.ForeColor = SystemColors.ControlText;
                _lblFan.Text = string.Format(CultureInfo.InvariantCulture,
                    "风扇1  {0} RPM      风扇2  {1} RPM{2}",
                    fans[0], fans[1],
                    fans[2] > 0 ? string.Format(CultureInfo.InvariantCulture, "      风扇3  {0} RPM", fans[2]) : "");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "风扇1        : {0} RPM", fans[0]));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "风扇2        : {0} RPM", fans[1]));
            }
            else
            {
                _fanBoostUsable = false;
                _lblFan.ForeColor = ColDim;
                _lblFan.Text = "风扇转速：本机未实现";
                missing.Add("风扇转速");
                sb.AppendLine("风扇转速     : 未实现");
            }

            // ── 风扇满速（可写开关，靠能力缓存 + 写后读回判定；结论与当时的供电类型绑定）
            bool boostReadable = false;
            int acType = Mifs.GetByte(Mifs.FnAcType) ?? -1;
            _acTypeNow = acType;
            int? mxs = Mifs.GetByte(Mifs.FnMaxFanSwitch);
            if (mxs.HasValue)
            {
                boostReadable = true;
                _fanBoostOn = mxs.Value == 1;
            }
            bool capsValid = Caps.ValidFor(acType);
            if (!capsValid)
            {
                _fanBoostUsable = boostReadable;   // 未检测（或换了电源）：允许点一次，点后按读回结果定性
                _btnBoost.Enabled = boostReadable;
                _btnBoost.Text = boostReadable
                    ? "风扇满速：" + (_fanBoostOn ? "开" : "关") + "（未检测）"
                    : "风扇满速：未实现";
                _btnBoost.Font = _fontUi;
                sb.AppendLine("风扇满速     : " + (boostReadable ? (_fanBoostOn ? "开" : "关") + "（未检测，点击后自动判定）" : "未实现"));
            }
            else if (Caps.FanBoost.Value && boostReadable)
            {
                _fanBoostUsable = true;
                _btnBoost.Enabled = true;
                _btnBoost.Text = "风扇满速：" + (_fanBoostOn ? "开" : "关");
                _btnBoost.Font = _fanBoostOn ? _fontBold : _fontUi;
                sb.AppendLine("风扇满速     : " + (_fanBoostOn ? "开" : "关"));
            }
            else
            {
                _fanBoostUsable = false;
                _btnBoost.Enabled = false;
                _btnBoost.Text = "风扇满速：未实现";
                _btnBoost.Font = _fontUi;
                if (acType == 1)
                {
                    // 上游驱动文档：Type-C(PD) 供电下性能/满速模式与风扇满速被硬件禁用
                    _btnBoost.Text = "风扇满速（Type-C 供电下被禁用）";
                    sb.AppendLine("风扇满速     : 被电源类型禁用（Type-C 供电）");
                }
                else
                {
                    missing.Add("风扇满速");
                    sb.AppendLine("风扇满速     : 未实现（EC 忽略写入）");
                }
            }

            // ── Fn 锁
            int? fnl = Mifs.GetByte(Mifs.FnFnLock);
            if (fnl.HasValue)
            {
                _chkFn.Enabled = true;
                _chkFn.Text = "Fn 锁";
                _chkFn.Checked = fnl.Value == 1;
                sb.AppendLine("Fn 锁        : " + (_chkFn.Checked ? "开" : "关"));
            }
            else
            {
                _chkFn.Enabled = false;
                _chkFn.Text = "Fn 锁（未实现）";
                missing.Add("Fn 锁");
                sb.AppendLine("Fn 锁        : 未实现");
            }

            // ── 触控板锁
            int? tpl = Mifs.GetByte(Mifs.FnTpLock);
            if (tpl.HasValue)
            {
                _chkTp.Enabled = true;
                _chkTp.Text = "触控板锁定";
                _chkTp.Checked = tpl.Value == 1;
                sb.AppendLine("触控板锁     : " + (_chkTp.Checked ? "已锁定" : "正常"));
            }
            else
            {
                _chkTp.Enabled = false;
                _chkTp.Text = "触控板锁（未实现）";
                missing.Add("触控板锁");
                sb.AppendLine("触控板锁     : 未实现");
            }

            // ── 键盘背光
            int? kbd = Mifs.GetByte(Mifs.FnRgbBright);
            _gbKbd.Text = kbd.HasValue ? "键盘背光亮度" : "键盘背光亮度（未实现）";
            for (int i = 0; i < _btnKbd.Length; i++)
            {
                if (!kbd.HasValue)
                {
                    _btnKbd[i].Enabled = false;
                    _btnKbd[i].Text = "—";
                    continue;
                }
                _btnKbd[i].Enabled = true;
                _btnKbd[i].Text = i.ToString(CultureInfo.InvariantCulture);
                bool cur = kbd.Value == i;
                _btnKbd[i].Font = cur ? _fontBold : _fontUi;
            }
            if (kbd.HasValue)
            {
                sb.AppendLine("键盘背光     : 等级 " + kbd.Value.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                missing.Add("键盘背光");
                sb.AppendLine("键盘背光     : 未实现");
            }

            // ── 供电
            int? ac = Mifs.GetByte(Mifs.FnAcType);
            if (ac.HasValue)
            {
                string t = ac.Value == 1 ? "外接电源" : (ac.Value == 0 ? "电池供电" : "原始值 " + ac.Value);
                sb.AppendLine("供电         : " + t);
            }

            // ── MIFS 的 CPU 温度/功率（功能号 22/23，本机恒为 0）
            // **故意不计入顶部"未实现"清单**：CPU 温度/功耗已由 AMD 通道（ADL PMLog）与 PDH 提供，
            // 把"MIFS CPU 温度"列出来只会让人误读成"这台机器读不到 CPU 温度"。
            // 只探测一次、写一条日志备查（面板上由传感器区负责显示）。
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
                    + (ctOk && cpOk ? " → 可用" : " → 该功能号在本机未实现；面板改用 AMD 通道（ADL PMLog）+ PDH"));
            }

            // ── OSD
            RefreshOsdState(false);
            sb.AppendLine("OSD          : " + _osdStatus);

            // ── 开机自启
            if ((DateTime.Now - _lastStartupCheck).TotalSeconds > 20)
            {
                _lastStartupCheck = DateTime.Now;
                RefreshStartupState();
            }
            sb.AppendLine("开机自启     : " + (_chkStartup.Checked ? "已启用（计划任务）" : "未启用"));

            // ── 顶部状态条
            if (anyOk)
            {
                _lblHeader.ForeColor = ColOk;
                string head = "OpenMIFS v" + MifsApp.VersionText + " · 接口正常 · " + (MifsApp.IsElevated ? "已提权" : "未提权") + " · 托盘常驻"
                            + (Log.Available ? " · 日志已开启" : " · 日志不可用（只读目录？）");
                // 没有任何缺失就只留一行（"无，全部功能可用" 属于噪音）
                if (missing.Count > 0)
                {
                    string miss = string.Join("、", missing.ToArray());
                    if (miss.Length > 56) miss = miss.Substring(0, 56) + "…";
                    head += "\r\n本机不支持：" + miss + "（其余功能正常）";
                }
                _lblHeader.Text = head;
            }
            else
            {
                _lblHeader.ForeColor = ColWarn;
                _lblHeader.Text = "接口不可用 · " + Mifs.LastError
                                + "\r\n请确认以管理员身份运行，且本机 BIOS 暴露了 MIFS 接口";
                if (_lastLoggedError != Mifs.LastError)
                {
                    _lastLoggedError = Mifs.LastError;
                    Log.Error("MIFS 接口不可用：" + Mifs.LastError);
                }
            }


            SetRows(_pnlStatusRows, _statusOrder, _statusRows, sb.ToString());
            TrayText.CaptureStatusText(sb.ToString());
            if (_gbTray != null) RefreshTrayPreview();

            _suppress = false;
            if (!_firstRefreshDone)
            {
                _firstRefreshDone = true;
                Log.Info("首次刷新完成：接口可用=" + anyOk + " 未实现=" + (missing.Count == 0 ? "无" : string.Join("、", missing.ToArray()))
                    + " OSD状态=" + _osdStatus);
            }
            if (StateChanged != null) StateChanged(this, EventArgs.Empty);

            // 传感器只在「传感器」页可见时读取（PDH 有开销，没必要后台一直采）
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
                // 所以先以"最小化 + 不显示在任务栏 + 完全透明"的方式 Show 一次，再收进托盘。
                _form.WindowState = FormWindowState.Minimized;
                _form.ShowInTaskbar = false;
                _form.Opacity = 0;
                _form.Show();
                _form.HideToTray();
                _form.Opacity = 1;
                _form.WindowState = FormWindowState.Normal;
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
                catch (Exception ex) { Log.Ex("主循环异常退出", ex); throw; }
                finally { Log.Info("================ OpenMIFS 退出 ================"); }
            }
        }
    }
}
