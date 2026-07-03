using System;

namespace DiskImagerX.Engine;

public enum Phase { Idle, Preparing, Backing, Restoring, Verifying, Formatting, Hashing, Done, Error, Cancelled }

/// <summary>Progress snapshot pushed from the worker to the UI.</summary>
public readonly record struct ImagingProgress(
    Phase Phase,
    long BytesDone,
    long BytesTotal,     // -1 when unknown (compressed source)
    double MBps,
    TimeSpan Elapsed,
    string Message)
{
    public double Percent => BytesTotal > 0 ? Math.Clamp(BytesDone * 100.0 / BytesTotal, 0, 100) : -1;
}
