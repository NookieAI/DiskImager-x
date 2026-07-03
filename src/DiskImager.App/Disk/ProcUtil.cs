using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DiskImagerX.Disk;

/// <summary>Small helper to run a command-line tool and capture stdout/stderr/exit.</summary>
internal static class ProcUtil
{
    public readonly record struct Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
    }

    public static async Task<Result> RunAsync(string file, string args, int timeoutMs = 30000,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        var outT = p.StandardOutput.ReadToEndAsync();
        var errT = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
        string so = await outT.ConfigureAwait(false);
        string se = await errT.ConfigureAwait(false);
        return new Result(p.HasExited ? p.ExitCode : -1, so, se);
    }

    public static Result Run(string file, string args, int timeoutMs = 30000)
        => RunAsync(file, args, timeoutMs).GetAwaiter().GetResult();
}
