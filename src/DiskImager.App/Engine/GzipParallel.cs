using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace DiskImagerX.Engine;

/// <summary>Parallel (pigz-style) gzip writer. Each 4 MiB chunk becomes an independent gzip
/// member, compressed concurrently on the thread pool and written strictly in order — the
/// result is a standard multi-member gzip file that GZipStream, gunzip and 7-Zip all read.
/// The first member's FEXTRA field carries the exact total uncompressed size ('DI' subfield),
/// because the trailing ISIZE only describes the final member.</summary>
public static class GzipParallel
{
    internal const int CHUNK = 4 * 1024 * 1024;

    /// <summary>Compress <paramref name="src"/> into <paramref name="sink"/>; returns bytes read.
    /// <paramref name="total"/> is the expected uncompressed size (0 = unknown) — it bounds the
    /// reads and is stamped into the first member's header for exact restore progress.</summary>
    public static long Compress(Stream src, Stream sink, long total, Action<long> onRead, CancellationToken ct)
    {
        int workers = Math.Clamp(Environment.ProcessorCount, 2, 12);
        var pending = new Queue<Task<byte[]>>(workers);
        long read = 0;
        bool first = true;

        void Emit(byte[] member)
        {
            if (first) { WriteFirstMember(sink, member, total); first = false; }
            else sink.Write(member, 0, member.Length);
        }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int want = total > 0 ? (int)Math.Min(CHUNK, total - read) : CHUNK;
                if (want <= 0) break;
                var raw = ArrayPool<byte>.Shared.Rent(CHUNK);
                int n = Imaging.ReadFull(src, raw, want);
                if (n == 0) { ArrayPool<byte>.Shared.Return(raw); break; }
                read += n;
                pending.Enqueue(Task.Run(() => CompressMember(raw, n)));

                // Backpressure: keep at most `workers` compressions in flight; drain completed heads.
                while (pending.Count >= workers || (pending.Count > 0 && pending.Peek().IsCompleted))
                    Emit(pending.Dequeue().GetAwaiter().GetResult());
                onRead(read);
            }
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                Emit(pending.Dequeue().GetAwaiter().GetResult());
            }
        }
        finally
        {
            // Observe abandoned tasks (cancellation path) so their pooled buffers are returned.
            while (pending.Count > 0) { try { pending.Dequeue().GetAwaiter().GetResult(); } catch { } }
        }
        return read;
    }

    static byte[] CompressMember(byte[] raw, int len)
    {
        try
        {
            using var ms = new MemoryStream(len / 2 + 64);
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                gz.Write(raw, 0, len);
            return ms.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(raw); }
    }

    // .NET's GZipStream always emits a fixed 10-byte header (no optional fields), so we can
    // replace member 0's header with one carrying FEXTRA and keep its deflate body + trailer.
    static void WriteFirstMember(Stream sink, byte[] member, long total)
    {
        Span<byte> h = stackalloc byte[24];
        h.Clear();
        h[0] = 0x1F; h[1] = 0x8B; h[2] = 8; h[3] = 4;   // magic · deflate · FLG = FEXTRA
        h[9] = 0xFF;                                     // OS = unknown
        h[10] = 12; h[11] = 0;                           // XLEN = subfield hdr (4) + data (8)
        h[12] = (byte)'D'; h[13] = (byte)'I';            // subfield id
        h[14] = 8; h[15] = 0;                            // subfield data length
        BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(16, 8), (ulong)Math.Max(total, 0));
        sink.Write(h);
        sink.Write(member, 10, member.Length - 10);
    }

    /// <summary>Read the 'DI' FEXTRA total-size subfield from the stream's current position
    /// (must be the start of a gzip file); returns -1 if absent. Leaves position undefined.</summary>
    public static long ReadSizeHeader(Stream f)
    {
        Span<byte> b = stackalloc byte[10];
        if (f.Read(b) < 10 || b[0] != 0x1F || b[1] != 0x8B || (b[3] & 4) == 0) return -1;
        if (f.Read(b.Slice(0, 2)) < 2) return -1;
        int xlen = b[0] | (b[1] << 8);
        long end = f.Position + xlen;
        while (f.Position + 4 <= end)
        {
            if (f.Read(b.Slice(0, 4)) < 4) return -1;
            int slen = b[2] | (b[3] << 8);
            if (b[0] == (byte)'D' && b[1] == (byte)'I' && slen == 8)
            {
                Span<byte> v = stackalloc byte[8];
                if (f.Read(v) < 8) return -1;
                long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(v);
                return size > 0 ? size : -1;
            }
            f.Seek(slen, SeekOrigin.Current);
        }
        return -1;
    }
}
