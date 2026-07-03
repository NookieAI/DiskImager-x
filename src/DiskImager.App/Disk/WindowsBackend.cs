using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DiskImagerX.Disk;

/// <summary>
/// Windows backend: WMI enumeration + raw <c>\\.\PhysicalDriveN</c> access via CreateFile,
/// with FSCTL volume lock/dismount before writes. Ported from the proven .NET 4 engine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBackend : IDiskBackend
{
    public string PlatformName => "Windows";
    public string ElevationHint => "Right-click → Run as administrator.";

    public bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public Task<IReadOnlyList<DiskInfo>> EnumerateAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<DiskInfo>>(() =>
        {
            var list = new List<DiskInfo>();
            var systemIdx = GetSystemDiskIndices();
            try
            {
                using var q = new ManagementObjectSearcher(
                    "SELECT Index,Model,Size,InterfaceType,MediaType FROM Win32_DiskDrive");
                foreach (ManagementObject d in q.Get())
                using (d)
                {
                    int idx = d["Index"] != null ? Convert.ToInt32(d["Index"]) : 0;
                    long size = d["Size"] != null ? Convert.ToInt64(d["Size"]) : 0;
                    if (size <= 0) continue;
                    string model = d["Model"]?.ToString()?.Trim() ?? "Unknown";
                    string iface = d["InterfaceType"]?.ToString()?.Trim() ?? "";
                    string mtype = d["MediaType"]?.ToString()?.Trim() ?? "";
                    bool removable = iface.Equals("USB", StringComparison.OrdinalIgnoreCase)
                                     || mtype.IndexOf("Removable", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool system = systemIdx.Contains(idx);
                    list.Add(new DiskInfo
                    {
                        Id = "PhysicalDrive" + idx,
                        DevicePath = $@"\\.\PhysicalDrive{idx}",
                        Model = model,
                        SizeBytes = size,
                        IsRemovable = removable && !system,
                        IsSystem = system,
                        Volumes = Array.Empty<string>(),   // resolved on-demand in OpenWrite
                    });
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("[WMI] " + ex.GetType().Name + ": " + ex.Message); }
            list.Sort((a, b) => ParseIndex(a.Id).CompareTo(ParseIndex(b.Id)));
            return list;
        }, ct);

    private static HashSet<int> GetSystemDiskIndices()
    {
        var sys = new HashSet<int>();
        var letters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { var r = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)); if (!string.IsNullOrEmpty(r)) letters.Add(r[..1]); } catch { }
        try { var b = Environment.GetEnvironmentVariable("SystemDrive"); if (!string.IsNullOrEmpty(b)) letters.Add(b[..1]); } catch { }
        if (letters.Count == 0) letters.Add("C");
        foreach (var letter in letters)
        {
            try
            {
                using var s1 = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID=\"{letter}:\"}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                foreach (ManagementObject part in s1.Get())
                using (part)
                {
                    using var s2 = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{{part.Path.RelativePath}}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                    foreach (ManagementObject dd in s2.Get())
                    using (dd)
                        if (dd["Index"] != null) sys.Add(Convert.ToInt32(dd["Index"]));
                }
            }
            catch { }
        }
        return sys;
    }

    public Stream OpenRead(DiskInfo disk)
        => new FileStream(OpenDevice(disk.DevicePath, GENERIC_READ, "reading"), FileAccess.Read);

    public Stream OpenWrite(DiskInfo disk)
    {
        var volHandles = LockVolumes(disk);
        SafeFileHandle h;
        try { h = OpenDevice(disk.DevicePath, GENERIC_READ | GENERIC_WRITE, "writing"); }
        catch { foreach (var v in volHandles) v.Dispose(); throw; }
        return new DiskWriteStream(new FileStream(h, FileAccess.ReadWrite), volHandles);
    }

    // Open the raw device, retrying transient "device re-enumerating" errors that occur right
    // after a filesystem/partition change (format, restore) while Windows re-mounts volumes.
    static SafeFileHandle OpenDevice(string path, uint access, string verb)
    {
        int err = 0;
        for (int i = 0; i < 10; i++)
        {
            var h = CreateFile(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            err = Marshal.GetLastWin32Error();
            h.Dispose();
            // 2 FILE_NOT_FOUND · 3 PATH_NOT_FOUND · 21 NOT_READY · 55 DEV_NOT_EXIST · 1167 DEVICE_NOT_CONNECTED
            if (err is not (2 or 3 or 21 or 55 or 1167)) break;   // non-transient (e.g. 5 ACCESS_DENIED): fail now
            Thread.Sleep(250 + i * 250);
        }
        throw new IOException($"Cannot open {path} for {verb} (error {err}).");
    }

    /// <summary>Lock + dismount every volume on this physical disk so raw writes are allowed.</summary>
    private static List<SafeFileHandle> LockVolumes(DiskInfo disk)
    {
        var handles = new List<SafeFileHandle>();
        int diskIndex = ParseIndex(disk.Id);
        for (char c = 'A'; c <= 'Z'; c++)
        {
            var hv = CreateFile($@"\\.\{c}:", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hv.IsInvalid) { hv.Dispose(); continue; }

            bool onOurDisk = false;
            IntPtr ext = Marshal.AllocHGlobal(512);
            try
            {
                if (DeviceIoControl(hv, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, ext, 512, out _, IntPtr.Zero))
                {
                    int count = Marshal.ReadInt32(ext, 0);
                    if (count > 0 && 8 + count * 24 <= 512)
                        for (int i = 0; i < count; i++)
                            if (Marshal.ReadInt32(ext, 8 + i * 24) == diskIndex) { onOurDisk = true; break; }
                }
            }
            finally { Marshal.FreeHGlobal(ext); }

            if (!onOurDisk) { hv.Dispose(); continue; }
            DeviceIoControl(hv, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            DeviceIoControl(hv, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            handles.Add(hv);
        }
        return handles;
    }

    public void Rescan(DiskInfo disk)
    {
        try
        {
            using var h = CreateFile(disk.DevicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!h.IsInvalid) DeviceIoControl(h, IOCTL_DISK_UPDATE_PROPERTIES, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch { }
    }

    public void Eject(DiskInfo disk)
    {
        try
        {
            using var h = CreateFile(disk.DevicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!h.IsInvalid) DeviceIoControl(h, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch { }
    }

    public async Task<string?> MountImageAsync(string imagePath, CancellationToken ct = default)
    {
        string safe = imagePath.Replace("'", "''");
        string ps = "try { $img = Mount-DiskImage -ImagePath '" + safe + "' -PassThru -ErrorAction Stop; " +
                    "$v = $img | Get-Volume; if ($v -and $v.DriveLetter) { Write-Output $v.DriveLetter } else { Write-Output 'OK' } } " +
                    "catch { Write-Error $_.Exception.Message; exit 1 }";
        var r = await ProcUtil.RunAsync("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{ps.Replace("\"", "\\\"")}\"", 60000, ct).ConfigureAwait(false);
        if (!r.Ok) throw new IOException(r.StdErr.Trim());
        string letter = r.StdOut.Trim();
        return letter.Length == 1 ? letter + ":\\" : "mounted";
    }

    private static int ParseIndex(string id)
    {
        int i = id.Length - 1;
        while (i >= 0 && char.IsDigit(id[i])) i--;
        return int.TryParse(id[(i + 1)..], out var n) ? n : -1;
    }

    /// <summary>FileStream over the disk that also releases the locked volume handles on dispose.</summary>
    private sealed class DiskWriteStream : Stream
    {
        private readonly FileStream _inner;
        private readonly List<SafeFileHandle> _vols;
        public DiskWriteStream(FileStream inner, List<SafeFileHandle> vols) { _inner = inner; _vols = vols; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] b, int o, int c) => _inner.Read(b, o, c);
        public override long Seek(long o, SeekOrigin r) => _inner.Seek(o, r);
        public override void SetLength(long v) => _inner.SetLength(v);
        public override void Write(byte[] b, int o, int c) => _inner.Write(b, o, c);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); foreach (var v in _vols) v.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;
    const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
    const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    const uint FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_DISMOUNT_VOLUME = 0x00090020;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec,
        uint disp, uint flags, IntPtr templ);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSz,
        IntPtr outBuf, uint outSz, out uint returned, IntPtr overlapped);
}
