using System;
using System.IO;
using System.Linq;
using System.Threading;
using DiskImagerX.Disk;
using DiskImagerX.Engine;

namespace DiskImagerX;

/// <summary>
/// End-to-end raw-I/O round-trip against a Microsoft Virtual Disk (VHD) that the driver
/// script has already attached. Exercises the real backend + engine (format / backup /
/// restore / verify, raw + gzip). SAFETY: only ever operates on a non-system disk whose
/// model is a "Virtual Disk" and whose size matches the expected VHD — never a real drive.
/// </summary>
internal static class E2ETest
{
    static int _pass, _fail;
    static TextWriter _log = Console.Out;
    static void Ok(bool c, string name) { if (c) { _pass++; _log.WriteLine("  ok   " + name); } else { _fail++; _log.WriteLine("  FAIL " + name); } }

    public static int Run(long expectedSize, string logPath)
    {
        using var log = new StreamWriter(logPath, false) { AutoFlush = true };
        _log = log;
        var backend = BackendFactory.Create();
        _log.WriteLine("=== DiskImager.X E2E (raw round-trip) ===");
        _log.WriteLine($"platform={backend.PlatformName} elevated={backend.IsElevated()} expect~{expectedSize / (1024 * 1024)}MB");
        if (!backend.IsElevated()) { _log.WriteLine("ABORT: not elevated"); return 90; }

        // WMI can lag device changes, so retry enumeration until the throwaway VHD appears.
        DiskInfo? vhd = null;
        for (int attempt = 0; attempt < 20 && vhd is null; attempt++)
        {
            var disks = backend.EnumerateAsync().GetAwaiter().GetResult();
            vhd = disks.FirstOrDefault(d =>
                !d.IsSystem &&
                d.Model.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(d.SizeBytes - expectedSize) < 8L * 1024 * 1024);
            if (vhd is null)
            {
                _log.WriteLine($"  attempt {attempt}: {disks.Count} disk(s), VHD not visible yet — " +
                               string.Join(", ", disks.Select(d => d.Id)));
                Thread.Sleep(1000);
            }
        }

        // Hard safety gate — refuse to run if we can't positively identify the throwaway VHD.
        if (vhd is null || vhd.IsSystem || !vhd.Model.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
        { _log.WriteLine("ABORT (SAFE): no matching virtual disk; refusing to touch any real disk."); return 91; }

        _log.WriteLine($"TARGET: {vhd.DevicePath} {vhd.SizeText} '{vhd.Model}'");
        var ct = CancellationToken.None;
        var quiet = new Progress<ImagingProgress>(_ => { });
        string dir = Path.GetDirectoryName(logPath)!;
        string img = Path.Combine(dir, "e2e_raw.img");
        string gz = Path.Combine(dir, "e2e_gz.img.gz");

        try
        {
            // A) RAW backup → zero → restore → verify round-trip (random data = no auto-mount)
            WritePattern(backend, vhd, seed: 12345);
            Imaging.Backup(backend, vhd, img, gzip: false, sha256: false, quiet, ct);
            Ok(new FileInfo(img).Length == vhd.SizeBytes, "raw backup: image size == disk size");
            VerifyStep(backend, vhd, img, "raw backup matches disk");
            ZeroDisk(backend, vhd);
            Imaging.Restore(backend, vhd, img, smartRestore: false, verify: false, quiet, ct);
            VerifyStep(backend, vhd, img, "raw restore round-trip");

            // B) GZIP backup (+SHA-256 sidecar) → restore → verify --------------------
            WritePattern(backend, vhd, seed: 67890);
            Imaging.Backup(backend, vhd, gz, gzip: true, sha256: true, quiet, ct);
            Ok(File.Exists(gz + ".sha256"), "gzip backup: .sha256 sidecar written");
            Ok(new FileInfo(gz).Length < vhd.SizeBytes, "gzip backup: compressed smaller than disk");
            ZeroDisk(backend, vhd);
            Imaging.Restore(backend, vhd, gz, smartRestore: false, verify: false, quiet, ct);
            VerifyStep(backend, vhd, gz, "gzip restore round-trip");

            // C) FORMAT writes a valid FAT32 MBR + VBR — LAST, since Windows will auto-mount
            //    the new filesystem (nothing after this needs raw write access to the VHD).
            Fat32.Format(backend, vhd, "E2ETEST", quick: true, quiet, ct);
            byte[] s0 = ReadSectorAt(backend, vhd, 0);
            Ok(s0[510] == 0x55 && s0[511] == 0xAA, "format: MBR boot signature");
            Ok(s0[446 + 4] == 0x0C, "format: MBR partition type FAT32-LBA");
            byte[] vbr = ReadSectorAt(backend, vhd, 2048L * 512);
            Ok(vbr[510] == 0x55 && vbr[511] == 0xAA, "format: VBR boot signature");
            Ok(System.Text.Encoding.ASCII.GetString(vbr, 82, 8) == "FAT32   ", "format: VBR FS type FAT32");
        }
        catch (Exception ex) { _fail++; _log.WriteLine("  FAIL exception: " + ex.Message); }
        finally { try { File.Delete(img); File.Delete(gz); File.Delete(gz + ".sha256"); } catch { } }

        _log.WriteLine($"=== {_pass} passed, {_fail} failed ===");
        return _fail;
    }

    // Let Windows settle the disk stack between rapid destructive cycles (rescan + brief pause).
    static void Settle(IDiskBackend backend, DiskInfo disk) { try { backend.Rescan(disk); } catch { } Thread.Sleep(1500); }

    static void VerifyStep(IDiskBackend backend, DiskInfo disk, string image, string name)
    {
        try { Imaging.Verify(backend, disk, image, smartRestore: false, new Progress<ImagingProgress>(_ => { }), CancellationToken.None); Ok(true, name); }
        catch (Exception ex) { Ok(false, name + " — " + ex.Message); }
    }

    static byte[] ReadSectorAt(IDiskBackend backend, DiskInfo disk, long offset)
    {
        using var s = backend.OpenRead(disk);
        s.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[512]; int n = 0;
        while (n < 512) { int r = s.Read(buf, n, 512 - n); if (r == 0) break; n += r; }
        return buf;
    }

    static void WritePattern(IDiskBackend backend, DiskInfo disk, int seed)
    {
        const int BUF = 4 * 1024 * 1024;
        var rnd = new Random(seed);
        var buf = new byte[BUF];
        using var s = backend.OpenWrite(disk);
        long left = disk.SizeBytes;
        while (left > 0)
        {
            int n = (int)Math.Min(BUF, left);
            rnd.NextBytes(buf.AsSpan(0, n));
            s.Write(buf, 0, n); left -= n;
        }
        s.Flush();
    }

    static void ZeroDisk(IDiskBackend backend, DiskInfo disk)
    {
        const int BUF = 4 * 1024 * 1024;
        var buf = new byte[BUF];
        using var s = backend.OpenWrite(disk);
        long left = disk.SizeBytes;
        while (left > 0) { int n = (int)Math.Min(BUF, left); s.Write(buf, 0, n); left -= n; }
        s.Flush();
    }
}
