using System.Diagnostics;
using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Images;

/// <summary>
/// <see cref="IHeicConverter"/> through libheif's <c>heif-dec</c> (Debian package libheif-examples, with the
/// libheif-plugin-libde265 decoder), installed in the API image. It applies the picture's rotation and mirroring
/// itself and writes a JPEG; libheif's security limits stay on. Each run gets its own empty folder, a time limit, and
/// is killed when it runs over.
/// </summary>
public sealed class HeifDecConverter(IConfiguration config, ILogger<HeifDecConverter> logger) : IHeicConverter
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private string Tool => config["Images:HeifDec"] ?? "heif-dec";

    public async Task<byte[]?> ToJpegAsync(byte[] heic, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ib-heic", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "in.heic");
        var output = Path.Combine(dir, "out.jpg");
        try
        {
            await File.WriteAllBytesAsync(input, heic, ct);
            var start = new ProcessStartInfo(Tool)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir,
            };
            foreach (var a in new[] { "-q", "95", input, output }) start.ArgumentList.Add(a);
            using var p = Process.Start(start);
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
}
