using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DiskImagerX.Disk;

namespace DiskImagerX.Engine;

/// <summary>Cross-platform backup / restore / verify. All disk access goes through IDiskBackend,
/// so the same engine drives Windows, macOS and Linux. Ported from the proven .NET 4 logic.</summary>
public static class Imaging
{
    const int BUF = 4 * 1024 * 1024;

    // ── BACKUP: disk → image file ─────────────────────────────────────────────
    public static void Backup(IDiskBackend backend, DiskInfo disk, string outPath,
        bool gzip, bool sha256, IProgress<ImagingProgress> progress, CancellationToken ct)
    {
        long total = disk.SizeBytes;
        var sw = Stopwatch.StartNew();
        long done = 0; var lastReport = TimeSpan.Zero;
        SHA256? sha = sha256 ? SHA256.Create() : null;

        using (var diskStream = backend.OpenRead(disk))
        using (var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1))
        {
            // chunks are BUF-sized, so an intermediate FileStream buffer would only add memcpy
            Stream sink = sha != null ? new CryptoStream(outFs, sha, CryptoStreamMode.Write, leaveOpen: true) : outFs;
            try
            {
                Report(progress, Phase.Backing, 0, total, 0, sw.Elapsed, "Starting backup…");
                if (gzip)
                {
                    done = GzipParallel.Compress(diskStream, sink, total, r =>
                    {
                        Throttle(progress, ref lastReport, sw, Phase.Backing, r, total, "Backing up…");
                    }, ct);
                }
                else
                {
                    // Pipelined copy: read chunk N+1 from the disk while chunk N is written out.
                    var cur = new byte[BUF]; var nxt = new byte[BUF];
                    Task<int>? pendingRead = null;
                    try
                    {
                        int n = ReadFull(diskStream, cur, (int)Math.Min(BUF, total));
                        while (n > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            long rem = total - done - n;
                            if (rem > 0)
                            {
                                var dst = nxt; int want = (int)Math.Min(BUF, rem);
                                pendingRead = Task.Run(() => ReadFull(diskStream, dst, want));
                            }
                            sink.Write(cur, 0, n);
                            done += n;
                            Throttle(progress, ref lastReport, sw, Phase.Backing, done, total, "Backing up…");
                            if (pendingRead == null) break;
                            n = pendingRead.GetAwaiter().GetResult(); pendingRead = null;
                            (cur, nxt) = (nxt, cur);
                        }
                    }
                    finally { if (pendingRead != null) { try { pendingRead.GetAwaiter().GetResult(); } catch { } } }
                }
                sink.Flush();
            }
            finally
            {
                if (sha != null) sink.Dispose();      // finalize hash
            }
        }

        if (ct.IsCancellationRequested) { Report(progress, Phase.Cancelled, done, total, 0, sw.Elapsed, "Cancelled."); return; }
        if (sha != null)
        {
            string hex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            File.WriteAllText(outPath + ".sha256", hex + "  " + Path.GetFileName(outPath) + "\n");
        }
        Report(progress, Phase.Done, done, total, 0, sw.Elapsed, "Backup complete.");
    }

    // ── RESTORE: image file → disk ────────────────────────────────────────────
    public static void Restore(IDiskBackend backend, DiskInfo disk, string imagePath,
        bool smartRestore, bool verify, IProgress<ImagingProgress> progress, CancellationToken ct)
    {
        long total = disk.SizeBytes;
        var sw = Stopwatch.StartNew();
        long done = 0; var lastReport = TimeSpan.Zero; bool overflowed = false;

        var opened = ImageSource.Open(imagePath);
        long sizeHint = opened.SizeHint;

        using (opened.Stream)
        using (var diskStream = backend.OpenWrite(disk))
        {
            if (sizeHint > 0 && sizeHint > total && !smartRestore)
                throw new IOException($"Image ({Fmt(sizeHint)}) is larger than the target disk ({Fmt(total)}).");

            Report(progress, Phase.Restoring, 0, sizeHint, 0, sw.Elapsed, "Starting restore…");

            // Pipelined: read/decompress image chunk N+1 while chunk N is written to the disk.
            var cur = new byte[BUF]; var nxt = new byte[BUF];
            Task<int>? pendingRead = null;
            int carry = -1;   // prefetched-but-unwritten length when the disk-size cap stops the loop
            try
            {
                int n = ReadFull(opened.Stream, cur, BUF);
                while (n > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    { var dst = nxt; pendingRead = Task.Run(() => ReadFull(opened.Stream, dst, BUF)); }

                    if (n % 512 != 0) { int p = (n + 511) / 512 * 512; Array.Clear(cur, n, p - n); n = p; }

                    bool stop = false;
                    if (total > 0 && done + n > total)
                    {
                        int capped = (int)((total - done) / 512 * 512);
                        if (!smartRestore && capped < n && cur.AsSpan(capped, n - capped).IndexOfAnyExcept((byte)0) >= 0)
                            overflowed = true;
                        n = capped;
                        if (n <= 0) stop = true;
                    }

                    if (!stop)
                    {
                        if (smartRestore && IsAllZero(cur, n)) diskStream.Seek(n, SeekOrigin.Current);
                        else WriteChecked(diskStream, cur, n, ct);
                        done += n;
                        Throttle(progress, ref lastReport, sw, Phase.Restoring, done, sizeHint, "Restoring…");
                    }

                    int next = pendingRead.GetAwaiter().GetResult(); pendingRead = null;
                    (cur, nxt) = (nxt, cur);
                    if (stop) { carry = next; break; }
                    n = next;
                }
            }
            finally { if (pendingRead != null) { try { pendingRead.GetAwaiter().GetResult(); } catch { } } }
            diskStream.Flush();

            if (!ct.IsCancellationRequested && !smartRestore && total > 0 && done >= total)
            {
                if (overflowed) throw new IOException($"Image is larger than the target disk — only the first {Fmt(total)} were written. Aborted.");
                int extra = carry >= 0 ? carry : ReadFull(opened.Stream, cur, BUF); long scanned = 0;
                while (extra > 0 && IsAllZero(cur, extra) && scanned < 64L * 1024 * 1024) { scanned += extra; extra = ReadFull(opened.Stream, cur, BUF); }
                if (extra > 0 && !IsAllZero(cur, extra))
                    throw new IOException($"Image is larger than the target disk — only the first {Fmt(total)} were written. Aborted.");
            }
        }

        backend.Rescan(disk);
        if (ct.IsCancellationRequested) { Report(progress, Phase.Cancelled, done, sizeHint, 0, sw.Elapsed, "Cancelled."); return; }

        if (verify)
        {
            Verify(backend, disk, imagePath, smartRestore, progress, ct, done);
            return;
        }
        Report(progress, Phase.Done, done, sizeHint, 0, sw.Elapsed, "Restore complete.");
    }

    // ── VERIFY: disk vs image, byte-for-byte ─────────────────────────────────
    public static void Verify(IDiskBackend backend, DiskInfo disk, string imagePath,
        bool smartRestore, IProgress<ImagingProgress> progress, CancellationToken ct, long limit = -1)
    {
        var sw = Stopwatch.StartNew();
        var cur = new byte[BUF]; var nxt = new byte[BUF]; var b = new byte[BUF];
        long done = 0; var lastReport = TimeSpan.Zero;

        var opened = ImageSource.Open(imagePath);
        long cap = limit > 0 ? limit : (opened.SizeHint > 0 ? Math.Min(opened.SizeHint, disk.SizeBytes) : disk.SizeBytes);

        using (opened.Stream)
        using (var diskStream = backend.OpenRead(disk))
        {
            Report(progress, Phase.Verifying, 0, cap, 0, sw.Elapsed, "Verifying…");

            // Pipelined: read/decompress image chunk N+1 while chunk N is read from disk + compared.
            Task<int>? pendingRead = null;
            try
            {
                int na = ReadFull(opened.Stream, cur, (int)Math.Min(BUF, cap));
                while (na > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    long rem = cap - done - na;
                    if (rem > 0)
                    {
                        var dst = nxt; int want = (int)Math.Min(BUF, rem);
                        pendingRead = Task.Run(() => ReadFull(opened.Stream, dst, want));
                    }

                    if (na % 512 != 0) { int p = (na + 511) / 512 * 512; Array.Clear(cur, na, p - na); na = p; }

                    if (smartRestore && IsAllZero(cur, na)) diskStream.Seek(na, SeekOrigin.Current);
                    else
                    {
                        int nb = ReadFull(diskStream, b, na, ct);
                        if (nb < na) throw new IOException($"Disk ended early at {Fmt(done)} during verify.");
                        if (!cur.AsSpan(0, na).SequenceEqual(b.AsSpan(0, na)))   // vectorized fast path; locate the offset only on mismatch
                        {
                            int i = 0; while (i < na && cur[i] == b[i]) i++;
                            throw new IOException($"Verify FAILED at offset {Fmt(done + i)} (image {cur[i]:X2} vs disk {b[i]:X2}).");
                        }
                    }
                    done += na;
                    Throttle(progress, ref lastReport, sw, Phase.Verifying, done, cap, "Verifying…");

                    if (pendingRead == null) break;
                    na = pendingRead.GetAwaiter().GetResult(); pendingRead = null;
                    (cur, nxt) = (nxt, cur);
                }
            }
            finally { if (pendingRead != null) { try { pendingRead.GetAwaiter().GetResult(); } catch { } } }
        }
        if (ct.IsCancellationRequested) { Report(progress, Phase.Cancelled, done, cap, 0, sw.Elapsed, "Cancelled."); return; }
        Report(progress, Phase.Done, done, cap, 0, sw.Elapsed, "Verify complete — image matches disk.");
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    internal static int ReadFull(Stream s, byte[] buf, int want, CancellationToken ct = default)
    {
        int n = 0;
        while (n < want)
        {
            ct.ThrowIfCancellationRequested();
            int r = s.Read(buf, n, want - n); if (r == 0) break; n += r;
        }
        return n;
    }

    // Write in 512 KiB slices so a cancel takes effect quickly even on slow USB media.
    static void WriteChecked(Stream s, byte[] buf, int n, CancellationToken ct)
    {
        const int SLICE = 512 * 1024;
        for (int off = 0; off < n; off += SLICE)
        {
            ct.ThrowIfCancellationRequested();
            s.Write(buf, off, Math.Min(SLICE, n - off));
        }
    }

    static bool IsAllZero(byte[] b, int n) => b.AsSpan(0, n).IndexOfAnyExcept((byte)0) < 0;   // vectorized: ~16x a byte loop

    static void Throttle(IProgress<ImagingProgress> p, ref TimeSpan last, Stopwatch sw, Phase ph, long done, long total, string msg)
    {
        var now = sw.Elapsed;
        if ((now - last).TotalMilliseconds < 100) return;
        double mbps = now.TotalSeconds > 0.01 ? done / (1024.0 * 1024.0) / now.TotalSeconds : 0;
        last = now;
        p.Report(new ImagingProgress(ph, done, total, mbps, now, msg));
    }

    static void Report(IProgress<ImagingProgress> p, Phase ph, long done, long total, double mbps, TimeSpan el, string msg)
        => p.Report(new ImagingProgress(ph, done, total, mbps, el, msg));

    static string Fmt(long b)
    {
        if (b >= 1L << 40) return $"{b / (double)(1L << 40):0.00} TB";
        if (b >= 1L << 30) return $"{b / (double)(1L << 30):0.00} GB";
        if (b >= 1L << 20) return $"{b / (double)(1L << 20):0.0} MB";
        return $"{b} B";
    }
}
