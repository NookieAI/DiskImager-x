using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiskImagerX.Disk;

/// <summary>
/// Linux backend: enumerates via <c>lsblk --json</c>, opens <c>/dev/sdX</c> directly,
/// unmounts children with <c>umount</c>, and mounts images via <c>udisksctl</c> (falling
/// back to <c>mount -o loop</c>). Raw writes require root.
/// NOTE: the /dev I/O paths must be validated on real hardware (cannot be exercised on Windows).
/// </summary>
public sealed class LinuxBackend : IDiskBackend
{
    public string PlatformName => "Linux";
    public string ElevationHint => "Relaunch with sudo/pkexec — raw disk access needs root.";

    public bool IsElevated() => UnixNative.IsRoot();

    public async Task<IReadOnlyList<DiskInfo>> EnumerateAsync(CancellationToken ct = default)
    {
        var list = new List<DiskInfo>();
        var r = await ProcUtil.RunAsync("lsblk",
            "--json -b -o NAME,PATH,MODEL,SIZE,TYPE,RM,HOTPLUG,MOUNTPOINT", 15000, ct).ConfigureAwait(false);
        if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut)) return list;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(r.StdOut); } catch { return list; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("blockdevices", out var devs)) return list;
            foreach (var dev in devs.EnumerateArray())
            {
                if (Str(dev, "type") != "disk") continue;   // skip part/loop/rom/ram
                long size = Long(dev, "size");
                if (size <= 0) continue;

                string path = Str(dev, "path");
                string name = Str(dev, "name");
                if (string.IsNullOrEmpty(path)) path = "/dev/" + name;

                string model = Str(dev, "model");
                if (string.IsNullOrWhiteSpace(model)) model = name;

                bool removable = Bool(dev, "rm") || Bool(dev, "hotplug");

                var vols = new List<string>();
                bool system = MountIsSystem(Str(dev, "mountpoint"));
                if (dev.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
                {
                    foreach (var kid in kids.EnumerateArray())
                    {
                        string mp = Str(kid, "mountpoint");
                        if (MountIsSystem(mp)) system = true;
                        if (!string.IsNullOrEmpty(mp))
                        {
                            string kpath = Str(kid, "path");
                            if (string.IsNullOrEmpty(kpath)) kpath = "/dev/" + Str(kid, "name");
                            if (!string.IsNullOrEmpty(kpath)) vols.Add(kpath);
                        }
                    }
                }

                list.Add(new DiskInfo
                {
                    Id = name,
                    DevicePath = path,
                    Model = model.Trim(),
                    SizeBytes = size,
                    IsRemovable = removable && !system,
                    IsSystem = system,
                    Volumes = vols.ToArray(),
                });
            }
        }
        return list;
    }

    private static bool MountIsSystem(string mp)
        => mp is "/" or "/boot" or "/boot/efi" or "[SWAP]";

    public Stream OpenRead(DiskInfo disk) => Open(disk, write: false);

    public Stream OpenWrite(DiskInfo disk)
    {
        // Release any mounted partitions so the kernel allows a raw write.
        foreach (var v in disk.Volumes)
            try { ProcUtil.Run("umount", v, 15000); } catch { }
        return Open(disk, write: true);
    }

    private static Stream Open(DiskInfo disk, bool write)
    {
        var access = write ? FileAccess.ReadWrite : FileAccess.Read;
        var handle = File.OpenHandle(disk.DevicePath, FileMode.Open, access, FileShare.ReadWrite, FileOptions.None);
        return new FileStream(handle, access);
    }

    public void Rescan(DiskInfo disk)
    {
        try { ProcUtil.Run("blockdev", $"--rereadpt {disk.DevicePath}", 10000); }
        catch { try { ProcUtil.Run("partprobe", disk.DevicePath, 10000); } catch { } }
    }

    public void Eject(DiskInfo disk)
    {
        try { ProcUtil.Run("eject", disk.DevicePath, 15000); } catch { }
    }

    public async Task<string?> MountImageAsync(string imagePath, CancellationToken ct = default)
    {
        // udisksctl doesn't require root and auto-mounts recognised filesystems.
        var setup = await ProcUtil.RunAsync("udisksctl", $"loop-setup -f \"{imagePath}\"", 30000, ct).ConfigureAwait(false);
        if (setup.Ok)
        {
            // "Mapped file ... as /dev/loop0."
            string outp = setup.StdOut;
            int i = outp.IndexOf("/dev/loop", StringComparison.Ordinal);
            string loop = i >= 0 ? outp[i..].TrimEnd('.', '\n', '\r', ' ') : "";
            if (!string.IsNullOrEmpty(loop))
            {
                var mount = await ProcUtil.RunAsync("udisksctl", $"mount -b {loop}", 20000, ct).ConfigureAwait(false);
                if (mount.Ok) return mount.StdOut.Trim();
                return $"Attached {loop} (mount it from your file manager).";
            }
        }
        throw new IOException("Could not attach image (udisksctl unavailable?). Try: sudo mount -o loop <image> /mnt");
    }

    // JSON helpers tolerant of lsblk version differences (string vs number vs bool) ---
    private static string Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    private static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return 0;
    }

    private static bool Bool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString() is "1" or "true",
            JsonValueKind.Number => v.TryGetInt64(out var n) && n != 0,
            _ => false,
        };
    }
}
