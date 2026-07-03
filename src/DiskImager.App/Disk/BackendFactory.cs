using System;
using System.Runtime.InteropServices;

namespace DiskImagerX.Disk;

public static class BackendFactory
{
    public static IDiskBackend Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new WindowsBackend();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return new MacBackend();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))   return new LinuxBackend();
        throw new PlatformNotSupportedException("Unsupported OS.");
    }
}
