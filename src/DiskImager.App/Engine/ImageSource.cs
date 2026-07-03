using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace DiskImagerX.Engine;

/// <summary>Opens an image file for restore/verify and reports its detected format.
/// Cross-platform: uses framework GZip/Zip; VHD/ISO/raw handled directly.</summary>
public static class ImageSource
{
    public readonly record struct Opened(Stream Stream, long SizeHint, string Label);

    public static string Detect(string path)
    {
        if (!File.Exists(path)) return "";
        Span<byte> m = stackalloc byte[8];
        int n;
        try { using var f = File.OpenRead(path); n = f.Read(m); }
        catch (Exception ex) { return "Cannot read: " + ex.Message; }

        if (n >= 2 && m[0] == 0x1F && m[1] == 0x8B)
        { long sz = GzipSize(path); return sz > 0 ? $"gzip  |  {Fmt(sz)} uncompressed" : "gzip compressed"; }
        if (n >= 4 && m[0] == 0x50 && m[1] == 0x4B && m[2] == 0x03 && m[3] == 0x04)
            return $"ZIP archive  |  {Fmt(new FileInfo(path).Length)}";
        if (n >= 6 && m[0] == 0xFD && m[1] == 0x37 && m[2] == 0x7A) return "XZ  —  not yet supported on this build";
        if (n >= 3 && m[0] == 0x42 && m[1] == 0x5A && m[2] == 0x68) return "BZip2  —  not yet supported on this build";
        if (n >= 4 && m[0] == 0x28 && m[1] == 0xB5 && m[2] == 0x2F && m[3] == 0xFD) return "Zstandard  —  not yet supported";

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".vhd") { long ds = new FileInfo(path).Length - 512; return $"VHD (fixed)  |  {Fmt(ds > 0 ? ds : 0)}"; }
        if (ext == ".iso") return $"ISO 9660  |  {Fmt(new FileInfo(path).Length)}";
        return $"Raw image  |  {Fmt(new FileInfo(path).Length)}";
    }

    public static Opened Open(string path)
    {
        Span<byte> m = stackalloc byte[8];
        using (var probe = File.OpenRead(path)) probe.Read(m);

        if (m[0] == 0x1F && m[1] == 0x8B)
        {
            var fs = File.OpenRead(path);
            try { return new Opened(new GZipStream(fs, CompressionMode.Decompress), GzipSize(path), "gzip"); }
            catch { fs.Dispose(); throw; }
        }
        if (m[0] == 0x50 && m[1] == 0x4B && m[2] == 0x03 && m[3] == 0x04)
        {
            var za = ZipFile.OpenRead(path);
            var entry = za.Entries.FirstOrDefault(e => e.Length > 0)
                        ?? throw new InvalidDataException("ZIP has no usable entry.");
            // Keep the archive alive for the lifetime of the entry stream.
            return new Opened(new OwningStream(entry.Open(), za), entry.Length, "zip:" + entry.Name);
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".vhd")
        {
            long len = new FileInfo(path).Length;
            long data = len - 512;
            if (data <= 0) throw new InvalidDataException("VHD too small.");
            var fs = File.OpenRead(path);
            return new Opened(new LimitedStream(fs, data), data, "vhd");
        }

        var raw = File.OpenRead(path);
        return new Opened(raw, raw.Length, ext == ".iso" ? "iso" : "raw");
    }

    static long GzipSize(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            if (f.Length < 18) return -1;
            if (f.Length > 0xFFFFFFFFL) return -1;   // ISIZE is 32-bit; unreliable for >4 GB inputs
            f.Seek(-4, SeekOrigin.End);
            Span<byte> b = stackalloc byte[4];
            f.Read(b);
            uint isize = BitConverter.ToUInt32(b);
            return isize > 0 ? isize : -1;
        }
        catch { return -1; }
    }

    static string Fmt(long b)
    {
        if (b >= 1L << 40) return $"{b / (double)(1L << 40):0.00} TB";
        if (b >= 1L << 30) return $"{b / (double)(1L << 30):0.00} GB";
        if (b >= 1L << 20) return $"{b / (double)(1L << 20):0.0} MB";
        return $"{b} B";
    }
}

/// <summary>Caps reads to N bytes (VHD data section).</summary>
public sealed class LimitedStream : Stream
{
    readonly Stream _inner; long _remaining;
    public LimitedStream(Stream inner, long limit) { _inner = inner; _remaining = limit; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _remaining;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buf, int off, int count)
    {
        if (_remaining <= 0) return 0;
        int n = _inner.Read(buf, off, (int)Math.Min(count, _remaining));
        _remaining -= n; return n;
    }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    protected override void Dispose(bool d) { if (d) _inner.Dispose(); base.Dispose(d); }
}

/// <summary>Wraps an entry stream and disposes the owning archive with it.</summary>
public sealed class OwningStream : Stream
{
    readonly Stream _inner; readonly IDisposable _owner;
    public OwningStream(Stream inner, IDisposable owner) { _inner = inner; _owner = owner; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] b, int o, int c) => _inner.Read(b, o, c);
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    protected override void Dispose(bool d) { if (d) { _inner.Dispose(); _owner.Dispose(); } base.Dispose(d); }
}
