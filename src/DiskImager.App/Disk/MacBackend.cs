using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DiskImagerX.Disk;

/// <summary>
/// macOS backend: enumerates via <c>diskutil ... -plist</c>, opens the raw character
/// device <c>/dev/rdiskN</c> for speed, unmounts with <c>diskutil unmountDisk</c>, and
/// mounts images with <c>hdiutil</c>. Requires root for raw writes.
/// NOTE: the /dev I/O paths must be validated on real hardware (cannot be exercised on Windows).
/// </summary>
public sealed class MacBackend : IDiskBackend
{
    public string PlatformName => "macOS";
    public string ElevationHint => "Relaunch with sudo — raw disk access needs root.";

    public bool IsElevated() => UnixNative.IsRoot();

    public async Task<IReadOnlyList<DiskInfo>> EnumerateAsync(CancellationToken ct = default)
    {
        var list = new List<DiskInfo>();
        var sysTask = ResolveSystemDisksAsync(ct);   // independent of the listing — overlap it
        var listing = await ProcUtil.RunAsync("diskutil", "list -plist physical", 15000, ct).ConfigureAwait(false);
        if (!listing.Ok) return list;
        var root = PlistParser.Parse(listing.StdOut);

        // WholeDisks: array of "disk0", "disk1", ...
        var whole = PlistParser.GetArray(root, "WholeDisks");
        // AllDisksAndPartitions: per-disk dict with DeviceIdentifier + Partitions/APFSVolumes
        var all = PlistParser.GetArray(root, "AllDisksAndPartitions");

        // Fan out one `diskutil info` per disk (each spawn costs 100-400 ms; serial = seconds).
        var ids = new List<string>();
        var infoTasks = new List<Task<ProcUtil.Result>>();
        foreach (var w in whole)
        {
            if (w is not string wid || string.IsNullOrEmpty(wid)) continue;
            ids.Add(wid);
            infoTasks.Add(ProcUtil.RunAsync("diskutil", $"info -plist /dev/{wid}", 10000, ct));
        }

        var systemDisks = await sysTask.ConfigureAwait(false);

        for (int i = 0; i < ids.Count; i++)
        {
            string id = ids[i];
            var info = await infoTasks[i].ConfigureAwait(false);
            if (!info.Ok) continue;
            var d = PlistParser.Parse(info.StdOut);

            long size = PlistParser.GetLong(d, "Size");
            if (size == 0) size = PlistParser.GetLong(d, "TotalSize");
            if (size <= 0) continue;

            string model = PlistParser.GetString(d, "MediaName");
            if (string.IsNullOrWhiteSpace(model)) model = PlistParser.GetString(d, "IORegistryEntryName");
            if (string.IsNullOrWhiteSpace(model)) model = id;

            bool removable = PlistParser.GetBool(d, "RemovableMedia")
                             || PlistParser.GetBool(d, "Ejectable")
                             || !PlistParser.GetBool(d, "Internal");
            bool system = systemDisks.Contains(id);

            list.Add(new DiskInfo
            {
                Id = id,
                DevicePath = $"/dev/{id}",
                Model = model.Trim(),
                SizeBytes = size,
                IsRemovable = removable && !system,
                IsSystem = system,
                Volumes = VolumesFor(all, id),
            });
        }
        return list;
    }

    private static string[] VolumesFor(List<object?> all, string diskId)
    {
        foreach (var e in all)
        {
            if (PlistParser.GetString(e, "DeviceIdentifier") != diskId) continue;
            var vols = new List<string>();
            foreach (var p in PlistParser.GetArray(e, "Partitions"))
            {
                var dev = PlistParser.GetString(p, "DeviceIdentifier");
                if (!string.IsNullOrEmpty(dev)) vols.Add("/dev/" + dev);
            }
            foreach (var p in PlistParser.GetArray(e, "APFSVolumes"))
            {
                var dev = PlistParser.GetString(p, "DeviceIdentifier");
                if (!string.IsNullOrEmpty(dev)) vols.Add("/dev/" + dev);
            }
            return vols.ToArray();
        }
        return Array.Empty<string>();
    }

    /// <summary>Whole-disk ids that carry the running OS (boot volume + its APFS physical stores).</summary>
    private static async Task<HashSet<string>> ResolveSystemDisksAsync(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var info = await ProcUtil.RunAsync("diskutil", "info -plist /", 10000, ct).ConfigureAwait(false);
            if (info.Ok)
            {
                var d = PlistParser.Parse(info.StdOut);
                var parent = PlistParser.GetString(d, "ParentWholeDisk");
                if (!string.IsNullOrEmpty(parent)) set.Add(parent);

                // If "/" is APFS, resolve the container's physical store(s) to their whole disks.
                var container = PlistParser.GetString(d, "APFSContainerReference");
                if (!string.IsNullOrEmpty(container))
                {
                    var ci = await ProcUtil.RunAsync("diskutil", $"info -plist {container}", 10000, ct).ConfigureAwait(false);
                    if (ci.Ok)
                    {
                        var cd = PlistParser.Parse(ci.StdOut);
                        foreach (var ps in PlistParser.GetArray(cd, "APFSPhysicalStores"))
                        {
                            var store = PlistParser.GetString(ps, "APFSPhysicalStore"); // e.g. disk0s2
                            var whole = WholeOf(store);
                            if (!string.IsNullOrEmpty(whole)) set.Add(whole);
                        }
                    }
                }
            }
        }
        catch { /* fall back to caller's Internal heuristic */ }
        return set;
    }

    private static string WholeOf(string dev)
    {
        // "disk0s2" -> "disk0"
        if (string.IsNullOrEmpty(dev)) return "";
        int s = dev.IndexOf('s', StringComparison.Ordinal);
        return s > 0 ? dev[..s] : dev;
    }

    public Stream OpenRead(DiskInfo disk) => OpenRaw(disk, write: false);

    public Stream OpenWrite(DiskInfo disk)
    {
        // Raw writes require the whole disk unmounted.
        var r = ProcUtil.Run("diskutil", $"unmountDisk force {disk.DevicePath}", 30000);
        if (!r.Ok) throw new IOException($"Could not unmount {disk.DevicePath}: {r.StdErr.Trim()}");
        return OpenRaw(disk, write: true);
    }

    private static Stream OpenRaw(DiskInfo disk, bool write)
    {
        // rdiskN is the raw (unbuffered) char device — far faster for bulk transfer.
        string raw = $"/dev/r{disk.Id}";
        var access = write ? FileAccess.ReadWrite : FileAccess.Read;
        var handle = File.OpenHandle(raw, FileMode.Open, access, FileShare.ReadWrite, FileOptions.None);
        return new FileStream(handle, access, 1);
    }

    public void Rescan(DiskInfo disk) { /* macOS re-reads on remount; nothing to do */ }

    public void Eject(DiskInfo disk)
    {
        try { ProcUtil.Run("diskutil", $"eject {disk.DevicePath}", 15000); } catch { }
    }

    public async Task<string?> MountImageAsync(string imagePath, CancellationToken ct = default)
    {
        var r = await ProcUtil.RunAsync("hdiutil", $"attach \"{imagePath}\"", 60000, ct).ConfigureAwait(false);
        if (!r.Ok) throw new IOException($"hdiutil attach failed: {r.StdErr.Trim()}");
        // Last column of the last non-empty line is usually the mount point.
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = line.IndexOf("/Volumes/", StringComparison.Ordinal);
            if (idx >= 0) return line[idx..].Trim();
        }
        return "mounted";
    }
}
