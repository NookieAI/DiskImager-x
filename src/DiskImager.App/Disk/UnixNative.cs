using System.Runtime.InteropServices;

namespace DiskImagerX.Disk;

internal static class UnixNative
{
    [DllImport("libc")]
    private static extern uint geteuid();

    /// <summary>True when running as root (effective uid 0).</summary>
    public static bool IsRoot() => geteuid() == 0;
}
