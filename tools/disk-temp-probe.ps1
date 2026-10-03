<#
  磁盘温度读取探针（只读，需要管理员）
  ==================================================================
  目的：把「磁盘温度读不到」这件事一次问清 —— 是平台不暴露，还是我们的调用方式不对。

  依次尝试 5 条通道，每条打印 Win32 错误码：
    1. IOCTL_STORAGE_QUERY_PROPERTY + StorageDeviceTemperatureProperty(22)     ← 主要给 ATA/SATA
    2. IOCTL_STORAGE_QUERY_PROPERTY + StorageAdapterTemperatureProperty(21)
    3. IOCTL_STORAGE_QUERY_PROPERTY + StorageDeviceProtocolSpecificProperty(20) ← NVMe 健康日志（正路）
    4. 同上但走适配器句柄 \\.\Scsi0: + StorageAdapterProtocolSpecificProperty(19)
    5. WMI：MSFT_StorageReliabilityCounter 实例数与内容

  判读
    · 通道 3 成功 → 是 OpenMIFS 的实现问题，把输出发我即可修
    · 全部 err=1（ERROR_INVALID_FUNCTION）→ 这块盘/驱动组合不向用户态暴露温度，
      到此为止，OpenMIFS 会如实显示「未实现」并写明原因
  常见错误码
    1    ERROR_INVALID_FUNCTION   请求的功能不被支持
    5    ERROR_ACCESS_DENIED      没提权
    87   ERROR_INVALID_PARAMETER  参数/结构不对
    122  ERROR_INSUFFICIENT_BUFFER 缓冲区太小（需要先问大小）

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
    const uint GENERIC_READ = 0x80000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;
    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    // ── 枚举 StorPort 适配器设备接口（GUID_DEVINTERFACE_STORAGEPORT）
    // NVMe 的协议专用查询在真实工具里是对"适配器"设备发的，而适配器路径要靠 SetupAPI 枚举拿到，
    // 而不是猜 \\.\Scsi0:。
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

    // 注意：GUID 必须是方法内的局部变量（静态只读字段不能按 ref 传递）

    /// <summary>返回所有 StorPort 适配器设备路径（如 \\?\scsi#...#{...}）</summary>
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
                uint need = 0;
                int detailSize = IntPtr.Size == 8 ? 8 : 6;   // SP_DEVICE_INTERFACE_DETAIL_DATA 的 cbSize
                IntPtr detail = Marshal.AllocHGlobal(1024);
                try {
                    Marshal.WriteInt32(detail, detailSize);
                    if (SetupDiGetDeviceInterfaceDetailW(h, ref did, detail, 1024, out need, IntPtr.Zero))
                        list.Add(Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4)));
                } finally { Marshal.FreeHGlobal(detail); }
                i++;
            }
        } finally { SetupDiDestroyDeviceInfoList(h); }
        return list.ToArray();
    }

    static IntPtr Open(string path, out int err) {
        IntPtr h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        err = (h == new IntPtr(-1)) ? Marshal.GetLastWin32Error() : 0;
        return h;
    }

    /// <summary>温度属性（property 21/22）：返回 温度 或 -1，err 出参带错误码</summary>
    public static string Temperature(string path, int propertyId, out int err) {
        err = 0;
        IntPtr h = Open(path, out err);
        if (h == new IntPtr(-1)) return "打不开（err=" + err + "）";
        IntPtr inBuf = IntPtr.Zero, outBuf = IntPtr.Zero;
        try {
            inBuf = Marshal.AllocHGlobal(16); outBuf = Marshal.AllocHGlobal(1024);
            for (int i = 0; i < 16; i++) Marshal.WriteByte(inBuf, i, 0);
            for (int i = 0; i < 1024; i++) Marshal.WriteByte(outBuf, i, 0);
            Marshal.WriteInt32(inBuf, 0, propertyId);
            Marshal.WriteInt32(inBuf, 4, 0);
            uint ret = 0;
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, 16, outBuf, 1024, out ret, IntPtr.Zero)) {
                err = Marshal.GetLastWin32Error();
                return "失败 err=" + err;
            }
            int infoCount = Marshal.ReadInt16(outBuf, 12);
            short raw = Marshal.ReadInt16(outBuf, 24 + 2);
            int t = raw; if (t > 200) t = (int)Math.Round(t - 273.15);
            return "成功 InfoCount=" + infoCount + " 原始=" + raw + " → " + t + " ℃（返回 " + ret + " 字节）";
        } finally {
            if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
            if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
            CloseHandle(h);
        }
    }

    /// <summary>协议专用查询：propertyId 19（适配器）/ 20（设备）；err 出参带错误码</summary>
    public static string ProtocolSpecific(string path, int propertyId, out int err) {
        err = 0;
        IntPtr h = Open(path, out err);
        if (h == new IntPtr(-1)) return "打不开（err=" + err + "）";
        const int QuerySize = 12, ProtoSize = 40, DataSize = 512;
        int total = QuerySize + ProtoSize + DataSize;
        IntPtr buf = IntPtr.Zero;
        try {
            buf = Marshal.AllocHGlobal(total);
            for (int i = 0; i < total; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteInt32(buf, 0, propertyId);
            Marshal.WriteInt32(buf, 4, 0);
            int p = QuerySize;
            Marshal.WriteInt32(buf, p + 0, 3);          // ProtocolTypeNvme
            Marshal.WriteInt32(buf, p + 4, 2);          // NVMeDataTypeLogPage
            Marshal.WriteInt32(buf, p + 8, 0x02);       // NVMe Health Info
            Marshal.WriteInt32(buf, p + 12, 0);
            Marshal.WriteInt32(buf, p + 16, ProtoSize); // ProtocolDataOffset
            Marshal.WriteInt32(buf, p + 20, DataSize);  // ProtocolDataLength
            uint ret = 0;
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, (uint)total, buf, (uint)total, out ret, IntPtr.Zero)) {
                err = Marshal.GetLastWin32Error();
                return "失败 err=" + err;
            }
            int off = QuerySize + ProtoSize;
            int kelvin = (int)Marshal.ReadByte(buf, off + 1) | ((int)Marshal.ReadByte(buf, off + 2) << 8);
            int crit = Marshal.ReadByte(buf, off + 0);
            int t = kelvin > 0 ? (int)Math.Round(kelvin - 273.15) : 0;
            return "成功 温度=" + t + " ℃（原始 " + kelvin + " K，critical_warning=" + crit + "，返回 " + ret + " 字节）";
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

function Try-One($title, $scriptBlock) {
    $err = 0
    $r = & $scriptBlock $err
    Write-Host ("  {0,-58} {1}" -f $title, $r)
}

Try-One '1) 温度属性 22（StorageDeviceTemperature）'   { param($e) [DiskTempProbe]::Temperature($disk, 22, [ref]$e) }
Try-One '2) 温度属性 21（StorageAdapterTemperature）'  { param($e) [DiskTempProbe]::Temperature($disk, 21, [ref]$e) }
Try-One '3) 协议专用 20（设备 → NVMe 健康日志）'      { param($e) [DiskTempProbe]::ProtocolSpecific($disk, 20, [ref]$e) }
Try-One '4) 协议专用 19（适配器句柄 \\.\Scsi0:）'      { param($e) [DiskTempProbe]::ProtocolSpecific('\\.\Scsi0:', 19, [ref]$e) }

# 真实工具（smartctl / CrystalDiskInfo 这类）对 NVMe 是向"StorPort 适配器设备接口"发协议专用查询，
# 适配器路径要枚举 GUID_DEVINTERFACE_STORAGEPORT 拿到，不能靠猜。
$adapters = [DiskTempProbe]::StorportAdapters()
$n = 5
Write-Host ''
Write-Host ("  找到 {0} 个 StorPort 适配器接口" -f $adapters.Count)
foreach ($a in $adapters) {
    $short = $a
    if ($short.Length -gt 40) { $short = $short.Substring(0, 40) + '…' }
    Try-One ("{0}) 协议专用 19（StorPort 适配器 {1}）" -f $n, $short) { param($e) [DiskTempProbe]::ProtocolSpecific($a, 19, [ref]$e) }
    $n++
}

Write-Host ''
Write-Host '6) WMI：MSFT_StorageReliabilityCounter'
try {
    $rel = @(Get-CimInstance -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_StorageReliabilityCounter -ErrorAction Stop)
    Write-Host ("   实例数 = {0}" -f $rel.Count)
    $rel | Select-Object DeviceId,Temperature,TemperatureMax,Wear,PowerOnHours,ReadErrorsTotal | Format-Table -AutoSize
} catch {
    Write-Host ("   查询失败：{0}" -f $_.Exception.Message) -ForegroundColor Yellow
}
Write-Host '7) 对照：Get-StorageReliabilityCounter'
try {
    Get-PhysicalDisk | ForEach-Object {
        $c = $_ | Get-StorageReliabilityCounter -ErrorAction Stop
        Write-Host ("   {0} → 温度={1} 磨损={2} 通电={3} h" -f $_.FriendlyName, $c.Temperature, $c.Wear, $c.PowerOnHours)
    }
} catch {
    Write-Host ("   失败：{0}" -f $_.Exception.Message) -ForegroundColor Yellow
}

Write-Host ''
Write-Host '判读：'
Write-Host '  · 通道 3 或 StorPort 适配器通道成功 → OpenMIFS 的实现问题，把输出发我'
Write-Host '  · 全部 err=1（ERROR_INVALID_FUNCTION）→ 该盘/驱动不向用户态暴露温度，到此为止' -ForegroundColor Yellow
Write-Host '  · err=5 → 提权后重跑；err=87 → 参数结构有问题（把输出发我）' -ForegroundColor Yellow
Write-Host ''
