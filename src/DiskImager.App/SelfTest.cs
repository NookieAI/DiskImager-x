using System;
using System.IO;
using System.IO.Compression;
using System.Text;
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
}
