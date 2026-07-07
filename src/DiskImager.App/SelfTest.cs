using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiskImagerX.Disk;
using DiskImagerX.Engine;

namespace DiskImagerX;

/// <summary>Pure-function tests for the portable engine (FAT32 builders, image detection/open).
/// Run with <c>--selftest</c>; exit code = number of failed assertions.</summary>
internal static class SelfTest
{
    static int _pass, _fail;
    static void Ok(bool c, string name) { if (c) _pass++; else { _fail++; Console.WriteLine("  FAIL: " + name); } }
    static void Eq(long a, long b, string name) { Ok(a == b, name + $" (got {a}, want {b})"); }

    public static int Run()
    {
        Console.WriteLine("=== DiskImager.X self-test ===");
        Fat32Tests();
        ImageTests();
        GzipParallelTests();
        EngineRoundTripTests();
        Console.WriteLine($"=== {_pass} passed, {_fail} failed ===");
        return _fail;
    }

    static void Fat32Tests()
    {
        // ChooseSpc thresholds
        Eq(Fat32.ChooseSpc(500000), 1, "spc <260MB");
        Eq(Fat32.ChooseSpc(1_000_000), 8, "spc <8GB");
        Eq(Fat32.ChooseSpc(20_000_000), 16, "spc <16GB");
        Eq(Fat32.ChooseSpc(40_000_000), 32, "spc <32GB");
        Eq(Fat32.ChooseSpc(200_000_000), 64, "spc >=32GB");

        // CalcFatSz32 grows with size, always >=1
        Ok(Fat32.CalcFatSz32(1_000_000, 32, 8) >= 1, "fatSz >=1");
        Ok(Fat32.CalcFatSz32(100_000_000, 32, 8) > Fat32.CalcFatSz32(1_000_000, 32, 8), "fatSz grows");

        // MBR
        var mbr = Fat32.BuildMbr(60_000_000);
        Eq(mbr.Length, 512, "mbr len");
        Ok(mbr[510] == 0x55 && mbr[511] == 0xAA, "mbr boot sig");
        Eq(mbr[446], 0x80, "mbr bootable");
        Eq(mbr[446 + 4], 0x0C, "mbr type FAT32-LBA");
        Eq(BitConverter.ToUInt32(mbr, 446 + 8), 2048u, "mbr LBA start 2048");
        Eq(BitConverter.ToUInt32(mbr, 446 + 12), 60_000_000u - 2048u, "mbr part sectors");
        Ok(BitConverter.ToUInt32(mbr, 446 + 12) == BitConverter.ToUInt32(Fat32.BuildMbr(60_000_000), 446 + 12), "mbr deterministic");

        // VBR
        var vbr = Fat32.BuildVbr(59_997_952, 64, 512, 2, "MYSTICK");
        Eq(vbr.Length, 512, "vbr len");
        Ok(vbr[510] == 0x55 && vbr[511] == 0xAA, "vbr boot sig");
        Eq(BitConverter.ToUInt16(vbr, 11), 512, "vbr bytes/sector");
        Eq(vbr[13], 64, "vbr spc");
        Eq(BitConverter.ToUInt16(vbr, 14), 32, "vbr reserved");
        Eq(vbr[16], 2, "vbr numFATs");
        Eq(BitConverter.ToUInt32(vbr, 36), 512u, "vbr fatSz32");
        Eq(BitConverter.ToUInt32(vbr, 44), 2u, "vbr root cluster");
        Ok(Encoding.ASCII.GetString(vbr, 82, 8) == "FAT32   ", "vbr fs type");
        Ok(Encoding.ASCII.GetString(vbr, 71, 11).StartsWith("MYSTICK"), "vbr label");

        // FSInfo
        var fsi = Fat32.BuildFsInfo(12345, 3);
        Ok(fsi[0] == 0x52 && fsi[1] == 0x52 && fsi[2] == 0x61 && fsi[3] == 0x41, "fsinfo lead RRaA");
        Ok(fsi[484] == 0x72 && fsi[485] == 0x72 && fsi[486] == 0x41 && fsi[487] == 0x61, "fsinfo struct rrAa");
        Eq(BitConverter.ToUInt32(fsi, 488), 12345u, "fsinfo free");
        Eq(BitConverter.ToUInt32(fsi, 492), 3u, "fsinfo next");
        Ok(fsi[510] == 0x55 && fsi[511] == 0xAA, "fsinfo trail");
    }

    static void ImageTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dix_tests");
        Directory.CreateDirectory(dir);
        var payload = new byte[8192];
        new Random(7).NextBytes(payload);

        // raw
        string raw = Path.Combine(dir, "t.img");
        File.WriteAllBytes(raw, payload);
        Ok(ImageSource.Detect(raw).StartsWith("Raw"), "detect raw");
        using (var o = ImageSource.Open(raw).Stream) Ok(ReadAll(o).AsSpan().SequenceEqual(payload), "open raw round-trip");

        // gzip
        string gz = Path.Combine(dir, "t.img.gz");
        using (var fs = File.Create(gz)) using (var g = new GZipStream(fs, CompressionLevel.Optimal)) g.Write(payload);
        Ok(ImageSource.Detect(gz).StartsWith("gzip"), "detect gzip");
        using (var o = ImageSource.Open(gz).Stream) Ok(ReadAll(o).AsSpan().SequenceEqual(payload), "open gzip round-trip");

        // zip
        string zip = Path.Combine(dir, "t.zip");
        using (var fs = File.Create(zip)) using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        { var e = za.CreateEntry("disk.img"); using var es = e.Open(); es.Write(payload); }
        Ok(ImageSource.Detect(zip).StartsWith("ZIP"), "detect zip");
        using (var o = ImageSource.Open(zip).Stream) Ok(ReadAll(o).AsSpan().SequenceEqual(payload), "open zip round-trip");

        // vhd (raw + 512-byte footer)
        string vhd = Path.Combine(dir, "t.vhd");
        using (var fs = File.Create(vhd)) { fs.Write(payload); fs.Write(new byte[512]); }
        Ok(ImageSource.Detect(vhd).StartsWith("VHD"), "detect vhd");
        var v = ImageSource.Open(vhd);
        Eq(v.SizeHint, payload.Length, "vhd size = data (excl footer)");
        using (var o = v.Stream) Ok(ReadAll(o).AsSpan().SequenceEqual(payload), "open vhd round-trip (footer excluded)");

        // LimitedStream caps
        using (var ms = new MemoryStream(payload))
        using (var lim = new LimitedStream(ms, 100))
            Eq(ReadAll(lim).Length, 100, "LimitedStream caps at 100");
    }

    static byte[] ReadAll(Stream s) { using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }

    // ── parallel gzip writer ───────────────────────────────────────────────────
    static void GzipParallelTests()
    {
        // 10 MiB spanning 3 members (4+4+2): compressible, zeros, random
        var payload = new byte[10 * 1024 * 1024];
        for (int i = 0; i < 4 << 20; i++) payload[i] = (byte)(i % 61 + 32);
        new Random(11).NextBytes(payload.AsSpan(8 << 20));

        using var gz = new MemoryStream();
        long read = GzipParallel.Compress(new MemoryStream(payload), gz, payload.Length, _ => { }, default);
        Eq(read, payload.Length, "pgz reads all input");

        var file = gz.ToArray();
        Ok(file[0] == 0x1F && file[1] == 0x8B, "pgz gzip magic");
        Ok((file[3] & 4) != 0, "pgz FEXTRA flag set");

        // the standard reader must decompress the multi-member stream byte-for-byte
        using (var dec = new GZipStream(new MemoryStream(file), CompressionMode.Decompress))
            Ok(ReadAll(dec).AsSpan().SequenceEqual(payload), "pgz multi-member round-trip");

        // exact total from the FEXTRA header (trailing ISIZE only covers the last member)
        Eq(GzipParallel.ReadSizeHeader(new MemoryStream(file)), payload.Length, "pgz FEXTRA size header");

        // a foreign single-member gz has no FEXTRA -> -1
        using var plain = new MemoryStream();
        using (var g = new GZipStream(plain, CompressionLevel.Fastest, leaveOpen: true)) g.Write(payload, 0, 1024);
        plain.Position = 0;
        Eq(GzipParallel.ReadSizeHeader(plain), -1, "foreign gz: no size header");
    }

    // ── full engine round-trips through an in-memory disk ─────────────────────
    sealed class MemBackend : Disk.IDiskBackend
    {
        public byte[] Data;
        public MemBackend(byte[] data) { Data = data; }
        public string PlatformName => "Test";
        public string ElevationHint => "";
        public bool IsElevated() => true;
        public Task<IReadOnlyList<DiskInfo>> EnumerateAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DiskInfo>>(Array.Empty<DiskInfo>());
        public Stream OpenRead(DiskInfo d) => new MemoryStream(Data, writable: false);
        public Stream OpenWrite(DiskInfo d) => new MemoryStream(Data, writable: true);
        public void Rescan(DiskInfo d) { }
        public void Eject(DiskInfo d) { }
        public Task<string?> MountImageAsync(string p, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    sealed class NullProgress : IProgress<ImagingProgress> { public void Report(ImagingProgress p) { } }

    static void EngineRoundTripTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dix_tests");
        Directory.CreateDirectory(dir);
        var prog = new NullProgress();

        // 12 MiB "disk": chunk0 patterned · chunk1 all-zero (smart-skip target) · chunk2 random
        var disk = new byte[12 * 1024 * 1024];
        for (int i = 0; i < 4 << 20; i++) disk[i] = (byte)(i % 251 + 1);
        new Random(23).NextBytes(disk.AsSpan(8 << 20));
        var info = new DiskInfo { Id = "mem0", DevicePath = "mem://0", Model = "MemDisk", SizeBytes = disk.Length };

        // raw backup (pipelined path)
        string rawImg = Path.Combine(dir, "e.img");
        Imaging.Backup(new MemBackend(disk), info, rawImg, gzip: false, sha256: false, prog, default);
        Ok(File.ReadAllBytes(rawImg).AsSpan().SequenceEqual(disk), "backup raw == disk");

        // gzip backup (parallel path) + sha sidecar
        string gzImg = Path.Combine(dir, "e.img.gz");
        Imaging.Backup(new MemBackend(disk), info, gzImg, gzip: true, sha256: true, prog, default);
        var opened = ImageSource.Open(gzImg);
        Eq(opened.SizeHint, disk.Length, "gz backup size hint exact");
        using (opened.Stream) Ok(ReadAll(opened.Stream).AsSpan().SequenceEqual(disk), "gz backup decompresses to disk");
        string sidecar = File.ReadAllText(gzImg + ".sha256");
        string wantHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(gzImg))).ToLowerInvariant();
        Ok(sidecar.StartsWith(wantHash), "sha256 sidecar matches gz file");

        // full restore of the gz image onto a 0xFF-filled disk -> byte-for-byte
        var target = new byte[disk.Length];
        Array.Fill(target, (byte)0xFF);
        var tb = new MemBackend(target);
        Imaging.Restore(tb, info, gzImg, smartRestore: false, verify: false, prog, default);
        Ok(target.AsSpan().SequenceEqual(disk), "restore full == source");

        // smart restore skips the all-zero chunk (pre-fill survives there, rest matches)
        var smart = new byte[disk.Length];
        Array.Fill(smart, (byte)0xFF);
        Imaging.Restore(new MemBackend(smart), info, gzImg, smartRestore: true, verify: false, prog, default);
        Ok(smart.AsSpan(0, 4 << 20).SequenceEqual(disk.AsSpan(0, 4 << 20)), "smart restore writes data chunks");
        Ok(smart.AsSpan(4 << 20, 4 << 20).IndexOfAnyExcept((byte)0xFF) < 0, "smart restore skips zero chunk");

        // verify: passes against the matching disk; smart verify passes on the smart-restored one
        Imaging.Verify(new MemBackend(target), info, gzImg, smartRestore: false, prog, default);
        Ok(true, "verify full passes");
        Imaging.Verify(new MemBackend(smart), info, gzImg, smartRestore: true, prog, default);
        Ok(true, "verify smart passes");

        // verify catches corruption
        var corrupt = (byte[])disk.Clone();
        corrupt[9 << 20] ^= 0x5A;
        bool threw = false;
        try { Imaging.Verify(new MemBackend(corrupt), info, gzImg, smartRestore: false, prog, default); }
        catch (IOException) { threw = true; }
        Ok(threw, "verify detects corruption");

        // raw image larger than the disk is rejected up front by the size check
        string overRaw = Path.Combine(dir, "over.img");
        using (var f = File.Create(overRaw)) { f.Write(disk); f.Write(new byte[4096]); }
        threw = false;
        try { Imaging.Restore(new MemBackend(new byte[disk.Length]), info, overRaw, smartRestore: false, verify: false, prog, default); }
        catch (IOException) { threw = true; }
        Ok(threw, "oversize raw rejected up front");

        // foreign multi-member gzip: trailing ISIZE lies (last member only), so the size check
        // passes and the streaming tail logic must decide — zero tail tolerated, data tail aborts
        string overZ = Path.Combine(dir, "overz.img.gz");
        using (var f = File.Create(overZ))
        {
            using (var g = new GZipStream(f, CompressionLevel.Fastest, leaveOpen: true)) g.Write(disk);
            using (var g = new GZipStream(f, CompressionLevel.Fastest, leaveOpen: true)) g.Write(new byte[4096]);
        }
        var t2 = new byte[disk.Length];
        Imaging.Restore(new MemBackend(t2), info, overZ, smartRestore: false, verify: false, prog, default);
        Ok(t2.AsSpan().SequenceEqual(disk), "oversize zero tail tolerated");

        string overNz = Path.Combine(dir, "overnz.img.gz");
        using (var f = File.Create(overNz))
        {
            using (var g = new GZipStream(f, CompressionLevel.Fastest, leaveOpen: true)) g.Write(disk);
            var tail = new byte[4096]; Array.Fill(tail, (byte)0xAB);
            using (var g = new GZipStream(f, CompressionLevel.Fastest, leaveOpen: true)) g.Write(tail);
        }
        threw = false;
        try { Imaging.Restore(new MemBackend(new byte[disk.Length]), info, overNz, smartRestore: false, verify: false, prog, default); }
        catch (IOException) { threw = true; }
        Ok(threw, "oversize nonzero tail aborts");

        // cancellation propagates
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        threw = false;
        try { Imaging.Backup(new MemBackend(disk), info, Path.Combine(dir, "c.img"), false, false, prog, cts.Token); }
        catch (OperationCanceledException) { threw = true; }
        Ok(threw, "cancel propagates");
    }
}
