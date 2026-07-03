using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DiskImagerX.Disk;

/// <summary>
/// Per-OS raw-disk operations. Every implementation must treat writes as destructive and
/// refuse to touch a device it could not positively identify. Read paths are always safe.
/// </summary>
public interface IDiskBackend
{
    string PlatformName { get; }

    /// <summary>True when the process can open raw devices (admin on Windows, root elsewhere).</summary>
    bool IsElevated();

    /// <summary>Command/marker the UI shows to tell the user how to gain privileges.</summary>
    string ElevationHint { get; }

    /// <summary>Enumerate candidate disks. Read-only and safe.</summary>
    Task<IReadOnlyList<DiskInfo>> EnumerateAsync(CancellationToken ct = default);

    /// <summary>Open the whole raw device for reading (backup / verify). Caller disposes.</summary>
    Stream OpenRead(DiskInfo disk);

    /// <summary>Unmount every volume on the disk and open the raw device for writing
    /// (restore / format). Caller disposes; volumes are released on dispose where needed.</summary>
    Stream OpenWrite(DiskInfo disk);

    /// <summary>Flush OS caches / re-read the partition table after a write (best-effort).</summary>
    void Rescan(DiskInfo disk);

    /// <summary>Safely eject a removable disk (best-effort).</summary>
    void Eject(DiskInfo disk);

    /// <summary>Mount an .iso/.vhd/.img as a browsable volume; returns a human location or null.</summary>
    Task<string?> MountImageAsync(string imagePath, CancellationToken ct = default);
}
