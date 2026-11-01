using System.Diagnostics;
using Amazon.S3;
using Amazon.S3.Model;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Common;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Accounts;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Video;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Security.Claims;
using Xunit;

namespace InvitesBlog.Tests.Integration;

/// <summary>Needs real Postgres, real MinIO and ffmpeg. Reports as skipped without them, never as passed.</summary>
public sealed class VideoStackFactAttribute : FactAttribute
{
    public VideoStackFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresApiFixture.ServerVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VideoStackFixture.MinioVariable)))
            Skip = $"Set {PostgresApiFixture.ServerVariable} and {VideoStackFixture.MinioVariable} to run against the real stack.";
    }
}

/// <summary>Real ffmpeg with places to step in: to change the outcome, or to do something mid-job.</summary>
public sealed class SteppableTools(IVideoTools real) : IVideoTools
{
    public Func<string, string, VideoKind, CancellationToken, Task<VideoCompressOutcome>>? Compress;
    public Func<Task>? BeforeCompress;
    public int Compressions;
    public bool Available => real.Available;
    public string NewWorkDir() => real.NewWorkDir();
    public void ClearWorkDir() => real.ClearWorkDir();
    public Func<long, bool>? Room;
    public bool HasRoomFor(long bytes) => Room?.Invoke(bytes) ?? real.HasRoomFor(bytes);
    public Func<bool>? StripFails;
    public Task<VideoProbe?> ProbeAsync(string path, VideoKind kind, CancellationToken ct = default) => real.ProbeAsync(path, kind, ct);
    public Task<bool> StripMetadataAsync(string input, string output, VideoKind kind, CancellationToken ct = default) =>
        StripFails?.Invoke() == true ? Task.FromResult(false) : real.StripMetadataAsync(input, output, kind, ct);
    public Task<byte[]?> PosterAsync(string input, VideoKind kind, CancellationToken ct = default) => real.PosterAsync(input, kind, ct);
    public async Task<VideoCompressOutcome> CompressAsync(string input, string output, VideoKind kind, CancellationToken ct = default)
    {
        Compressions++;
        if (BeforeCompress is { } before) await before();
        return Compress is { } c ? await c(input, output, kind, ct) : await real.CompressAsync(input, output, kind, ct);
    }
    public Task<bool> PlaysAsync(string path, VideoKind kind, double seconds, CancellationToken ct = default) => real.PlaysAsync(path, kind, seconds, ct);
    public void Reset() { Compress = null; BeforeCompress = null; Room = null; StripFails = null; Compressions = 0; }
}

/// <summary>The API on real Postgres and MinIO (both buckets), with the trim service driven by hand.</summary>
public sealed class VideoStackFixture : IAsyncLifetime
{
    public const string MinioVariable = "IB_TEST_MINIO";
    public const string Secret = "integration-test-signing-key-long-enough-for-hmac-sha256-0123456789";

    private string _database = default!;
    private string _server = default!;
    private Factory? _factory;
    public string Bucket { get; private set; } = default!;
    public string Holding { get; private set; } = default!;
    public IAmazonS3 S3 { get; private set; } = default!;
    public string Files { get; private set; } = default!;
    public SteppableTools Tools { get; private set; } = default!;
    public IServiceProvider Services => _factory!.Services;

    public async Task InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable(PostgresApiFixture.ServerVariable);
        // "endpoint|user|password"
        var minio = Environment.GetEnvironmentVariable(MinioVariable);
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(minio)) return;
        var m = minio.Split('|');
        _server = server;
        _database = $"ib_vid_{Guid.NewGuid():N}";
        Bucket = $"ib-assets-{Guid.NewGuid():N}"[..30];
        Holding = $"ib-incoming-{Guid.NewGuid():N}"[..30];
        Files = Directory.CreateTempSubdirectory("ib-video-test").FullName;

        S3 = new AmazonS3Client(m[1], m[2], new AmazonS3Config { ServiceURL = m[0], ForcePathStyle = true });
        await S3.PutBucketAsync(Bucket);
        await S3.PutBucketAsync(Holding);

        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_server) { Database = "postgres" }.ToString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {_database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        await PostgresApiFixture.EnvLock.WaitAsync();
        try
        {
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["ConnectionStrings__Postgres"] = new NpgsqlConnectionStringBuilder(_server) { Database = _database }.ToString(),
                ["Otp__Channels"] = "Email,Sms",
                ["Email__Provider"] = "Console",
                ["Storage__Provider"] = "Minio",
                ["Storage__Endpoint"] = m[0],
                ["Storage__Bucket"] = Bucket,
                ["Storage__IncomingBucket"] = Holding,
                ["Storage__AccessKey"] = m[1],
                ["Storage__SecretKey"] = m[2],
                ["Uploads__Direct"] = "on",
                ["Video__Preset"] = "ultrafast",
                ["Payments__Provider"] = "Fake",
                ["Urls__ApiBase"] = "http://localhost",
                ["Urls__InviterBase"] = "http://localhost",
                ["Urls__InviteeBase"] = "http://localhost",
                ["Urls__AssetsBase"] = $"{m[0]}/{Bucket}",
                ["Jwt__SigningKey"] = Secret,
            })
                Environment.SetEnvironmentVariable(key, value);

            _factory = new Factory(this);
            _ = _factory.Server;
        }
        finally
        {
            // The Storage settings must not reach a fixture starting after this one.
            Environment.SetEnvironmentVariable("Storage__Endpoint", null);
            Environment.SetEnvironmentVariable("Storage__IncomingBucket", null);
            Environment.SetEnvironmentVariable("Uploads__Direct", null);
            PostgresApiFixture.EnvLock.Release();
        }
    }

    public async Task DisposeAsync()
    {
        if (_factory is null) return;
        await _factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_server) { Database = "postgres" }.ToString());
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        foreach (var bucket in new[] { Bucket, Holding })
        {
            var page = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
            foreach (var o in page.S3Objects ?? []) await S3.DeleteObjectAsync(bucket, o.Key);
            await S3.DeleteBucketAsync(bucket);
        }
        Directory.Delete(Files, recursive: true);
    }

    public HttpClient Client(string token)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task WriteAsync(Func<AppDbContext, Task> write)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await write(db);
        await db.SaveChangesAsync();
    }

    public async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public string TokenFor(Guid userId, string email)
    {
        var issuer = Services.GetRequiredService<IInviteeTokenIssuer>();
        return issuer.IssueForRoles([Roles.Customer], new Dictionary<string, string>
        {
            [ClaimTypes.NameIdentifier] = userId.ToString(),
            ["contact_type"] = "email",
            ["contact"] = email,
        }, TimeSpan.FromDays(14));
    }

    private sealed class Factory(VideoStackFixture fixture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<PendingRemovalService>();
                services.AddSingleton<VideoTrimService>();
                var real = new FfmpegVideoTools(
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Video:Preset"] = "ultrafast",
                        ["Video:WorkDir"] = Path.Combine(fixture.Files, "work"),
                    }).Build(),
                    NullLogger<FfmpegVideoTools>.Instance);
                fixture.Tools = new SteppableTools(real);
                services.RemoveAll<IVideoTools>();
                services.AddSingleton<IVideoTools>(fixture.Tools);
            });
        }
    }
}

/// <summary>Makes test videos with the real ffmpeg, with a camera's metadata and a rotation in them.</summary>
public static class TestVideos
{
    public const string SecretPlace = "+04.1755+073.5093/";
    public const string SecretPhone = "SECRET-PHONE-MODEL";

    public static async Task MakeAsync(string path, int width, int height, int seconds, int fps, string extra = "-crf 12", int rotate = 90, string ext = "mp4")
    {
        var raw = path + ".raw." + ext;
        await RunAsync("ffmpeg",
            $"-nostdin -y -loglevel error -f lavfi -i testsrc2=s={width}x{height}:r={fps} -f lavfi -i sine=frequency=440:sample_rate=48000 -t {seconds} " +
            $"-vf noise=alls=30:allf=t -c:v libx264 -preset ultrafast {extra} -pix_fmt yuv420p -c:a aac " +
            $"-metadata location={SecretPlace} -metadata com.apple.quicktime.location.ISO6709={SecretPlace} -metadata com.android.manufacturer={SecretPhone} -metadata title={SecretPhone} " +
            $"-movflags use_metadata_tags {raw}");
        await RunAsync("ffmpeg", $"-nostdin -y -loglevel error -display_rotation {rotate} -i {raw} -c copy -map_metadata 0 -movflags use_metadata_tags {path}");
        File.Delete(raw);
    }

    public static async Task<string> RunAsync(string tool, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(tool, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{tool} {args}\n{await errors}");
        return await output;
    }

    /// <summary>Fails if a camera's words survive anywhere in the file's bytes or its tags.</summary>
    public static async Task AssertNoCameraMetadataAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        foreach (var word in new[] { SecretPlace, SecretPhone })
            Assert.True(bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(word)) < 0, $"{word} is still in the file");
        var tags = await RunAsync("ffprobe", $"-v error -show_entries format_tags -of json {path}");
        Assert.DoesNotContain("location", tags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("manufacturer", tags, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>width, height, as displayed (rotation applied by ffprobe's side data).</summary>
    public static async Task<VideoProbe> ProbeAsync(string path)
    {
        var json = await RunAsync("ffprobe", $"-v error -show_streams -show_format -of json {path}");
        return FfmpegVideoTools.Read(json)!;
    }
}
