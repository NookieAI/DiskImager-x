using System;

namespace DiskImagerX.Disk;

/// <summary>A physical disk/device the user can image. OS-neutral view.</summary>
public sealed class DiskInfo
{
    /// <summary>OS-specific device identifier used to open the raw device.
    /// Windows: <c>\\.\PhysicalDrive2</c> · macOS: <c>/dev/rdisk3</c> · Linux: <c>/dev/sdb</c>.</summary>
    public string DevicePath { get; init; } = "";

    /// <summary>Short human id, e.g. "disk3" / "PhysicalDrive2" / "sdb".</summary>
    public string Id { get; init; } = "";

    public string Model { get; init; } = "Unknown";
    public long SizeBytes { get; init; }
    public bool IsRemovable { get; init; }

    /// <summary>Hosts the running OS / boot volume — extra-guarded before any erase.</summary>
    public bool IsSystem { get; init; }

    /// <summary>Mounted volume nodes on this disk (used to unmount before a raw write).
    /// Windows: volume GUID paths · macOS: /dev/disk3s1 · Linux: /dev/sdb1.</summary>
    public string[] Volumes { get; init; } = Array.Empty<string>();

    public string SizeText
    {
        get
        {
            double b = SizeBytes;
            if (SizeBytes >= 1L << 40) return $"{b / (1L << 40):0.0} TB";
            if (SizeBytes >= 1L << 30) return $"{b / (1L << 30):0.0} GB";
            if (SizeBytes >= 1L << 20) return $"{b / (1L << 20):0} MB";
            return $"{SizeBytes} B";
        }
    }

    public string Tag => IsSystem ? "  [SYSTEM]" : IsRemovable ? "  [USB/SD]" : "";

    public override string ToString() => $"{Id}   —   {SizeText}   —   {Model}{Tag}";
}
