using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using DiskImagerX.Disk;

namespace DiskImagerX.Engine;

/// <summary>Cross-platform FAT32 formatter. Pure structure builders (ported + unit-tested from
/// the proven .NET 4 engine) plus a sequential writer that streams MBR/VBR/FSInfo/FAT/root/data
/// straight through the backend's raw-disk stream — no per-OS filesystem tools.</summary>
public static class Fat32
{
    const long PART_START = 2048;   // 1 MiB aligned
    const uint RESV = 32;           // reserved sectors

    public static void Format(IDiskBackend backend, DiskInfo disk, string label, bool quick,
        IProgress<ImagingProgress> progress, CancellationToken ct)
    {
        long totalSectors = disk.SizeBytes / 512;
        long partSectors = totalSectors - PART_START;
        if (partSectors < 65536) throw new IOException("Drive too small for FAT32 (minimum ~32 MB).");

        byte spc = ChooseSpc(partSectors);
        uint fatSz = CalcFatSz32(partSectors, RESV, spc);
        uint dataStart = RESV + 2 * fatSz;
        long totalData = partSectors - dataStart;
        long nClusters = totalData / spc;
        uint freeClusters = nClusters > 1 ? (uint)(nClusters - 1) : 0;

        byte[] mbr = BuildMbr(totalSectors);
        byte[] vbr = BuildVbr(partSectors, spc, fatSz, 2u, label, false);
        byte[] fsi = BuildFsInfo(freeClusters, 3u);
        byte[] vbrBak = BuildVbr(partSectors, spc, fatSz, 2u, label, true);
        byte[] fsiBak = BuildFsInfo(freeClusters, 3u);
        byte[] fatHd = new byte[512];
        LE32(fatHd, 0, 0x0FFFFFF8u); LE32(fatHd, 4, 0x0FFFFFFFu); LE32(fatHd, 8, 0x0FFFFFFFu);

        long totalBytes = (PART_START + RESV + 2L * fatSz + spc) * 512 + (quick ? 0 : totalData * 512);
        long done = 0; var sw = Stopwatch.StartNew(); var last = TimeSpan.Zero;
        var zero = new byte[512];
        var zeroChunk = new byte[1 << 20];   // 1 MiB of zeros for bulk fills

        using var s = backend.OpenWrite(disk);   // seeks to 0; we write strictly sequentially

        void W(byte[] sector) { ct.ThrowIfCancellationRequested(); s.Write(sector, 0, 512); done += 512; Tick(); }
        void Zeros(long sectors)
        {
            long bytes = sectors * 512;
            while (bytes > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(zeroChunk.Length, bytes);
                s.Write(zeroChunk, 0, n); bytes -= n; done += n; Tick();
            }
        }
        void Tick()
        {
            var now = sw.Elapsed;
            if ((now - last).TotalMilliseconds < 200) return;
            last = now;
            double mbps = now.TotalSeconds > 0.01 ? done / (1024.0 * 1024.0) / now.TotalSeconds : 0;
            progress.Report(new ImagingProgress(Phase.Formatting, done, totalBytes, mbps, now, "Formatting FAT32…"));
        }

        progress.Report(new ImagingProgress(Phase.Formatting, 0, totalBytes, 0, sw.Elapsed, "Writing filesystem…"));
        W(mbr);                     // sector 0
        Zeros(PART_START - 1);      // gap 1..2047
        W(vbr); W(fsi); Zeros(4); W(vbrBak); W(fsiBak); Zeros(RESV - 8);   // reserved region (32 sectors)
        W(fatHd); Zeros(fatSz - 1); // FAT1
        W(fatHd); Zeros(fatSz - 1); // FAT2
        Zeros(spc);                 // root directory (cluster 2)
        if (!quick) Zeros(totalData - spc);   // zero the rest of the data area
        s.Flush();

        backend.Rescan(disk);
        progress.Report(new ImagingProgress(Phase.Done, done, totalBytes, 0, sw.Elapsed, "Format complete."));
    }

    // ── pure structure builders (unit-tested) ─────────────────────────────────
    public static byte ChooseSpc(long totalSectors)
    {
        if (totalSectors < 532480L) return 1;
        if (totalSectors < 16777216L) return 8;
        if (totalSectors < 33554432L) return 16;
        if (totalSectors < 67108864L) return 32;
        return 64;
    }

    public static uint CalcFatSz32(long totalSectors, uint resvSectors, byte spc)
    {
        long dataSectors = totalSectors - resvSectors;
        long numClusters = dataSectors / spc;
        long fatBytes = (numClusters + 2) * 4L;
        return (uint)((fatBytes + 511) / 512);
    }

    public static byte[] BuildMbr(long totalSectors)
    {
        var mbr = new byte[512];
        mbr[0] = 0xEB; mbr[1] = 0x58; mbr[2] = 0x90;
        int pe = 446;
        mbr[pe] = 0x80;
        mbr[pe + 1] = 0x00; mbr[pe + 2] = 0x02; mbr[pe + 3] = 0x00;
        mbr[pe + 4] = 0x0C;
        mbr[pe + 5] = 0xFE; mbr[pe + 6] = 0xFF; mbr[pe + 7] = 0xFF;
        LE32(mbr, pe + 8, 2048);
        long partSectors = totalSectors - 2048;
        LE32(mbr, pe + 12, (uint)Math.Min(partSectors, 0xFFFFFFFFL));
        LE32(mbr, 440, StableSig(totalSectors));
        mbr[510] = 0x55; mbr[511] = 0xAA;
        return mbr;
    }

    public static byte[] BuildVbr(long partSectors, byte spc, uint fatSz, uint rootClus, string label, bool isBackup = false)
    {
        var vbr = new byte[512];
        vbr[0] = 0xEB; vbr[1] = 0x58; vbr[2] = 0x90;
        Array.Copy(Encoding.ASCII.GetBytes("MSDOS5.0"), 0, vbr, 3, 8);
        LE16(vbr, 11, 512);
        vbr[13] = spc;
        LE16(vbr, 14, 32);
        vbr[16] = 2;
        LE16(vbr, 17, 0); LE16(vbr, 19, 0);
        vbr[21] = 0xF8;
        LE16(vbr, 22, 0);
        LE16(vbr, 24, 63); LE16(vbr, 26, 255);
        LE32(vbr, 28, 2048);
        LE32(vbr, 32, (uint)Math.Min(partSectors, 0xFFFFFFFFL));
        LE32(vbr, 36, fatSz);
        LE16(vbr, 40, 0); LE16(vbr, 42, 0);
        LE32(vbr, 44, rootClus);
        LE16(vbr, 48, 1); LE16(vbr, 50, 6);
        vbr[64] = 0x00; vbr[66] = 0x29;
        LE32(vbr, 67, StableSig(partSectors ^ 0x5A5A));
        string lbl = (label.Length > 0 ? label : "NO NAME").ToUpperInvariant();
        if (lbl.Length > 11) lbl = lbl[..11]; else lbl = lbl.PadRight(11);
        Array.Copy(Encoding.ASCII.GetBytes(lbl), 0, vbr, 71, 11);
        Array.Copy(Encoding.ASCII.GetBytes("FAT32   "), 0, vbr, 82, 8);
        vbr[510] = 0x55; vbr[511] = 0xAA;
        return vbr;
    }

    public static byte[] BuildFsInfo(uint freeClusters, uint nextFree)
    {
        var fs = new byte[512];
        fs[0] = 0x52; fs[1] = 0x52; fs[2] = 0x61; fs[3] = 0x41;
        fs[484] = 0x72; fs[485] = 0x72; fs[486] = 0x41; fs[487] = 0x61;
        LE32(fs, 488, freeClusters);
        LE32(fs, 492, nextFree);
        fs[508] = 0x00; fs[509] = 0x00; fs[510] = 0x55; fs[511] = 0xAA;
        return fs;
    }

    // deterministic 32-bit "signature" (avoids RNG so builders are unit-testable)
    static uint StableSig(long seed)
    {
        ulong x = (ulong)seed * 2654435761UL + 0x9E3779B97F4A7C15UL;
        x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL; x ^= x >> 33;
        return (uint)x;
    }

    static void LE16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    static void LE32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
}
