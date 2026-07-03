using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
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
        var buf = new byte[BUF];
        long done = 0; var lastReport = TimeSpan.Zero;
        SHA256? sha = sha256 ? SHA256.Create() : null;

        using (var diskStream = backend.OpenRead(disk))
        using (var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            Stream hashSink = sha != null ? new CryptoStream(outFs, sha, CryptoStreamMode.Write, leaveOpen: true) : outFs;
            Stream sink = gzip ? new GZipStream(hashSink, CompressionLevel.Optimal, leaveOpen: true) : hashSink;
            try
            {
                Report(progress, Phase.Backing, 0, total, 0, sw.Elapsed, "Starting backup…");
                while (done < total)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(BUF, total - done);
                    int n = ReadFull(diskStream, buf, want);
                    if (n == 0) break;
                    sink.Write(buf, 0, n);
                    done += n;
                    Throttle(progress, ref lastReport, sw, Phase.Backing, done, total, "Backing up…");
                }
                sink.Flush();
            }
            finally
            {
                if (gzip) sink.Dispose();                 // flush gzip trailer
                if (sha != null) hashSink.Dispose();      // finalize hash
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
        var buf = new byte[BUF];
        long done = 0; var lastReport = TimeSpan.Zero; bool overflowed = false;

        var opened = ImageSource.Open(imagePath);
        long sizeHint = opened.SizeHint;

        using (opened.Stream)
        using (var diskStream = backend.OpenWrite(disk))
        {
            if (sizeHint > 0 && sizeHint > total && !smartRestore)
                throw new IOException($"Image ({Fmt(sizeHint)}) is larger than the target disk ({Fmt(total)}).");

            Report(progress, Phase.Restoring, 0, sizeHint, 0, sw.Elapsed, "Starting restore…");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = ReadFull(opened.Stream, buf, BUF);
                if (n == 0) break;
                if (n % 512 != 0) { int p = (n + 511) / 512 * 512; Array.Clear(buf, n, p - n); n = p; }

                if (total > 0 && done + n > total)
                {
                    int capped = (int)((total - done) / 512 * 512);
                    if (!smartRestore && capped < n)
                        for (int q = capped; q < n; q++) if (buf[q] != 0) { overflowed = true; break; }
                    n = capped;
                    if (n <= 0) break;
                }

                if (smartRestore && IsAllZero(buf, n))
                {
                    diskStream.Seek(n, SeekOrigin.Current);
                    done += n;
                    Throttle(progress, ref lastReport, sw, Phase.Restoring, done, sizeHint, "Restoring…");
                    continue;
                }

                diskStream.Write(buf, 0, n);
                done += n;
                Throttle(progress, ref lastReport, sw, Phase.Restoring, done, sizeHint, "Restoring…");
            }
            diskStream.Flush();

            if (!ct.IsCancellationRequested && !smartRestore && total > 0 && done >= total)
            {
                if (overflowed) throw new IOException($"Image is larger than the target disk — only the first {Fmt(total)} were written. Aborted.");
                int extra = ReadFull(opened.Stream, buf, BUF); long scanned = 0;
                while (extra > 0 && IsAllZero(buf, extra) && scanned < 64L * 1024 * 1024) { scanned += extra; extra = ReadFull(opened.Stream, buf, BUF); }
                if (extra > 0 && !IsAllZero(buf, extra))
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
        var a = new byte[BUF]; var b = new byte[BUF];
        long done = 0; var lastReport = TimeSpan.Zero;

        var opened = ImageSource.Open(imagePath);
        long cap = limit > 0 ? limit : (opened.SizeHint > 0 ? Math.Min(opened.SizeHint, disk.SizeBytes) : disk.SizeBytes);

        using (opened.Stream)
        using (var diskStream = backend.OpenRead(disk))
        {
            Report(progress, Phase.Verifying, 0, cap, 0, sw.Elapsed, "Verifying…");
            while (done < cap)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(BUF, cap - done);
                int na = ReadFull(opened.Stream, a, want);
                if (na == 0) break;
                if (na % 512 != 0) { int p = (na + 511) / 512 * 512; Array.Clear(a, na, p - na); na = p; }

                if (smartRestore && IsAllZero(a, na)) { diskStream.Seek(na, SeekOrigin.Current); done += na; Throttle(progress, ref lastReport, sw, Phase.Verifying, done, cap, "Verifying…"); continue; }

                int nb = ReadFull(diskStream, b, na);
                if (nb < na) throw new IOException($"Disk ended early at {Fmt(done)} during verify.");
                for (int i = 0; i < na; i++)
                    if (a[i] != b[i]) throw new IOException($"Verify FAILED at offset {Fmt(done + i)} (image {a[i]:X2} vs disk {b[i]:X2}).");
                done += na;
                Throttle(progress, ref lastReport, sw, Phase.Verifying, done, cap, "Verifying…");
            }
        }
        if (ct.IsCancellationRequested) { Report(progress, Phase.Cancelled, done, cap, 0, sw.Elapsed, "Cancelled."); return; }
        Report(progress, Phase.Done, done, cap, 0, sw.Elapsed, "Verify complete — image matches disk.");
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    static int ReadFull(Stream s, byte[] buf, int want)
    {
        int n = 0;
        while (n < want) { int r = s.Read(buf, n, want - n); if (r == 0) break; n += r; }
        return n;
    }

    static bool IsAllZero(byte[] b, int n) { for (int i = 0; i < n; i++) if (b[i] != 0) return false; return true; }

    static void Throttle(IProgress<ImagingProgress> p, ref TimeSpan last, Stopwatch sw, Phase ph, long done, long total, string msg)
    {
        var now = sw.Elapsed;
        if ((now - last).TotalMilliseconds < 200) return;
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
