<#
  磁盘温度读取探针（只读，需要管理员）
  ==================================================================
  这块盘（YMTC PC300 / stornvme）目前在 OpenMIFS 里读不到温度，多条通道都返回
  err=1（ERROR_INVALID_FUNCTION）。本探针把"还能怀疑的几种原因"一次问完：

    0) 属性 0（StorageDeviceProperty）自检 —— 每块盘都必须支持。
       这一步要是也失败，说明是"IOCTL 管道/环境"问题（比如安全软件拦截），
       而不是"NVMe 温度不被支持"。
    1) 温度属性 22 / 21（NVMe 通常不支持，返回 err=1 属正常）
    2) 协议专用 20（设备 → NVMe 健康日志，只读）
    3) 协议专用 20（同上，但用 GENERIC_READ|GENERIC_WRITE 打开）
    4) 协议专用 20（两步法：先问需要多大缓冲区，再正式读）
    5) 协议专用 19（StorPort 适配器接口，只读 / 读写各试一次）
    6) WMI 可靠性计数器实例数
    7) Get-StorageReliabilityCounter 对照

  判读
    · 第 0 步成功 + 其余全 err=1 → 该盘/驱动不向用户态暴露温度（平台限制），到此为止
    · 第 0 步也失败 → 是环境问题（安全软件/过滤驱动），需要另想办法
    · 任意一条成功 → 把输出发我，照它改进 OpenMIFS

  错误码：1=ERROR_INVALID_FUNCTION  5=ERROR_ACCESS_DENIED
          87=ERROR_INVALID_PARAMETER  122=ERROR_INSUFFICIENT_BUFFER

  用法：管理员 PowerShell
    powershell -ExecutionPolicy Bypass -File tools\disk-temp-probe.ps1
#>
[CmdletBinding()]
param([int]$DriveIndex = 0)

$ErrorActionPreference = 'Continue'
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DiskTempProbe {
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;
    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr h, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, out uint required, IntPtr devInfo);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);

    public static string[] StorportAdapters() {
        var list = new System.Collections.Generic.List<string>();
        Guid guid = new Guid("2accfe60-c130-11d2-b082-00a0c91efb8b");   // GUID_DEVINTERFACE_STORAGEPORT
        IntPtr h = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10);
        if (h == new IntPtr(-1)) return list.ToArray();
        try {
            var did = new SP_DEVICE_INTERFACE_DATA();
            did.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
            uint i = 0;
            while (SetupDiEnumDeviceInterfaces(h, IntPtr.Zero, ref guid, i, ref did)) {
                IntPtr detail = Marshal.AllocHGlobal(1024);
                try {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    uint need = 0;
                    if (SetupDiGetDeviceInterfaceDetailW(h, ref did, detail, 1024, out need, IntPtr.Zero))
                        list.Add(Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4)));
                } finally { Marshal.FreeHGlobal(detail); }
                i++;
            }
        } finally { SetupDiDestroyDeviceInfoList(h); }
        return list.ToArray();
    }

    static IntPtr Open(string path, uint access, out int err) {
        IntPtr h = CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        err = (h == new IntPtr(-1)) ? Marshal.GetLastWin32Error() : 0;
        return h;
    }

    /// <summary>属性 0 自检：拿到型号字符串 = IOCTL 管道正常</summary>
    public static string DeviceProperty(string path) {
        int err = 0;
        IntPtr h = Open(path, GENERIC_READ, out err);
        if (h == new IntPtr(-1)) return "打不开（err=" + err + "）";
        IntPtr inBuf = IntPtr.Zero, outBuf = IntPtr.Zero;
        try {
            inBuf = Marshal.AllocHGlobal(16); outBuf = Marshal.AllocHGlobal(1024);
            for (int i = 0; i < 16; i++) Marshal.WriteByte(inBuf, i, 0);
            for (int i = 0; i < 1024; i++) Marshal.WriteByte(outBuf, i, 0);
            Marshal.WriteInt32(inBuf, 0, 0);   // StorageDeviceProperty
            Marshal.WriteInt32(inBuf, 4, 0);
            uint ret = 0;
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, 16, outBuf, 1024, out ret, IntPtr.Zero))
                return "失败 err=" + Marshal.GetLastWin32Error();
            int vOff = Marshal.ReadInt32(outBuf, 12), pOff = Marshal.ReadInt32(outBuf, 16);
            string vendor = vOff > 0 ? Marshal.PtrToStringAnsi(new IntPtr(outBuf.ToInt64() + vOff)) : "";
            string product = pOff > 0 ? Marshal.PtrToStringAnsi(new IntPtr(outBuf.ToInt64() + pOff)) : "";
            return "成功 型号=" + (vendor + " " + product).Trim() + "（返回 " + ret + " 字节）";
        } finally {
            if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
            if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
            CloseHandle(h);
        }
    }

    /// <summary>温度属性（21/22）</summary>
    public static string Temperature(string path, int propertyId) {
        int err = 0;
        IntPtr h = Open(path, GENERIC_READ, out err);
        if (h == new IntPtr(-1)) return "打不开（err=" + err + "）";
        IntPtr inBuf = IntPtr.Zero, outBuf = IntPtr.Zero;
        try {
            inBuf = Marshal.AllocHGlobal(16); outBuf = Marshal.AllocHGlobal(1024);
            for (int i = 0; i < 16; i++) Marshal.WriteByte(inBuf, i, 0);
            for (int i = 0; i < 1024; i++) Marshal.WriteByte(outBuf, i, 0);
            Marshal.WriteInt32(inBuf, 0, propertyId);
            Marshal.WriteInt32(inBuf, 4, 0);
            uint ret = 0;
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, 16, outBuf, 1024, out ret, IntPtr.Zero))
                return "失败 err=" + Marshal.GetLastWin32Error();
            int infoCount = Marshal.ReadInt16(outBuf, 12);
            short raw = Marshal.ReadInt16(outBuf, 26);
            int t = raw; if (t > 200) t = (int)Math.Round(t - 273.15);
            return "成功 InfoCount=" + infoCount + " 原始=" + raw + " → " + t + " ℃";
        } finally {
            if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
            if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
            CloseHandle(h);
        }
    }

    /// <summary>协议专用查询：propertyId 19（适配器）/ 20（设备）
    /// writeAccess=true 用 GENERIC_READ|GENERIC_WRITE 打开；twoStep=true 先问缓冲区大小再读</summary>
    public static string ProtocolSpecific(string path, int propertyId, bool writeAccess, bool twoStep) {
        const int QuerySize = 12, ProtoSize = 40, DataSize = 512;
        int total = QuerySize + ProtoSize + DataSize;
        int err = 0;
        IntPtr h = Open(path, writeAccess ? (GENERIC_READ | GENERIC_WRITE) : GENERIC_READ, out err);
        if (h == new IntPtr(-1)) return "打不开（err=" + err + "，" + (writeAccess ? "读写" : "只读") + "）";
        IntPtr buf = IntPtr.Zero;
        try {
            buf = Marshal.AllocHGlobal(total);
            for (int i = 0; i < total; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteInt32(buf, 0, propertyId);
            Marshal.WriteInt32(buf, 4, 0);
            int p = QuerySize;
            Marshal.WriteInt32(buf, p + 0, 3);            // ProtocolTypeNvme
            Marshal.WriteInt32(buf, p + 4, 2);            // NVMeDataTypeLogPage
            Marshal.WriteInt32(buf, p + 8, 0x02);         // NVMe Health Info
            Marshal.WriteInt32(buf, p + 12, 0);
            Marshal.WriteInt32(buf, p + 16, ProtoSize);   // ProtocolDataOffset
            Marshal.WriteInt32(buf, p + 20, twoStep ? 0 : DataSize);
            uint ret = 0;

            string step = "";
            if (twoStep) {
                bool ok1 = DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, (uint)total, buf, (uint)total, out ret, IntPtr.Zero);
                int err1 = Marshal.GetLastWin32Error();
                int need = Marshal.ReadInt32(buf, p + 20);
                step = "两步[第1步 " + (ok1 ? "成功" : "err=" + err1) + " 需要=" + need + "] ";
                if (!ok1 && err1 != 122 && err1 != 234) return step + "→ 放弃（err=" + err1 + "）";
                Marshal.WriteInt32(buf, p + 20, DataSize);
                for (int i = QuerySize + ProtoSize; i < total; i++) Marshal.WriteByte(buf, i, 0);
            }

            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, (uint)total, buf, (uint)total, out ret, IntPtr.Zero))
                return step + "失败 err=" + Marshal.GetLastWin32Error();

            int off = QuerySize + ProtoSize;
            int kelvin = (int)Marshal.ReadByte(buf, off + 1) | ((int)Marshal.ReadByte(buf, off + 2) << 8);
            int crit = Marshal.ReadByte(buf, off + 0);
            int t = kelvin > 0 ? (int)Math.Round(kelvin - 273.15) : 0;
            return step + "成功 温度=" + t + " ℃（原始 " + kelvin + " K，critical_warning=" + crit + "，返回 " + ret + " 字节）";
        } finally {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseHandle(h);
        }
    }
}
'@

Write-Host ''
Write-Host '===== 磁盘温度通道探针 =====' -ForegroundColor Cyan
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host ("管理员权限 : {0}" -f $(if ($isAdmin) { '是' } else { '否（很多通道会 err=5）' }))
$disk = "\\.\PhysicalDrive$DriveIndex"
Write-Host ("设备路径   : {0}" -f $disk)
Write-Host ''

function Show($title, $value) { Write-Host ("  {0,-54} {1}" -f $title, $value) }

Show '0) 属性 0 自检（StorageDeviceProperty，必须成功）' ([DiskTempProbe]::DeviceProperty($disk))
Write-Host ''
Show '1) 温度属性 22（StorageDeviceTemperature）'   ([DiskTempProbe]::Temperature($disk, 22))
Show '2) 温度属性 21（StorageAdapterTemperature）'  ([DiskTempProbe]::Temperature($disk, 21))
Show '3) 协议专用 20（设备，只读）'                  ([DiskTempProbe]::ProtocolSpecific($disk, 20, $false, $false))
Show '4) 协议专用 20（设备，读写权限）'              ([DiskTempProbe]::ProtocolSpecific($disk, 20, $true, $false))
Show '5) 协议专用 20（设备，两步法）'                ([DiskTempProbe]::ProtocolSpecific($disk, 20, $false, $true))
Write-Host ''

$adapters = [DiskTempProbe]::StorportAdapters()
Write-Host ("  找到 {0} 个 StorPort 适配器接口" -f $adapters.Count)
$n = 6
foreach ($a in $adapters) {
    $short = $a
    if ($short.Length -gt 30) { $short = $short.Substring(0, 30) + '…' }
    Show ("{0}) StorPort 适配器 19（只读，{1}）" -f $n, $short) ([DiskTempProbe]::ProtocolSpecific($a, 19, $false, $false))
    $n++
    Show ("{0}) StorPort 适配器 19（读写，{1}）" -f $n, $short) ([DiskTempProbe]::ProtocolSpecific($a, 19, $true, $false))
    $n++
}

Write-Host ''
Write-Host '7) WMI：MSFT_StorageReliabilityCounter'
try {
    $rel = @(Get-CimInstance -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_StorageReliabilityCounter -ErrorAction Stop)
    Write-Host ("   实例数 = {0}" -f $rel.Count)
    $rel | Select-Object DeviceId, Temperature, Wear, PowerOnHours | Format-Table -AutoSize
} catch { Write-Host ("   查询失败：{0}" -f $_.Exception.Message) -ForegroundColor Yellow }

Write-Host '8) 对照：Get-StorageReliabilityCounter'
try {
    Get-PhysicalDisk | ForEach-Object {
        $c = $_ | Get-StorageReliabilityCounter -ErrorAction Stop
        Write-Host ("   {0} → 温度={1} 磨损={2} 通电={3} h" -f $_.FriendlyName, $c.Temperature, $c.Wear, $c.PowerOnHours)
    }
} catch { Write-Host ("   失败：{0}" -f $_.Exception.Message) -ForegroundColor Yellow }

Write-Host ''
Write-Host '判读：'
Write-Host '  · 第 0 步成功 + 其余全 err=1 → 该盘/驱动不向用户态暴露温度（平台限制），到此为止'
Write-Host '  · 第 0 步也失败 → 是环境问题（安全软件/过滤驱动拦截），不是 NVMe 本身'
Write-Host '  · 任意一条成功 → 把输出发我，照它改 OpenMIFS'
Write-Host ''
