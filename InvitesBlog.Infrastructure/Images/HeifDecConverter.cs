using System.Diagnostics;
using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Images;

/// <summary>
/// <see cref="IHeicConverter"/> through libheif's command-line decoder (package libheif-examples, with the
/// libheif-plugin-libde265 decoder), installed in the API image. It is <c>heif-dec</c> from libheif 1.18 and
/// <c>heif-convert</c> before that (the API image, Ubuntu 24.04, has 1.17), with the same arguments. It applies the picture's rotation and mirroring
/// itself and writes a JPEG; libheif's security limits stay on. Each run gets its own empty folder, a time limit, and
/// is killed when it runs over.
/// </summary>
public sealed class HeifDecConverter(IConfiguration config, ILogger<HeifDecConverter> logger) : IHeicConverter
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private IEnumerable<string> Tools => config["Images:HeifDec"] is { Length: > 0 } set ? [set] : ["heif-dec", "heif-convert"];

    public async Task<byte[]?> ToJpegAsync(byte[] heic, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ib-heic", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "in.heic");
        var output = Path.Combine(dir, "out.jpg");
        try
        {
            await File.WriteAllBytesAsync(input, heic, ct);
            using var p = StartFirst(input, output, dir);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Limit);
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
                if (ct.IsCancellationRequested) throw;
                logger.LogWarning("heif-dec ran over {Seconds} s and was stopped.", Limit.TotalSeconds);
                return null;
            }
            await Task.WhenAll(stdout, stderr);
            if (p.ExitCode != 0 || !File.Exists(output))
            {
                var why = stderr.Result.Trim();
                logger.LogWarning("heif-dec couldn't read an uploaded HEIC (exit {Code}): {Error}",
                    p.ExitCode, why.Length > 300 ? why[..300] : why);
                return null;
            }
            return await File.ReadAllBytesAsync(output, ct);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogWarning("heif-dec isn't available or failed: {Error}", e.Message);
            return null;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The first decoder that is installed, started; null when none is.</summary>
    private Process? StartFirst(string input, string output, string dir)
    {
        foreach (var tool in Tools)
        {
            var start = new ProcessStartInfo(tool)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir,
            };
            foreach (var a in new[] { "-q", "95", input, output }) start.ArgumentList.Add(a);
            try
            {
                return Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed under this name: try the next.
            }
        }
        logger.LogWarning("No HEIC decoder (heif-dec or heif-convert) is installed, so HEIC uploads can't be read.");
        return null;
    }
}
