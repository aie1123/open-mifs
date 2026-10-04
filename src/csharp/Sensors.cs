// OpenMIFS — 传感器读取层
// =====================================================================
// 设计原则：**零驱动、零依赖**。
//   · 只走 Windows 自带通道：性能计数器（PDH）、WMI/CIM、MIFS、存储类驱动
//   · 不加载、不捆绑任何第三方内核组件（WinRing0 在微软易受攻击驱动黑名单里）
//
// 已实测的数据源（本机 MECHREVO WUJIE14 PRO / R7-7840HS / 780M / Win11 24H2）：
//   CPU 封装功耗  \Energy Meter(rapl_package0_pkg)\Power           单位 mW（负载标定过）
//   CPU 每核功耗  \Energy Meter(rapl_package0_coreN_core)\Power
//   SoC / VDDCR   \Energy Meter(vddcr_soc power / vddcr_vdd power)\Power
//   插槽功耗      \Energy Meter(current socket power)\Power
//   CPU 频率/负载 \Processor Information(_Total)\Processor Frequency × % Processor Performance
//   热区温度      \Thermal Zone Information(_tz.tz01)\Temperature  单位 K
//   高精度温度    \Thermal Zone Information(*)\High Precision Temperature  单位 0.1 K
//   降频原因      \Thermal Zone Information(*)\Throttle Reasons    位域
//   GPU 利用率    \GPU Engine(*)\Utilization Percentage（按引擎类型汇总）
//   GPU 显存      \GPU Adapter Memory(*)\Dedicated Usage
//   内存/磁盘/电池 WMI；风扇 MIFS fn=13
//
// 拿不到的（需要内核驱动，本项目刻意不做）：CPU die 温度 Tctl/Tdie、主板/VRM/内存温度。
// 详见 docs/SENSORS.md。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace OpenMIFS
{
    /// <summary>一条传感器读数。</summary>
    internal sealed class Reading
    {
        public string Group;
        public string Name;
        public string Value;
        public string Note = "";
        public bool Ok = true;

        public Reading(string group, string name, string value, bool ok, string note)
        {
            Group = group; Name = name; Value = value; Ok = ok; Note = note;
        }
    }

    internal static class Sensors
    {
        // ─────────────────────────────────────────────── PDH 计数器缓存
        private sealed class Pc
        {
            public PerformanceCounter Counter;
            public string Path;
        }

        private static readonly Dictionary<string, Pc> _cache = new Dictionary<string, Pc>();
        private static readonly object _lock = new object();
        private static string _lastError = "";
        private static bool _primed;

        public static string LastError { get { return _lastError; } }

        /// <summary>第一次读取前必须预热：速率类计数器（Power / % Processor Time 等）
        /// 需要两次采样之间有一个真实时间间隔，否则第一次读回来是 0。
        /// 做法是一次性把所有要用的计数器建好并各采一次，然后统一等一个采样窗口。</summary>
        private static void PrimeAll()
        {
            if (_primed) return;
            _primed = true;
            ResolvePowerInstances();
            string zone = ResolveZoneInstance();

            if (_powerInstance != null) Get("Energy Meter", "Power", _powerInstance);
            if (_socketInstance != null) Get("Energy Meter", "Power", _socketInstance);
            string[] em = Instances("Energy Meter");
            for (int i = 0; i < em.Length; i++)
            {
                string n = em[i].ToLowerInvariant();
                if (Regex.IsMatch(n, @"^rapl_package\d+_core\d+_core$") || n == "vddcr_vdd power" || n == "vddcr_soc power")
                    Get("Energy Meter", "Power", em[i]);
            }
            Get("Processor Information", "Processor Frequency", "_Total");
            Get("Processor Information", "% Processor Performance", "_Total");
            Get("Processor Information", "% Processor Time", "_Total");
            if (zone != null)
            {
                Get("Thermal Zone Information", "Temperature", zone);
                Get("Thermal Zone Information", "High Precision Temperature", zone);
                Get("Thermal Zone Information", "Throttle Reasons", zone);
            }
            Thread.Sleep(700);   // 采样窗口：等一次 PDH 间隔，Power 之类的计数器才有有效值
            Log.Info("传感器：计数器预热完成（" + _cache.Count.ToString(CultureInfo.InvariantCulture) + " 个），已等待 700 ms 采样窗口");
        }

        /// <summary>取一个计数器（带缓存 + 预热）。instance 为 null 表示无实例计数器。</summary>
        private static PerformanceCounter Get(string category, string counter, string instance)
        {
            string key = category + "|" + counter + "|" + (instance == null ? "" : instance);
            lock (_lock)
            {
                Pc entry;
                if (_cache.TryGetValue(key, out entry)) return entry.Counter;
                PerformanceCounter pc = null;
                try
                {
                    pc = instance == null
                        ? new PerformanceCounter(category, counter, true)
                        : new PerformanceCounter(category, counter, instance, true);
                    pc.NextValue();   // 预热：速率类计数器需要两次采样
                }
                catch (Exception ex)
                {
                    _lastError = category + "\\" + counter + "： " + ex.Message;
                    pc = null;
                }
                _cache[key] = new Pc();
                _cache[key].Counter = pc;
                _cache[key].Path = category + "\\" + counter + "(" + (instance == null ? "" : instance) + ")";
                return pc;
            }
        }

        private static float? Next(string category, string counter, string instance)
        {
            PerformanceCounter pc = Get(category, counter, instance);
            if (pc == null) return null;
            try { return pc.NextValue(); }
            catch (Exception ex) { _lastError = pc.CounterName + "： " + ex.Message; return null; }
        }

        private static string[] Instances(string category)
        {
            try { return new PerformanceCounterCategory(category).GetInstanceNames(); }
            catch (Exception ex) { _lastError = "枚举 " + category + " 实例失败： " + ex.Message; return new string[0]; }
        }

        /// <summary>读 GPU 性能计数器的「已格式化」WMI 类。
        /// 用 WMI 而不是 PerformanceCounterCategory.ReadCategory()：后者只给原始值，
        /// 百分比类计数器（RAW_FRACTION）拿不到 Base，算不出百分比。</summary>
        private static List<KeyValuePair<string, double>> PerfFormatted(string className, string valueProp)
        {
            // GPU Engine 有两三百个实例（每个进程每个引擎一条），能过滤就过滤：只取 > 0 的。
            // 有些 WMI 性能提供程序不支持在已格式化类上带 WHERE，失败就退回不带条件的查询。
            List<KeyValuePair<string, double>> list = PerfFormattedRaw(className, valueProp,
                "SELECT Name," + valueProp + " FROM " + className + " WHERE " + valueProp + " > 0");
            if (list == null)
                list = PerfFormattedRaw(className, valueProp, "SELECT Name," + valueProp + " FROM " + className);
            return list == null ? new List<KeyValuePair<string, double>>() : list;
        }

        private static List<KeyValuePair<string, double>> PerfFormattedRaw(string className, string valueProp, string wql)
        {
            try
            {
                List<KeyValuePair<string, double>> list = new List<KeyValuePair<string, double>>();
                ManagementObjectSearcher q = new ManagementObjectSearcher("root\\cimv2", wql);
                foreach (ManagementBaseObject o in q.Get())
                    list.Add(new KeyValuePair<string, double>(S(o["Name"]), Num(o[valueProp])));
                return list;
            }
            catch (Exception ex) { _lastError = className + "： " + ex.Message; return null; }
        }

        // 缓存发现的实例名（机型相关，不能写死）
        private static string _powerInstance;      // 封装功耗
        private static string _socketInstance;     // 插槽功耗
        private static bool _powerResolved;
        private static string _zoneInstance;
        private static bool _zoneResolved;
        private static string _memTypeCache;

        private static void ResolvePowerInstances()
        {
            if (_powerResolved) return;
            _powerResolved = true;
            string[] names = Instances("Energy Meter");
            string bestPkg = null;
            string bestSocket = null;
            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i].ToLowerInvariant();
                if (bestPkg == null && Regex.IsMatch(n, @"^rapl_package\d+_pkg$")) bestPkg = names[i];
                if (bestSocket == null && n == "current socket power") bestSocket = names[i];
            }
            if (bestSocket == null)
                for (int i = 0; i < names.Length; i++)
                    if (names[i].ToLowerInvariant() == "apu power") { bestSocket = names[i]; break; }
            _powerInstance = bestPkg;
            _socketInstance = bestSocket;
            Log.Info("传感器：Energy Meter 实例 → 封装=" + (bestPkg == null ? "无" : bestPkg)
                + " 插槽=" + (bestSocket == null ? "无" : bestSocket)
                + "（共 " + names.Length.ToString(CultureInfo.InvariantCulture) + " 个实例）");
        }

        private static string ResolveZoneInstance()
        {
            if (_zoneResolved) return _zoneInstance;
            _zoneResolved = true;
            string[] names = Instances("Thermal Zone Information");
            if (names.Length > 0) _zoneInstance = names[0];
            Log.Info("传感器：热区实例 → " + (names.Length == 0 ? "无" : string.Join("、", names)));
            return _zoneInstance;
        }

        // ─────────────────────────────── GPU / CPU 温度（ADL2 PMLog，AMD 官方用户态通道）
        // 这是 AMD Software: Adrenalin Edition「性能 → 指标」页读的同一套数据：
        // atiadlxx.dll（随显卡驱动安装，用户态 DLL，不需要内核驱动、不需要管理员）
        //   ADL2_Main_Control_Create → ADL2_New_QueryPMLogData_Get(adapter, ADLPMLogDataOutput)
        // 结构：{ int size; { int size; int value; } sensors[256]; }
        // 传感器编号来自 ADL 官方 adl_defines.h 的 ADL_PMLOG_SENSORS 枚举。
        // 本机实测（Radeon 780M，Win11）：温度/功耗/频率全部有值，
        // 负载下 GPU 52→56 ℃、CPU 51→57 ℃、SoC 54→57 ℃ 同向变化（是真实传感器，不是常数）。
        internal static class AdlPmlog
        {
            private delegate IntPtr AllocCb(int size);

            [DllImport("atiadlxx.dll")]
            private static extern int ADL2_Main_Control_Create(AllocCb cb, int enumConnected, out IntPtr ctx);
            [DllImport("atiadlxx.dll")]
            private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr ctx, out int num);
            [DllImport("atiadlxx.dll")]
            private static extern int ADL2_New_QueryPMLogData_Get(IntPtr ctx, int adapterIndex, IntPtr data);

            // ADL_PMLOG_SENSORS（官方 adl_defines.h）
            public const int ClkGfx = 1;        // GFXCLK  MHz
            public const int ClkMem = 2;        // MEMCLK  MHz
            public const int SocVoltage = 16;   // mV
            public const int GfxActivity = 19;  // %
            public const int AsicPower = 23;    // W
            public const int TempGfx = 28;      // ℃（核显温度）
            public const int TempSoc = 29;      // ℃
            public const int TempCpu = 32;      // ℃（CPU die，来自 SMU）
            public const int ClkCpu = 34;       // MHz

            private const int SensorCount = 256;
            private const int BufSize = 4 + SensorCount * 8;

            private static AllocCb _keepAlive;          // 回调必须保活
            private static IntPtr _ctx = IntPtr.Zero;
            private static IntPtr _buf = IntPtr.Zero;
            private static bool _tried;
            private static bool _ok;
            private static int _adapter = -1;
            private static string _note = "未探测";

            public static bool Available { get { return _ok; } }
            public static string Note { get { return _note; } }

            private static IntPtr Alloc(int size) { return Marshal.AllocHGlobal(size); }

            /// <summary>初始化（惰性，只做一次）。失败也只是一条说明，不影响其它传感器。</summary>
            public static void Init()
            {
                if (_tried) return;
                _tried = true;
                try
                {
                    _keepAlive = new AllocCb(Alloc);
                    int rc = ADL2_Main_Control_Create(_keepAlive, 1, out _ctx);
                    if (rc != 0) { _note = "ADL2 初始化失败 rc=" + rc.ToString(CultureInfo.InvariantCulture); return; }
                    int n = 0;
                    ADL2_Adapter_NumberOfAdapters_Get(_ctx, out n);
                    _buf = Marshal.AllocHGlobal(BufSize);

                    // 找一个真能读出温度的适配器（本机 5 个适配器数据相同，取第一个有效的即可）
                    for (int a = 0; a < n; a++)
                    {
                        int t = ReadAt(a, TempGfx);
                        if (t > 0 && t < 120) { _ok = true; _adapter = a; break; }
                    }
                    _note = _ok
                        ? "ADL2 PMLog（适配器 " + _adapter.ToString(CultureInfo.InvariantCulture)
                          + " / 共 " + n.ToString(CultureInfo.InvariantCulture) + "，AMD 官方用户态通道）"
                        : "ADL2 PMLog 可用但没有温度数据（共 " + n.ToString(CultureInfo.InvariantCulture) + " 个适配器）";
                    Log.Info("传感器：ADL2 PMLog → " + _note);
                }
                catch (Exception ex)
                {
                    _note = "加载 atiadlxx.dll 失败：" + ex.Message;
                    Log.Ex("传感器：ADL2 PMLog 初始化异常", ex);
                }
            }

            private static int ReadAt(int adapter, int sensor)
            {
                if (_ctx == IntPtr.Zero || _buf == IntPtr.Zero) return -1;
                try
                {
                    for (int i = 0; i < BufSize; i++) Marshal.WriteByte(_buf, i, 0);
                    if (ADL2_New_QueryPMLogData_Get(_ctx, adapter, _buf) != 0) return -1;
                    return Marshal.ReadInt32(_buf, 4 + sensor * 8 + 4);
                }
                catch { return -1; }
            }

            /// <summary>读一个传感器；不可用返回 -1。</summary>
            public static int Get(int sensor)
            {
                if (!_ok) return -1;
                return ReadAt(_adapter, sensor);
            }
        }

        // ─────────────────────────────────────────────── GPU（ADL，用户态，不捆绑二进制）
        private static bool _adlTried;
        private static bool _adlOk;
        private static string _adlNote = "未探测";
        private static int _adlAdapter = -1;
        private static bool _adlTempMilliC = true;

        [StructLayout(LayoutKind.Sequential)]
        private struct ADLTemperature { public int iSize; public int iTemperature; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ADLPMActivity
        {
            public int iSize;
            public int iEngineClock;
            public int iMemoryClock;
            public int iVddc;
            public int iActivityPercent;
            public int iCurrentPerformanceLevel;
            public int iCurrentBusSpeed;
            public int iCurrentBusLanes;
            public int iMaximumBusLanes;
            public int iReserved;
        }

        private delegate IntPtr AdlAlloc(int size);

        private static class Adl
        {
            [DllImport("atiadlxx.dll")] public static extern int ADL_Main_Control_Create(AdlAlloc cb, int enumConnected);
            [DllImport("atiadlxx.dll")] public static extern int ADL_Main_Control_Destroy();
            [DllImport("atiadlxx.dll")] public static extern int ADL_Adapter_NumberOfAdapters_Get(out int num);
            [DllImport("atiadlxx.dll")] public static extern int ADL_Overdrive5_Temperature_Get(int adapter, int thermalController, out ADLTemperature t);
            [DllImport("atiadlxx.dll")] public static extern int ADL_Overdrive5_CurrentActivity_Get(int adapter, out ADLPMActivity a);
            [DllImport("atiadlxx.dll")] public static extern int ADL_Overdrive6_Temperature_Get(int adapter, out int temperature);
        }

        private static AdlAlloc _adlAllocKeepAlive;   // 必须保活，否则委托被 GC 回收导致回调崩溃

        private static IntPtr AdlAllocImpl(int size) { return Marshal.AllocHGlobal(size); }

        private static void AdlInit()
        {
            if (_adlTried) return;
            _adlTried = true;
            try
            {
                _adlAllocKeepAlive = new AdlAlloc(AdlAllocImpl);
                int rc = Adl.ADL_Main_Control_Create(_adlAllocKeepAlive, 1);
                if (rc != 0) { _adlNote = "ADL_Main_Control_Create 返回 " + rc.ToString(CultureInfo.InvariantCulture); Log.Warn("传感器：ADL 初始化失败（" + _adlNote + "）"); return; }

                int count = 0;
                rc = Adl.ADL_Adapter_NumberOfAdapters_Get(out count);
                if (rc != 0 || count <= 0) { _adlNote = "取适配器数量失败（" + rc.ToString(CultureInfo.InvariantCulture) + "）"; return; }

                for (int i = 0; i < count; i++)
                {
                    ADLTemperature t = new ADLTemperature();
                    t.iSize = Marshal.SizeOf(typeof(ADLTemperature));
                    int trc = Adl.ADL_Overdrive5_Temperature_Get(i, 0, out t);
                    if (trc == 0 && t.iTemperature > 0)
                    {
                        _adlOk = true;
                        _adlAdapter = i;
                        _adlTempMilliC = true;
                        _adlNote = "ADL 适配器 " + i.ToString(CultureInfo.InvariantCulture) + "（共 " + count.ToString(CultureInfo.InvariantCulture) + "），Overdrive5";
                        Log.Info("传感器：ADL 可用，" + _adlNote + "，温度 " + (t.iTemperature / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " ℃");
                        return;
                    }
                }
                // Overdrive5 拿不到就再试 Overdrive6（部分新驱动只实现了 6）
                for (int i = 0; i < count; i++)
                {
                    int temp = 0;
                    if (Adl.ADL_Overdrive6_Temperature_Get(i, out temp) == 0 && temp > 0)
                    {
                        _adlOk = true;
                        _adlAdapter = i;
                        _adlTempMilliC = temp > 1000;   // OD6 有的驱动给摄氏度，有的给毫度
                        _adlNote = "ADL 适配器 " + i.ToString(CultureInfo.InvariantCulture) + "（共 " + count.ToString(CultureInfo.InvariantCulture) + "），Overdrive6";
                        Log.Info("传感器：ADL(OD6) 可用，" + _adlNote + "，原始温度值 " + temp.ToString(CultureInfo.InvariantCulture));
                        return;
                    }
                }
                _adlNote = "ADL 能加载，但 " + count.ToString(CultureInfo.InvariantCulture) + " 个适配器都取不到温度（核显多不支持 OD5/OD6 温度）";
                Log.Warn("传感器：" + _adlNote);
            }
            catch (Exception ex)
            {
                _adlNote = "加载 atiadlxx.dll 失败：" + ex.Message;
                Log.Ex("传感器：ADL 探测异常", ex);
            }
        }

        // ─────────────────────────────────────────────── 对外：读全部传感器
        public static List<Reading> ReadAll()
        {
            List<Reading> list = new List<Reading>();
            _lastError = "";
            PrimeAll();
            ResolvePowerInstances();
            string zone = ResolveZoneInstance();

            // ── CPU
            float? pkg = _powerInstance == null ? (float?)null : Next("Energy Meter", "Power", _powerInstance);
            string pkgNote = _powerInstance == null ? "本机没有 rapl_packageN_pkg 计数器" : "PDH Energy Meter / " + _powerInstance;

            float? freq = Next("Processor Information", "Processor Frequency", "_Total");
            float? perf = Next("Processor Information", "% Processor Performance", "_Total");
            if (freq.HasValue && perf.HasValue)
                list.Add(new Reading("CPU", "CPU 频率",
                    string.Format(CultureInfo.InvariantCulture, "{0:0.00} GHz", freq.Value * perf.Value / 100.0 / 1000.0),
                    true, string.Format(CultureInfo.InvariantCulture, "估算：{0:0} MHz × {1:0.0}%", freq.Value, perf.Value)));
            else
                list.Add(new Reading("CPU", "CPU 频率", "未实现", false, "Processor Information 计数器不可用"));

            float? load = Next("Processor Information", "% Processor Time", "_Total");
            if (load.HasValue) list.Add(new Reading("CPU", "CPU 负载", string.Format(CultureInfo.InvariantCulture, "{0:0.0} %", load.Value), true, "PDH % Processor Time"));

            // 每核 / 供电域 / 插槽：细节太多，塞进「CPU 功耗」那一行的鼠标提示，面板上不占行
            string[] em = Instances("Energy Meter");
            List<string> coreNames = new List<string>();
            for (int i = 0; i < em.Length; i++)
                if (Regex.IsMatch(em[i].ToLowerInvariant(), @"^rapl_package\d+_core\d+_core$")) coreNames.Add(em[i]);
            coreNames.Sort();
            if (coreNames.Count > 0)
            {
                StringBuilder cs = new StringBuilder();
                int shown = 0;
                for (int i = 0; i < coreNames.Count; i++)
                {
                    float? v = Next("Energy Meter", "Power", coreNames[i]);
                    if (!v.HasValue) continue;
                    if (shown > 0) cs.Append(" / ");
                    cs.Append(string.Format(CultureInfo.InvariantCulture, "{0:0.00}", v.Value / 1000.0));
                    shown++;
                }
                if (shown > 0) pkgNote += "；每核功耗 W " + cs.ToString();
            }
            string s1 = null, s2 = null;
            for (int i = 0; i < em.Length; i++)
            {
                string n = em[i].ToLowerInvariant();
                if (n == "vddcr_vdd power") s1 = em[i];
                if (n == "vddcr_soc power") s2 = em[i];
            }
            if (s1 != null || s2 != null)
            {
                float? v1 = s1 == null ? (float?)null : Next("Energy Meter", "Power", s1);
                float? v2 = s2 == null ? (float?)null : Next("Energy Meter", "Power", s2);
                pkgNote += "；供电域 VDDCR " + (v1.HasValue ? W(v1.Value) : "—") + " / SoC " + (v2.HasValue ? W(v2.Value) : "—");
            }
            if (_socketInstance != null)
            {
                float? sock = Next("Energy Meter", "Power", _socketInstance);
                if (sock.HasValue) pkgNote += "；插槽 " + W(sock.Value);
            }
            if (pkg.HasValue)
                list.Add(new Reading("CPU", "CPU 功耗", W(pkg.Value), true, pkgNote));
            else
                list.Add(new Reading("CPU", "CPU 功耗", "未实现", false, pkgNote));

            // ── 温度：优先用 AMD 官方通道（ADL PMLog），它给的是真 die 温度；
            //         拿不到才退回 ACPI 热区（EC 上报的封装邻区，偏低且滞后）
            AdlPmlog.Init();
            int tCpu = AdlPmlog.Get(AdlPmlog.TempCpu);
            int tGfx = AdlPmlog.Get(AdlPmlog.TempGfx);
            int tSoc = AdlPmlog.Get(AdlPmlog.TempSoc);

            if (tCpu > 0 && tCpu < 120)
            {
                string cpuNote = "SMU 传感器 TEMPERATURE_CPU（#"
                    + AdlPmlog.TempCpu.ToString(CultureInfo.InvariantCulture) + "），" + AdlPmlog.Note;
                if (tSoc > 0 && tSoc < 120) cpuNote += "；TEMPERATURE_SOC（#" + AdlPmlog.TempSoc.ToString(CultureInfo.InvariantCulture) + "）=" + tSoc.ToString(CultureInfo.InvariantCulture) + " ℃";
                if (zone != null)
                {
                    float? th = Next("Thermal Zone Information", "Throttle Reasons", zone);
                    if (th.HasValue) cpuNote += "；ACPI 降频原因=" + th.Value.ToString("0", CultureInfo.InvariantCulture) + (th.Value == 0 ? "（正常）" : "（正在降频！）");
                }
                list.Add(new Reading("温度", "CPU 温度", tCpu.ToString(CultureInfo.InvariantCulture) + " ℃", true, cpuNote));
            }
            else if (zone != null)
            {
                float? t = Next("Thermal Zone Information", "Temperature", zone);
                if (t.HasValue)
                    list.Add(new Reading("温度", "热区温度", string.Format(CultureInfo.InvariantCulture, "{0:0.0} ℃", t.Value - 273.15f), true,
                        "ACPI 热区 " + zone + "（EC 上报的封装邻区，比 die 温度低且滞后）"));
            }
            else
            {
                list.Add(new Reading("温度", "CPU 温度", "未实现", false, "ADL PMLog 与 ACPI 热区都不可用"));
            }


            // ── GPU
            List<KeyValuePair<string, double>> gpu = PerfFormatted(
                "Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine", "UtilizationPercentage");
            if (gpu.Count > 0)
            {
                Dictionary<string, double> byType = new Dictionary<string, double>();
                double total = 0;
                for (int i = 0; i < gpu.Count; i++)
                {
                    if (gpu[i].Value <= 0.01) continue;
                    string type = "其它";
                    int k = gpu[i].Key.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                    if (k >= 0) type = gpu[i].Key.Substring(k + 8);
                    double cur;
                    byType.TryGetValue(type, out cur);
                    byType[type] = cur + gpu[i].Value;
                    total += gpu[i].Value;
                }
                string busiest = "—";
                double best = 0;
                foreach (KeyValuePair<string, double> kv in byType)
                    if (kv.Value > best) { best = kv.Value; busiest = kv.Key; }
                list.Add(new Reading("GPU", "GPU 利用率",
                    string.Format(CultureInfo.InvariantCulture, "{0} {1:0.0} %（合计 {2:0.0} %）", busiest, best, total),
                    true, "WMI GPU Engine（Windows 原生）"));
            }
            else
            {
                list.Add(new Reading("GPU", "GPU 利用率", "未实现", false, "GPU Engine 计数器不可用"));
            }

            List<KeyValuePair<string, double>> gm = PerfFormatted(
                "Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory", "DedicatedUsage");
            double memSum = 0;
            for (int i = 0; i < gm.Count; i++) if (gm[i].Value > 0) memSum += gm[i].Value;
            if (gm.Count > 0)
                list.Add(new Reading("GPU", "GPU 显存", string.Format(CultureInfo.InvariantCulture, "{0:0} MB", memSum / 1048576.0), true, "WMI GPU Adapter Memory"));

            // GPU 温度 / 功耗 / 频率：优先 ADL2 PMLog（Adrenalin 同源，实测本机核显有值）
            if (tGfx > 0 && tGfx < 120)
                list.Add(new Reading("GPU", "GPU 温度", tGfx.ToString(CultureInfo.InvariantCulture) + " ℃", true,
                    "SMU 传感器 TEMPERATURE_GFX（#" + AdlPmlog.TempGfx.ToString(CultureInfo.InvariantCulture) + "），" + AdlPmlog.Note));

            int gfxClk = AdlPmlog.Get(AdlPmlog.ClkGfx);
            int memClk = AdlPmlog.Get(AdlPmlog.ClkMem);
            if (gfxClk > 0)
            {
                string clkNote = "ADL_PMLOG_CLK_GFXCLK（核显，空闲约 800 MHz，负载时上boost）";
                if (memClk > 0) clkNote += "；显存 ADL_PMLOG_CLK_MEMCLK=" + memClk.ToString(CultureInfo.InvariantCulture) + " MHz（DDR5-5600 的一半）";
                list.Add(new Reading("GPU", "GPU 频率", gfxClk.ToString(CultureInfo.InvariantCulture) + " MHz", true, clkNote));
            }

            int asicPower = AdlPmlog.Get(AdlPmlog.AsicPower);
            if (asicPower > 0 && asicPower < 300)
                list.Add(new Reading("GPU", "GPU 功耗", asicPower.ToString(CultureInfo.InvariantCulture) + " W", true,
                    "ADL_PMLOG_ASIC_POWER（APU 整体，含 CPU 与核显）"));

            // ADL Overdrive5/6 作为兜底（本机核显取不到值，留着给独显机型）
            AdlInit();
            if (!(tGfx > 0) && _adlOk)
            {
                double tempC = 0;
                bool haveTemp = false;
                if (_adlNote.IndexOf("Overdrive6") >= 0)
                {
                    int raw = 0;
                    if (Adl.ADL_Overdrive6_Temperature_Get(_adlAdapter, out raw) == 0 && raw > 0)
                    {
                        tempC = _adlTempMilliC ? raw / 1000.0 : raw;
                        haveTemp = true;
                    }
                }
                else
                {
                    ADLTemperature t = new ADLTemperature();
                    t.iSize = Marshal.SizeOf(typeof(ADLTemperature));
                    if (Adl.ADL_Overdrive5_Temperature_Get(_adlAdapter, 0, out t) == 0 && t.iTemperature > 0)
                    {
                        tempC = t.iTemperature / 1000.0;
                        haveTemp = true;
                    }
                }
                if (haveTemp)
                    list.Add(new Reading("GPU", "GPU 温度", string.Format(CultureInfo.InvariantCulture, "{0:0.0} ℃", tempC), true, "ADL Overdrive（" + _adlNote + "）"));
            }

            // ── 内存
            try
            {
                ulong totalBytes = 0;
                int modules = 0, speed = 0, smbios = 0;
                ManagementObjectSearcher q = new ManagementObjectSearcher(
                    "SELECT Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType FROM Win32_PhysicalMemory");
                foreach (ManagementBaseObject o in q.Get())
                {
                    modules++;
                    totalBytes += Convert.ToUInt64(o["Capacity"] == null ? 0 : o["Capacity"], CultureInfo.InvariantCulture);
                    int sp = Int(o["ConfiguredClockSpeed"]);
                    if (sp == 0) sp = Int(o["Speed"]);
                    if (sp > speed) speed = sp;
                    if (smbios == 0) smbios = Int(o["SMBIOSMemoryType"]);
                }
                ManagementObjectSearcher q2 = new ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem");
                ulong freeKb = 0, totalKb = 0;
                foreach (ManagementBaseObject o in q2.Get())
                {
                    totalKb = Convert.ToUInt64(o["TotalVisibleMemorySize"] == null ? 0 : o["TotalVisibleMemorySize"], CultureInfo.InvariantCulture);
                    freeKb = Convert.ToUInt64(o["FreePhysicalMemory"] == null ? 0 : o["FreePhysicalMemory"], CultureInfo.InvariantCulture);
                }
                if (modules > 0)
                {
                    _memTypeCache = MemType(smbios);
                    // 速率是"每秒百万次传输"= MT/s（DDR 的等效频率）。WMI 只给数字，
                    // 不补单位会让人以为是 MHz —— 这里显式写 MT/s，并在提示里说明换算。
                    string rate = speed > 0
                        ? " " + speed.ToString(CultureInfo.InvariantCulture) + " MT/s（≈" 
                          + (speed / 2).ToString(CultureInfo.InvariantCulture) + " MHz 实际时钟）"
                        : "";
                    list.Add(new Reading("内存", "内存规格",
                        string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB  {1}×{2:0} GB {3}{4}",
                            totalBytes / 1073741824.0, modules, totalBytes / 1073741824.0 / modules,
                            _memTypeCache, speed > 0 ? "-" + speed.ToString(CultureInfo.InvariantCulture) + " MT/s" : ""),
                        true, "WMI Win32_PhysicalMemory" + rate));
                }
                if (totalKb > 0)
                {
                    double usedGb = (totalKb - freeKb) / 1048576.0;
                    double totalGb = totalKb / 1048576.0;
                    list.Add(new Reading("内存", "内存占用",
                        string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0.0} GB（{2:0.0} %）", usedGb, totalGb, usedGb / totalGb * 100.0),
                        true, "WMI Win32_OperatingSystem"));
                }
            }
            catch (Exception ex) { Log.Ex("传感器：读内存失败", ex); }

            // ── 存储
            try
            {
                // 可靠性计数器（温度 / 磨损 / 通电时长）需要管理员权限：
                // 整表查一次，再按 DeviceId 与磁盘对上（比 WQL ASSOCIATORS 稳，ObjectId 里带引号会解析失败）
                Dictionary<string, ManagementBaseObject> rel = new Dictionary<string, ManagementBaseObject>();
                string relError = "";
                try
                {
                    ManagementObjectSearcher qr = new ManagementObjectSearcher(
                        "root\\Microsoft\\Windows\\Storage",
                        "SELECT DeviceId,Temperature,TemperatureMax,Wear,PowerOnHours FROM MSFT_StorageReliabilityCounter");
                    foreach (ManagementBaseObject c in qr.Get())
                    {
                        string did = S(c["DeviceId"]);
                        if (did.Length > 0 && !rel.ContainsKey(did)) rel[did] = c;
                    }
                    Log.Info("传感器：读取磁盘可靠性计数器成功，共 " + rel.Count.ToString(CultureInfo.InvariantCulture) + " 条");
                }
                catch (Exception exr)
                {
                    relError = exr.Message;
                    Log.Warn("传感器：磁盘可靠性计数器不可用（需要管理员）—— " + exr.Message);
                }

                ManagementObjectSearcher q = new ManagementObjectSearcher(
                    "root\\Microsoft\\Windows\\Storage",
                    "SELECT FriendlyName,MediaType,BusType,HealthStatus,Size,DeviceId FROM MSFT_PhysicalDisk");
                foreach (ManagementBaseObject o in q.Get())
                {
                    string name = S(o["FriendlyName"]);
                    string media = MediaTypeName(Int(o["MediaType"]));
                    string bus = BusTypeName(Int(o["BusType"]));
                    string health = HealthName(Int(o["HealthStatus"]));
                    double gb = Num(o["Size"]) / 1073741824.0;
                    string did = S(o["DeviceId"]);
                    string note = "WMI Storage"
                        + (bus.Length > 0 ? " / " + bus : "")
                        + (health.Length > 0 ? " / " + health : "");

                    ManagementBaseObject c2;
                    // 面板上拆成两行：磁盘（型号/容量）与 磁盘温度（温度/磨损/通电）
                    string tempText = "未实现";
                    string tempNote = note;
                    bool tempOk = false;
                    bool paired = rel.TryGetValue(did, out c2);
                    if (!paired && rel.Count > 0)
                    {
                        // DeviceId 对不上（不同 Windows 版本的取值格式不一致）→ 唯一一条就直接用
                        foreach (KeyValuePair<string, ManagementBaseObject> kv in rel)
                        {
                            c2 = kv.Value;
                            paired = true;
                            break;
                        }
                        if (paired && rel.Count > 1) paired = false;   // 多盘又对不上就不猜
                    }
                    if (paired)
                    {
                        int temp = Int(c2["Temperature"]);
                        int wear = Int(c2["Wear"]);
                        long hours = Long(c2["PowerOnHours"]);
                        tempText = (temp > 0 ? temp.ToString(CultureInfo.InvariantCulture) + " ℃" : "未知")
                                 + "  磨损 " + wear.ToString(CultureInfo.InvariantCulture)
                                 + (hours > 0 ? "  通电 " + hours.ToString(CultureInfo.InvariantCulture) + " h" : "");
                        tempNote = note + " + WMI StorageReliabilityCounter（共 "
                            + rel.Count.ToString(CultureInfo.InvariantCulture) + " 条）";
                        tempOk = temp > 0;
                    }
                    else
                    {
                        // WMI 拿不到 → 依次试：温度属性 IOCTL → NVMe 健康日志 IOCTL
                        int idx;
                        if (!int.TryParse(did, out idx)) idx = 0;
                        List<string> why = new List<string>();
                        why.Add("可靠性计数器 " + rel.Count.ToString(CultureInfo.InvariantCulture) + " 条"
                            + (relError.Length > 0 ? "（" + relError + "）" : "，DeviceId 没配上"));
                        int? io = DiskTemperatureIoctl(idx, why);
                        if (!io.HasValue) io = NvmeTemperatureIoctl(idx, why);
                        if (!io.HasValue) io = StorportTemperatureIoctl(why);
                        if (io.HasValue)
                        {
                            tempText = io.Value.ToString(CultureInfo.InvariantCulture) + " ℃";
                            tempNote = note + " + IOCTL（" + string.Join("；", why.ToArray()) + "）";
                            tempOk = true;
                        }
                        else
                        {
                            tempNote = note + "（" + string.Join("；", why.ToArray()) + "）";
                        }
                    }

                    list.Add(new Reading("存储", "磁盘", name + (media.Length > 0 ? "（" + media + "）" : "")
                        + "  " + string.Format(CultureInfo.InvariantCulture, "{0:0} GB", gb), true, note + "（容量与型号）"));
                    list.Add(new Reading("存储", "磁盘温度", tempText, tempOk, tempNote));
                }
            }
            catch (Exception ex) { Log.Ex("传感器：读磁盘失败", ex); }

            // ── 风扇（MIFS）：拆成两行，方便一眼看
            int[] fans = Mifs.GetFans();
            if (fans != null)
            {
                list.Add(new Reading("风扇 / 电池", "风扇1", string.Format(CultureInfo.InvariantCulture, "{0} RPM", fans[0]), true, "MIFS fn=13"));
                list.Add(new Reading("风扇 / 电池", "风扇2", string.Format(CultureInfo.InvariantCulture, "{0} RPM", fans[1]), true, "MIFS fn=13"));
                if (fans[2] > 0)
                    list.Add(new Reading("风扇 / 电池", "风扇3", string.Format(CultureInfo.InvariantCulture, "{0} RPM", fans[2]), true, "MIFS fn=13"));
            }
            else
            {
                list.Add(new Reading("风扇 / 电池", "风扇1", "未实现", false, "MIFS 不可用（需要管理员）"));
                list.Add(new Reading("风扇 / 电池", "风扇2", "未实现", false, "MIFS 不可用（需要管理员）"));
            }

            // ── 电池
            try
            {
                int? charge = null, status = null;
                ManagementObjectSearcher q = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining,BatteryStatus FROM Win32_Battery");
                foreach (ManagementBaseObject o in q.Get())
                {
                    charge = Int(o["EstimatedChargeRemaining"]);
                    status = Int(o["BatteryStatus"]);
                    break;
                }
                int design = 0, full = 0;
                try
                {
                    ManagementObjectSearcher q2 = new ManagementObjectSearcher("root\\wmi", "SELECT DesignedCapacity FROM BatteryStaticData");
                    foreach (ManagementBaseObject o in q2.Get()) { design = Int(o["DesignedCapacity"]); break; }
                    ManagementObjectSearcher q3 = new ManagementObjectSearcher("root\\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
                    foreach (ManagementBaseObject o in q3.Get()) { full = Int(o["FullChargedCapacity"]); break; }
                }
                catch { }
                if (charge.HasValue)
                {
                    string ac = status.HasValue ? (status.Value == 2 ? "外接电源" : "电池供电") : "";
                    string v = charge.Value.ToString(CultureInfo.InvariantCulture) + " %";
                    string note = "WMI Win32_Battery";
                    if (ac.Length > 0) note = ac + " · " + note;
                    if (design > 0 && full > 0)
                    {
                        v += string.Format(CultureInfo.InvariantCulture, "  ·  健康 {0:0.0} %", full * 100.0 / design);
                        note += " · 满充 " + full.ToString(CultureInfo.InvariantCulture) + " mWh / 设计 " + design.ToString(CultureInfo.InvariantCulture) + " mWh";
                    }
                    list.Add(new Reading("风扇 / 电池", "电池", v, true, note));
                }
            }
            catch (Exception ex) { Log.Ex("传感器：读电池失败", ex); }

            return list;
        }

        /// <summary>把读数渲染成等宽文本面板。</summary>
        public static string Render(List<Reading> list)
        {
            StringBuilder sb = new StringBuilder();
            string group = "";
            for (int i = 0; i < list.Count; i++)
            {
                Reading r = list[i];
                if (r.Group != group)
                {
                    group = r.Group;
                    sb.AppendLine("== " + group + " ==");
                }
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-14}: {1}", r.Name, r.Value));
                if (r.Note.Length > 0 && r.Note.Length < 90)
                    sb.AppendLine("                 └ " + r.Note);
            }
            sb.AppendLine();
            sb.AppendLine("数据源：PDH(Energy Meter / Thermal Zone / GPU Engine / Processor) + WMI + MIFS");
            sb.AppendLine("刷新  ：" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            if (_lastError.Length > 0) sb.AppendLine("最后错误：" + _lastError);
            return sb.ToString();
        }

        /// <summary>探测报告：本机到底有哪些传感器数据源可用（只读）。</summary>
        public static string Probe()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("===== OpenMIFS 传感器探测 =====");
            sb.AppendLine("主机：" + Environment.MachineName + "   时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine();

            string[] sets = new string[] { "Energy Meter", "Thermal Zone Information", "GPU Engine", "GPU Adapter Memory", "Processor Information", "Memory" };
            for (int i = 0; i < sets.Length; i++)
            {
                string name = sets[i];
                sb.AppendLine("── 计数器集：" + name);
                try
                {
                    PerformanceCounterCategory cat = new PerformanceCounterCategory(name);
                    string[] inst = cat.GetInstanceNames();
                    PerformanceCounter[] cs = inst.Length > 0 ? cat.GetCounters(inst[0]) : cat.GetCounters();
                    StringBuilder cn = new StringBuilder();
                    for (int j = 0; j < cs.Length; j++)
                    {
                        if (j > 0) cn.Append("、");
                        cn.Append(cs[j].CounterName);
                    }
                    sb.AppendLine("   计数器：" + cn.ToString() + (inst.Length > 0 ? "（下列自实例 " + inst[0] + "）" : ""));
                    if (inst.Length == 0) sb.AppendLine("   实例  ：（无实例）");
                    else if (inst.Length <= 10) sb.AppendLine("   实例  ：" + string.Join("、", inst));
                    else sb.AppendLine("   实例  ：" + string.Join("、", inst, 0, 8) + " …（共 " + inst.Length.ToString(CultureInfo.InvariantCulture) + " 个）");
                }
                catch (Exception ex) { sb.AppendLine("   不可用：" + ex.Message); }
                sb.AppendLine();
            }

            sb.AppendLine("── 逐项探测");
            List<Reading> list = ReadAll();
            string group = "";
            for (int i = 0; i < list.Count; i++)
            {
                Reading r = list[i];
                if (r.Group != group) { group = r.Group; sb.AppendLine("   [" + group + "]"); }
                sb.AppendLine("   " + (r.Ok ? "OK  " : "FAIL") + " " + r.Name + " = " + r.Value + (r.Note.Length > 0 ? "    ← " + r.Note : ""));
            }
            sb.AppendLine();
            sb.AppendLine("── 结论");
            sb.AppendLine("   本机可用的免驱动数据源已列在上方（OK 行）。FAIL 行说明该机型没有对应计数器/接口。");
            sb.AppendLine("   CPU die 温度、主板/VRM/内存温度需要内核驱动，本项目刻意不做，见 docs/SENSORS.md。");
            Log.Info("执行了传感器探测");
            return sb.ToString();
        }

        // ─────────────────────────────── 磁盘温度兜底：IOCTL_STORAGE_QUERY_PROPERTY
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
        // 注意：STORAGE_PROPERTY_ID 不是连续枚举！官方头文件在 StorageDeviceIoCapabilityProperty 处
        // 显式跳到了 48（reserved 值占位），之后才依次是 49/50/51/52。
        // 按连续编号推测会全部偏低 30 → 驱动收到未定义属性 → 一律返回 ERROR_INVALID_FUNCTION(1)。
        // 这正是 OpenMIFS 0.4.0~0.4.2 读不到磁盘温度的原因（CrystalDiskInfo 能读到 35 ℃ 即为反证）。
        private const int StorageAdapterTemperatureProperty = 51;
        private const int StorageDeviceTemperatureProperty = 52;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize,
            IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        /// <summary>读物理盘温度：WMI 可靠性计数器不可用时的兜底通道（同样需要管理员）。
        /// 两种属性都试：StorageDeviceTemperatureProperty(22) → StorageAdapterTemperatureProperty(21)，
        /// 并对单位做兜底判断（有的驱动给开尔文）。失败原因写进 why，供界面直接显示。</summary>
        private static int? DiskTemperatureIoctl(int driveIndex, List<string> why)
        {
            IntPtr h = IntPtr.Zero, inBuf = IntPtr.Zero, outBuf = IntPtr.Zero;
            try
            {
                h = CreateFileW(@"\\.\PhysicalDrive" + driveIndex.ToString(CultureInfo.InvariantCulture),
                    GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == new IntPtr(-1))
                {
                    int err = Marshal.GetLastWin32Error();
                    why.Add("打不开 PhysicalDrive" + driveIndex.ToString(CultureInfo.InvariantCulture) + " err=" + err.ToString(CultureInfo.InvariantCulture));
                    Log.Warn("传感器：打开 PhysicalDrive" + driveIndex.ToString(CultureInfo.InvariantCulture) + " 失败 err=" + err.ToString(CultureInfo.InvariantCulture));
                    return null;
                }

                inBuf = Marshal.AllocHGlobal(16);
                outBuf = Marshal.AllocHGlobal(1024);
                for (int i = 0; i < 16; i++) Marshal.WriteByte(inBuf, i, 0);
                for (int i = 0; i < 1024; i++) Marshal.WriteByte(outBuf, i, 0);

                int[] props = new int[] { StorageDeviceTemperatureProperty, StorageAdapterTemperatureProperty };
                for (int p = 0; p < props.Length; p++)
                {
                    Marshal.WriteInt32(inBuf, 0, props[p]);   // PropertyId
                    Marshal.WriteInt32(inBuf, 4, 0);          // QueryType = PropertyStandardQuery
                    for (int i = 0; i < 1024; i++) Marshal.WriteByte(outBuf, i, 0);
                    uint ret = 0;
                    if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, 16, outBuf, 1024, out ret, IntPtr.Zero))
                    {
                        int err = Marshal.GetLastWin32Error();
                        why.Add("温度属性 " + props[p].ToString(CultureInfo.InvariantCulture) + " err=" + err.ToString(CultureInfo.InvariantCulture));
                        Log.Info("传感器：磁盘温度 IOCTL property=" + props[p].ToString(CultureInfo.InvariantCulture) + " 失败 err=" + err.ToString(CultureInfo.InvariantCulture));
                        continue;
                    }
                    int infoCount = Marshal.ReadInt16(outBuf, 12);       // STORAGE_TEMPERATURE_DATA_DESCRIPTOR.InfoCount
                    if (infoCount <= 0)
                    {
                        why.Add("温度属性 " + props[p].ToString(CultureInfo.InvariantCulture) + " 成功但 InfoCount=0");
                        continue;
                    }
                    short raw = Marshal.ReadInt16(outBuf, 24 + 2);       // Info[0].Temperature
                    int t = raw;
                    if (t > 200) t = (int)Math.Round(t - 273.15);         // 有的驱动给开尔文
                    if (t > 0 && t < 120)
                    {
                        Log.Info("传感器：磁盘温度来自 IOCTL property=" + props[p].ToString(CultureInfo.InvariantCulture)
                            + " → " + t.ToString(CultureInfo.InvariantCulture) + " ℃（原始 " + raw.ToString(CultureInfo.InvariantCulture) + "）");
                        return t;
                    }
                    why.Add("温度属性 " + props[p].ToString(CultureInfo.InvariantCulture) + " 值不合理(" + raw.ToString(CultureInfo.InvariantCulture) + ")");
                }
                return null;
            }
            catch (Exception ex) { why.Add("温度属性异常 " + ex.Message); Log.Ex("传感器：磁盘温度 IOCTL 异常", ex); return null; }
            finally
            {
                if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
                if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
                if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
            }
        }

        // ─────────────── NVMe 专用通道：IOCTL_STORAGE_QUERY_PROPERTY + NVMe 健康日志（SMART/Health, log page 0x02）
        // Windows 上读 NVMe 温度最通用的一条路（磁盘工具普遍用它）：健康日志第 1~2 字节是开尔文温度。
        private const int StorageDeviceProtocolSpecificProperty = 50;
        private const int ProtocolTypeNvme = 3;
        private const int NvmeDataTypeLogPage = 2;
        private const int NvmeLogPageHealthInfo = 0x02;

        // ─────────────── StorPort 适配器通道（真实工具的做法）
        // NVMe 的协议专用查询在 smartctl/CrystalDiskInfo 这类工具里是向"StorPort 适配器设备接口"
        // 发的（GUID_DEVINTERFACE_STORAGEPORT），而不是 \\.\PhysicalDriveN，也不是猜的 \\.\Scsi0:。
        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr h, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, out uint required, IntPtr devInfo);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);

        private static string[] StorportAdapterPaths()
        {
            List<string> list = new List<string>();
            Guid guid = new Guid("2accfe60-c130-11d2-b082-00a0c91efb8b");   // GUID_DEVINTERFACE_STORAGEPORT
            IntPtr h = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10);
            if (h == new IntPtr(-1)) return list.ToArray();
            try
            {
                SP_DEVICE_INTERFACE_DATA did = new SP_DEVICE_INTERFACE_DATA();
                did.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                uint i = 0;
                while (SetupDiEnumDeviceInterfaces(h, IntPtr.Zero, ref guid, i, ref did))
                {
                    IntPtr detail = Marshal.AllocHGlobal(1024);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA
                        uint need = 0;
                        if (SetupDiGetDeviceInterfaceDetailW(h, ref did, detail, 1024, out need, IntPtr.Zero))
                            list.Add(Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4)));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                    i++;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(h); }
            return list.ToArray();
        }

        /// <summary>向 StorPort 适配器发协议专用查询（property 19）读 NVMe 健康日志温度。</summary>
        private static int? StorportTemperatureIoctl(List<string> why)
        {
            string[] paths = StorportAdapterPaths();
            if (paths.Length == 0) { why.Add("StorPort 适配器 0 个"); return null; }
            for (int a = 0; a < paths.Length; a++)
            {
                int? t = ProtocolSpecificTemperature(paths[a], StorageAdapterProtocolSpecificProperty, why, "StorPort 适配器");
                if (t.HasValue) return t;
            }
            return null;
        }

        private const int StorageAdapterProtocolSpecificProperty = 49;

        /// <summary>协议专用查询的公共实现（设备通道与适配器通道共用）。</summary>
        private static int? ProtocolSpecificTemperature(string path, int propertyId, List<string> why, string tag)
        {
            const int QuerySize = 12, ProtoSize = 40, DataSize = 512;
            const int Total = QuerySize + ProtoSize + DataSize;
            IntPtr h = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == new IntPtr(-1))
                {
                    why.Add(tag + " 打不开 err=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                    return null;
                }
                buf = Marshal.AllocHGlobal(Total);
                for (int i = 0; i < Total; i++) Marshal.WriteByte(buf, i, 0);
                Marshal.WriteInt32(buf, 0, propertyId);
                Marshal.WriteInt32(buf, 4, 0);
                int p = QuerySize;
                Marshal.WriteInt32(buf, p + 0, ProtocolTypeNvme);
                Marshal.WriteInt32(buf, p + 4, NvmeDataTypeLogPage);
                Marshal.WriteInt32(buf, p + 8, NvmeLogPageHealthInfo);
                Marshal.WriteInt32(buf, p + 12, 0);
                Marshal.WriteInt32(buf, p + 16, ProtoSize);
                Marshal.WriteInt32(buf, p + 20, DataSize);
                uint ret = 0;
                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, (uint)Total, buf, (uint)Total, out ret, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    why.Add(tag + " 协议专用 " + propertyId.ToString(CultureInfo.InvariantCulture) + " err=" + err.ToString(CultureInfo.InvariantCulture));
                    return null;
                }
                int off = QuerySize + ProtoSize;
                int kelvin = (int)Marshal.ReadByte(buf, off + 1) | ((int)Marshal.ReadByte(buf, off + 2) << 8);
                int t = kelvin > 0 ? (int)Math.Round(kelvin - 273.15) : 0;
                if (t > 0 && t < 120)
                {
                    Log.Info("传感器：磁盘温度来自 " + tag + " NVMe 健康日志 → " + t.ToString(CultureInfo.InvariantCulture)
                        + " ℃（原始 " + kelvin.ToString(CultureInfo.InvariantCulture) + " K）");
                    return t;
                }
                why.Add(tag + " 健康日志温度不合理(" + kelvin.ToString(CultureInfo.InvariantCulture) + " K)");
                return null;
            }
            catch (Exception ex) { why.Add(tag + " 异常 " + ex.Message); return null; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
            }
        }

        private static int? NvmeTemperatureIoctl(int driveIndex, List<string> why)
        {
            const int QuerySize = 12;          // STORAGE_PROPERTY_QUERY
            const int ProtoSize = 40;          // STORAGE_PROTOCOL_SPECIFIC_DATA
            const int DataSize = 512;          // NVMe 健康日志（512 字节足够）
            const int Total = QuerySize + ProtoSize + DataSize;

            IntPtr h = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                h = CreateFileW(@"\\.\PhysicalDrive" + driveIndex.ToString(CultureInfo.InvariantCulture),
                    GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == new IntPtr(-1))
                {
                    why.Add("NVMe: 打不开盘 err=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                    return null;
                }

                buf = Marshal.AllocHGlobal(Total);
                for (int i = 0; i < Total; i++) Marshal.WriteByte(buf, i, 0);

                Marshal.WriteInt32(buf, 0, StorageDeviceProtocolSpecificProperty);  // PropertyId
                Marshal.WriteInt32(buf, 4, 0);                                      // QueryType = PropertyStandardQuery

                int p = QuerySize;
                Marshal.WriteInt32(buf, p + 0, ProtocolTypeNvme);            // ProtocolType
                Marshal.WriteInt32(buf, p + 4, NvmeDataTypeLogPage);         // DataType
                Marshal.WriteInt32(buf, p + 8, NvmeLogPageHealthInfo);       // ProtocolDataRequestValue = 健康日志
                Marshal.WriteInt32(buf, p + 12, 0);                          // SubValue
                Marshal.WriteInt32(buf, p + 16, ProtoSize);                  // ProtocolDataOffset（相对本结构开头）
                Marshal.WriteInt32(buf, p + 20, DataSize);                   // ProtocolDataLength
                Marshal.WriteInt32(buf, p + 24, 0);                          // FixedProtocolReturnData

                uint ret = 0;
                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, (uint)Total, buf, (uint)Total, out ret, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    why.Add("NVMe 健康日志 err=" + err.ToString(CultureInfo.InvariantCulture));
                    Log.Info("传感器：NVMe 健康日志 IOCTL 失败 err=" + err.ToString(CultureInfo.InvariantCulture));
                    return null;
                }

                int dataOff = QuerySize + ProtoSize;
                int kelvin = (int)Marshal.ReadByte(buf, dataOff + 1) | ((int)Marshal.ReadByte(buf, dataOff + 2) << 8);
                int critical = Marshal.ReadByte(buf, dataOff + 0);
                int t = kelvin > 0 ? (int)Math.Round(kelvin - 273.15) : 0;
                if (t > 0 && t < 120)
                {
                    Log.Info("传感器：磁盘温度来自 NVMe 健康日志 → " + t.ToString(CultureInfo.InvariantCulture)
                        + " ℃（原始 " + kelvin.ToString(CultureInfo.InvariantCulture) + " K，critical_warning=" + critical.ToString(CultureInfo.InvariantCulture) + "）");
                    return t;
                }
                why.Add("NVMe 健康日志温度不合理(" + kelvin.ToString(CultureInfo.InvariantCulture) + " K)");
                return null;
            }
            catch (Exception ex) { why.Add("NVMe 异常 " + ex.Message); Log.Ex("传感器：NVMe 健康日志异常", ex); return null; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
            }
        }

        private static string W(float mw) { return string.Format(CultureInfo.InvariantCulture, "{0:0.00} W", mw / 1000.0); }

        private static string MemType(int code)
        {
            switch (code)
            {
                case 20: return "DDR";
                case 21: return "DDR2";
                case 24: return "DDR3";
                case 26: return "DDR4";
                case 27: return "LPDDR";
                case 28: return "LPDDR2";
                case 29: return "LPDDR3";
                case 30: return "LPDDR4";
                case 34: return "DDR5";
                case 35: return "LPDDR5";
            }
            return code > 0 ? "类型" + code.ToString(CultureInfo.InvariantCulture) : "";
        }

        /// <summary>MSFT_PhysicalDisk.MediaType</summary>
        private static string MediaTypeName(int v)
        {
            switch (v)
            {
                case 3: return "HDD";
                case 4: return "SSD";
                case 5: return "SCM";
            }
            return v > 0 ? "介质" + v.ToString(CultureInfo.InvariantCulture) : "";
        }

        /// <summary>MSFT_PhysicalDisk.BusType</summary>
        private static string BusTypeName(int v)
        {
            switch (v)
            {
                case 1: return "SCSI";
                case 2: return "ATAPI";
                case 3: return "ATA";
                case 7: return "USB";
                case 8: return "RAID";
                case 10: return "SAS";
                case 11: return "SATA";
                case 13: return "MMC";
                case 14: return "虚拟";
                case 15: return "文件虚拟";
                case 16: return "存储空间";
                case 17: return "NVMe";
                case 18: return "SCM";
                case 19: return "UFS";
            }
            return v > 0 ? "总线" + v.ToString(CultureInfo.InvariantCulture) : "";
        }

        /// <summary>MSFT_PhysicalDisk.HealthStatus</summary>
        private static string HealthName(int v)
        {
            switch (v)
            {
                case 0: return "健康";
                case 1: return "警告";
                case 2: return "不健康";
            }
            return v > 0 ? "状态" + v.ToString(CultureInfo.InvariantCulture) : "";
        }

        private static string S(object o) { return o == null ? "" : o.ToString(); }

        private static int Int(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static long Long(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static double Num(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }
    }
}
