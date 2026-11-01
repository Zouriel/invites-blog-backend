using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Services.Photos;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Accounts;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Video;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("video-stack")]
public sealed class VideoStackCollection : ICollectionFixture<VideoStackFixture>;

/// <summary>
/// A video's whole way through: link, PUT to the holding bucket, the checks, the public file, the trim.
/// Real Postgres, real MinIO, real ffmpeg; everything read back from a context of its own.
/// </summary>
[Collection("video-stack")]
public sealed class DirectVideoTests(VideoStackFixture stack) : IAsyncLifetime
{
    private static readonly HttpClient Plain = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static string? _medium;
    private static readonly SemaphoreSlim MakeLock = new(1, 1);

    /// <summary>The queue takes the oldest job first, so another test's leftover would be picked up instead of this test's.</summary>
    public async Task InitializeAsync()
    {
        if (stack.Tools is null) return;
        stack.Tools.Reset();
        await stack.WriteAsync(db => db.VideoTrimJobs
            .Where(j => j.Status == VideoTrimStatus.Waiting || j.Status == VideoTrimStatus.Running)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, VideoTrimStatus.Kept)));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A heavy clip of about 30 MB: over the "already small" line and quick to transcode.</summary>
    private async Task<string> MediumAsync()
    {
        await MakeLock.WaitAsync();
        try
        {
            if (_medium is not null) return _medium;
            var path = Path.Combine(stack.Files, "medium.mp4");
            await TestVideos.MakeAsync(path, 640, 360, 8, 30, "-crf 1");
            Assert.True(new FileInfo(path).Length > 21 * 1024 * 1024, $"made only {new FileInfo(path).Length} bytes");
            return _medium = path;
        }
        finally
        {
            MakeLock.Release();
        }
    }

    private sealed record Album(Guid Campaign, Guid Bucket, HttpClient Client);

    private async Task<Album> AlbumAsync()
    {
        var user = Guid.NewGuid();
        var email = $"v-{user:N}@example.com";
        var campaign = Guid.NewGuid();
        var bucket = Guid.NewGuid();
        var template = Guid.NewGuid();
        await stack.WriteAsync(async db =>
        {
            var role = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).FirstAsync();
            var person = new AppUser { Id = user, Email = email, DisplayName = "Host", PasswordHash = "x", IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            person.UserRoles.Add(new UserRole { RoleId = role });
            db.Users.Add(person);
            db.Templates.Add(new Template
            {
                Id = template, Name = "T", Slug = $"t-{template:N}", Version = "1.0.0", Category = "Imported", Description = "D", PreviewImageUrl = "",
                SceneJson = "{}", ManifestJson = "{}", PackageUrl = "", Visibility = TemplateVisibility.Imported,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Campaigns.Add(new Campaign
            {
                Id = campaign, TemplateId = template, TemplateVersion = "1.0.0", TemplatePackageUrl = "", CreatedByUserId = user,
                AccessTokenHash = Guid.NewGuid().ToString("N"), Title = "A wedding", Slug = $"e-{campaign:N}", Status = CampaignStatus.Dispatched,
                EventStartAt = DateTimeOffset.UtcNow.AddHours(1), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.MediaBuckets.Add(new MediaBucket
            {
                Id = bucket, CampaignId = campaign, OwnerUserId = user, EventDate = DateTimeOffset.UtcNow, UploadWindowDays = 3,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        });
        return new Album(campaign, bucket, stack.Client(stack.TokenFor(user, email)));
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
    }

    private static async Task PutAsync(string url, string file, string type)
    {
        await using var from = File.OpenRead(file);
        using var content = new StreamContent(from);
        content.Headers.ContentType = new(type);
        content.Headers.ContentLength = from.Length;
        var put = await Plain.PutAsync(url, content);
        Assert.True(put.IsSuccessStatusCode, $"PUT {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync()}");
    }

    /// <summary>What the processor does with a claimed upload.</summary>
    private async Task FinishAsync(Guid id)
    {
        await stack.WriteAsync(async db =>
        {
            var row = await db.PendingUploads.FirstAsync(u => u.Id == id);
            Assert.Equal(PendingUploadStatus.Uploaded, row.Status);
            row.Status = PendingUploadStatus.Processing;
        });
        using var scope = stack.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDirectUploadService>().FinishAsync(id);
    }

    /// <summary>Everything a client does: link, PUT, complete; then the server's own step.</summary>
    private async Task<Guid> UploadAsync(Album album, string file, string type = "video/mp4", string? poster = null)
    {
        var ticket = await DataAsync(await album.Client.PostAsJsonAsync($"/api/campaigns/{album.Campaign}/photos/uploads", new
        {
            fileName = Path.GetFileName(file),
            contentType = type,
            size = new FileInfo(file).Length,
            posterSize = poster is null ? (long?)null : new FileInfo(poster).Length,
        }));
        Assert.True(ticket.GetProperty("direct").GetBoolean());
        var id = ticket.GetProperty("id").GetGuid();
        await PutAsync(ticket.GetProperty("url").GetString()!, file, type);
        if (poster is not null) await PutAsync(ticket.GetProperty("posterUrl").GetString()!, poster, "image/jpeg");

        var done = await DataAsync(await album.Client.PostAsync($"/api/uploads/{id}/complete", null));
        Assert.Equal("processing", done.GetProperty("status").GetString());
        await FinishAsync(id);
        return id;
    }

    private Task<EventPhoto> PhotoAsync(Guid id) => stack.ReadAsync(db => db.EventPhotos.AsNoTracking().FirstAsync(p => p.Id == id));
    private Task<VideoTrimJob?> JobAsync(Guid id) => stack.ReadAsync(db => db.VideoTrimJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id));
    private Task<long> UsedAsync(Guid bucket) => stack.ReadAsync(db => db.MediaBuckets.Where(b => b.Id == bucket).Select(b => b.UsedBytes).FirstAsync());

    private string KeyOf(string url) => url[(url.IndexOf(stack.Bucket, StringComparison.Ordinal) + stack.Bucket.Length + 1)..];

    private async Task<long?> ObjectSizeAsync(string url)
    {
        try
        {
            return (await stack.S3.GetObjectMetadataAsync(stack.Bucket, KeyOf(url))).ContentLength;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<string> FetchAsync(string url, string name)
    {
        var path = Path.Combine(stack.Files, $"{name}-{Guid.NewGuid():N}.bin");
        using var res = await stack.S3.GetObjectAsync(stack.Bucket, KeyOf(url));
        await using var to = File.Create(path);
        await res.ResponseStream.CopyToAsync(to);
        return path;
    }

    private Task<bool> TrimAsync() => stack.Services.GetRequiredService<VideoTrimService>().RunOnceAsync(CancellationToken.None);

    private async Task AssertSpaceIsWhatIsStoredAsync(Album album, EventPhoto photo)
    {
        var video = await ObjectSizeAsync(photo.Url);
        var tile = await ObjectSizeAsync(photo.ThumbUrl);
        Assert.Equal(video + tile, photo.SizeBytes);
        Assert.Equal(photo.SizeBytes, await UsedAsync(album.Bucket));
    }

    // ----- the size that started this -----

    [VideoStackFact]
    public async Task A_video_over_cloudflares_limit_goes_through_whole_and_comes_out_trimmed()
    {
        var album = await AlbumAsync();
        var file = Path.Combine(stack.Files, "big.mp4");
        await TestVideos.MakeAsync(file, 1920, 1080, 6, 30, "-crf 18");
        var size = new FileInfo(file).Length;
        Assert.InRange(size, 120L * 1024 * 1024, 256L * 1024 * 1024);

        var id = await UploadAsync(album, file);

        // Viewable straight away, under the upload's id, without the camera's metadata, its shape kept.
        var stored = await PhotoAsync(id);
        Assert.Equal("video/mp4", stored.ContentType);
        var first = await FetchAsync(stored.Url, "first");
        await TestVideos.AssertNoCameraMetadataAsync(first);
        Assert.Equal((1080, 1920), (stored.Width, stored.Height));
        Assert.Equal((1080, 1920), ((await TestVideos.ProbeAsync(first)).Width, (await TestVideos.ProbeAsync(first)).Height));
        Assert.NotNull(await ObjectSizeAsync(stored.ThumbUrl));
        await AssertSpaceIsWhatIsStoredAsync(album, stored);
        Assert.Equal(VideoTrimStatus.Waiting, (await JobAsync(id))!.Status);
        // The holding copy has gone.
        await Assert.ThrowsAsync<AmazonS3Exception>(() => stack.S3.GetObjectMetadataAsync(
            stack.Holding, $"incoming/{album.Bucket:N}/{id:N}"));

        Assert.True(await TrimAsync());

        var trimmed = await PhotoAsync(id);
        var job = (await JobAsync(id))!;
        Assert.Equal(VideoTrimStatus.Trimmed, job.Status);
        Assert.NotEqual(stored.Url, trimmed.Url);
        Assert.Equal(trimmed.Url, trimmed.OriginalUrl);
        var made = await FetchAsync(trimmed.Url, "made");
        Assert.True(new FileInfo(made).Length < size * 0.9);
        await TestVideos.AssertNoCameraMetadataAsync(made);
        var probe = await TestVideos.ProbeAsync(made);
        Assert.Equal("h264", probe.Codec);
        Assert.True(probe.HasAudio);
        Assert.Equal(6, probe.Seconds, 0.5);
        Assert.Equal((1080, 1920), (probe.Width, probe.Height));
        Assert.Equal((1080, 1920), (trimmed.Width, trimmed.Height));
        await AssertSpaceIsWhatIsStoredAsync(album, trimmed);

        // The old file waits for anyone playing it, then goes through the removal queue.
        var removal = await stack.ReadAsync(db => db.PendingRemovals.AsNoTracking().FirstAsync(r => r.Target == KeyOf(stored.Url)));
        Assert.True(removal.NextAttemptAt > DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.NotNull(await ObjectSizeAsync(stored.Url));
        await stack.WriteAsync(async db => (await db.PendingRemovals.FirstAsync(r => r.Id == removal.Id)).NextAttemptAt = DateTimeOffset.UtcNow);
        await stack.Services.GetRequiredService<PendingRemovalService>().RunOnceAsync(CancellationToken.None);
        Assert.Null(await ObjectSizeAsync(stored.Url));
    }

    // ----- the checks that still run on what arrived -----

    [VideoStackFact]
    public async Task A_file_labelled_video_that_is_not_one_is_refused_and_its_space_returned()
    {
        var album = await AlbumAsync();
        var file = Path.Combine(stack.Files, "fake.mp4");
        await File.WriteAllTextAsync(file, "#EXTM3U\n#EXTINF:1,\nfile:///etc/passwd\n");

        var id = await UploadAsync(album, file);

        var row = await stack.ReadAsync(db => db.PendingUploads.AsNoTracking().FirstAsync(u => u.Id == id));
        Assert.Equal(PendingUploadStatus.Failed, row.Status);
        Assert.Contains("isn't a video", row.Error);
        Assert.Equal(0, await UsedAsync(album.Bucket));
        Assert.False(await stack.ReadAsync(db => db.EventPhotos.AnyAsync(p => p.Id == id)));
    }

    [VideoStackFact]
    public async Task A_truncated_body_is_refused_as_not_arriving_whole()
    {
        var album = await AlbumAsync();
        var file = await MediumAsync();
        var ticket = await DataAsync(await album.Client.PostAsJsonAsync($"/api/campaigns/{album.Campaign}/photos/uploads",
            new { fileName = "a.mp4", contentType = "video/mp4", size = new FileInfo(file).Length + 1 }));
        var id = ticket.GetProperty("id").GetGuid();
        await stack.S3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest { BucketName = stack.Holding, Key = $"incoming/{album.Bucket:N}/{id:N}", FilePath = file });

        var done = await DataAsync(await album.Client.PostAsync($"/api/uploads/{id}/complete", null));

        Assert.Equal("failed", done.GetProperty("status").GetString());
        Assert.Equal(0, await UsedAsync(album.Bucket));
    }

    [VideoStackFact]
    public async Task An_album_with_no_room_gives_no_link()
    {
        var album = await AlbumAsync();

        var r = await album.Client.PostAsJsonAsync($"/api/campaigns/{album.Campaign}/photos/uploads",
            new { fileName = "a.mp4", contentType = "video/mp4", size = 200L * 1024 * 1024 });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());   // the free plan's 1 GB has room

        await stack.WriteAsync(async db => await db.MediaBuckets.Where(b => b.Id == album.Bucket)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.UsedBytes, 1024L * 1024 * 1024)));
        var full = await album.Client.PostAsJsonAsync($"/api/campaigns/{album.Campaign}/photos/uploads",
            new { fileName = "a.mp4", contentType = "video/mp4", size = 1024L });
        Assert.Equal(HttpStatusCode.BadRequest, full.StatusCode);
    }

    [VideoStackFact]
    public async Task A_client_still_is_used_and_a_missing_one_is_drawn_from_the_video()
    {
        var album = await AlbumAsync();
        var file = await MediumAsync();
        var drawn = await PhotoAsync(await UploadAsync(album, file));
        var drawnTile = await FetchAsync(drawn.ThumbUrl, "tile");
        Assert.Equal((byte)0xFF, File.ReadAllBytes(drawnTile)[0]);

        // A flat colour, so it can only have come from the client and not from the noise in the video.
        var still = Path.Combine(stack.Files, "still.jpg");
        await TestVideos.RunAsync("ffmpeg", $"-nostdin -y -loglevel error -f lavfi -i color=c=red:s=640x360 -frames:v 1 {still}");
        var mine = await PhotoAsync(await UploadAsync(album, file, poster: still));
        var mineTile = await FetchAsync(mine.ThumbUrl, "tile");
        Assert.True(new FileInfo(mineTile).Length < new FileInfo(drawnTile).Length / 3,
            $"client still {new FileInfo(mineTile).Length}, drawn {new FileInfo(drawnTile).Length}");
    }

    [VideoStackFact]
    public async Task A_webm_keeps_its_own_container_and_type_when_stored()
    {
        var album = await AlbumAsync();
        var webm = Path.Combine(stack.Files, "clip.webm");
        await TestVideos.RunAsync("ffmpeg", $"-nostdin -y -loglevel error -f lavfi -i testsrc2=s=320x240:r=15 -t 2 -c:v libvpx -b:v 300k -metadata title={TestVideos.SecretPhone} {webm}");

        var photo = await PhotoAsync(await UploadAsync(album, webm, "video/webm"));

        Assert.Equal("video/webm", photo.ContentType);
        Assert.EndsWith(".webm", photo.Url);
        var stored = await FetchAsync(photo.Url, "webm");
        await TestVideos.AssertNoCameraMetadataAsync(stored);
        Assert.True(await TrimAsync());
        Assert.Equal(VideoTrimStatus.Kept, (await JobAsync(photo.Id))!.Status);
        Assert.Equal("already small", (await JobAsync(photo.Id))!.Note);
    }

    // ----- what the trim must never do -----

    [VideoStackFact]
    public async Task When_ffmpeg_fails_the_original_stays_and_the_space_is_unchanged()
    {
        var album = await AlbumAsync();
        var id = await UploadAsync(album, await MediumAsync());
        var before = await PhotoAsync(id);
        stack.Tools.Compress = (_, _, _, _) => Task.FromResult(VideoCompressOutcome.Failed);

        Assert.True(await TrimAsync());

        var after = await PhotoAsync(id);
        Assert.Equal(before.Url, after.Url);
        Assert.Equal(before.SizeBytes, after.SizeBytes);
        var job = (await JobAsync(id))!;
        Assert.Equal(VideoTrimStatus.Kept, job.Status);
        Assert.Equal("ffmpeg failed", job.Note);
        await AssertSpaceIsWhatIsStoredAsync(album, after);
    }

    [VideoStackFact]
    public async Task A_copy_that_is_not_smaller_is_thrown_away()
    {
        var album = await AlbumAsync();
        var file = await MediumAsync();
        var id = await UploadAsync(album, file);
        var before = await PhotoAsync(id);
        // Larger than what is stored, and a perfectly good video.
        stack.Tools.Compress = (input, output, _, _) => { File.Copy(input, output); using var s = File.OpenWrite(output); s.Seek(0, SeekOrigin.End); s.Write(new byte[1024]); return Task.FromResult(VideoCompressOutcome.Done); };

        Assert.True(await TrimAsync());

        Assert.Equal(before.Url, (await PhotoAsync(id)).Url);
        Assert.Equal("not smaller", (await JobAsync(id))!.Note);
        await AssertSpaceIsWhatIsStoredAsync(album, await PhotoAsync(id));
    }

    [VideoStackFact]
    public async Task A_smaller_file_that_is_not_the_same_video_is_thrown_away()
    {
        var album = await AlbumAsync();
        var id = await UploadAsync(album, await MediumAsync());
        var before = await PhotoAsync(id);
        // Smaller, valid H.264, but only a second of the clip.
        stack.Tools.Compress = async (input, output, _, _) =>
        {
            await TestVideos.RunAsync("ffmpeg", $"-nostdin -y -loglevel error -i {input} -t 1 -c:v libx264 -preset ultrafast -c:a aac -f mp4 {output}");
            return VideoCompressOutcome.Done;
        };

        Assert.True(await TrimAsync());

        Assert.Equal(before.Url, (await PhotoAsync(id)).Url);
        Assert.Equal("copy didn't match", (await JobAsync(id))!.Note);
    }

    [VideoStackFact]
    public async Task The_time_limit_keeps_the_original()
    {
        var album = await AlbumAsync();
        var id = await UploadAsync(album, await MediumAsync());
        var before = await PhotoAsync(id);
        stack.Tools.Compress = (_, _, _, _) => Task.FromResult(VideoCompressOutcome.TimedOut);

        Assert.True(await TrimAsync());

        Assert.Equal(before.Url, (await PhotoAsync(id)).Url);
        Assert.Equal("time limit", (await JobAsync(id))!.Note);
    }

    [VideoStackFact]
    public async Task With_no_disk_room_it_waits_without_spending_an_attempt()
    {
        var album = await AlbumAsync();
        var id = await UploadAsync(album, await MediumAsync());
        stack.Tools.Room = _ => false;

        Assert.True(await TrimAsync());

        var job = (await JobAsync(id))!;
        Assert.Equal(VideoTrimStatus.Waiting, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.True(job.NextAttemptAt > DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(0, stack.Tools.Compressions);
    }

    // ----- restarts -----

    [VideoStackFact]
    public async Task A_stop_mid_job_resumes_on_restart_and_finishes_right()
    {
        var album = await AlbumAsync();
        var id = await UploadAsync(album, await MediumAsync());
        var before = await PhotoAsync(id);
        var service = stack.Services.GetRequiredService<VideoTrimService>();

        using var stop = new CancellationTokenSource();
        stack.Tools.BeforeCompress = () => { stop.Cancel(); return Task.CompletedTask; };
        stack.Tools.Compress = (_, _, _, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(VideoCompressOutcome.Done); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunOnceAsync(stop.Token));

        var stopped = (await JobAsync(id))!;
        Assert.Equal(VideoTrimStatus.Waiting, stopped.Status);
        Assert.Equal(0, stopped.Attempts);
        Assert.Equal(before.Url, (await PhotoAsync(id)).Url);

        stack.Tools.Reset();
        Assert.True(await TrimAsync());
        Assert.Equal(VideoTrimStatus.Trimmed, (await JobAsync(id))!.Status);
        await AssertSpaceIsWhatIsStoredAsync(album, await PhotoAsync(id));
    }

    [VideoStackFact]
    public async Task A_crash_mid_job_leaves_it_running_and_startup_puts_it_back()
    {
        var album = await AlbumAsync();
        var id = await UploadAsync(album, await MediumAsync());
        await stack.WriteAsync(async db =>
        {
            var job = await db.VideoTrimJobs.FirstAsync(j => j.Id == id);
            job.Status = VideoTrimStatus.Running;
            job.Attempts = 1;
        });
        var leftover = stack.Tools.NewWorkDir();
        await File.WriteAllTextAsync(Path.Combine(leftover, "in"), "half a video");
        // Made by a process before this one; a folder from this process may be an upload at work.
        var before = DateTime.UtcNow.AddDays(-1);
        Directory.SetCreationTimeUtc(leftover, before);
        Directory.SetLastWriteTimeUtc(leftover, before);
        var current = stack.Tools.NewWorkDir();

        await stack.Services.GetRequiredService<VideoTrimService>().ReleaseInterruptedAsync(CancellationToken.None);

        var job = (await JobAsync(id))!;
        Assert.Equal(VideoTrimStatus.Waiting, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.False(Directory.Exists(leftover), "the interrupted run's files should have gone");
        Assert.True(Directory.Exists(current), "a folder this process is using must stay");
        Directory.Delete(current);
        Assert.True(await TrimAsync());
        Assert.Equal(VideoTrimStatus.Trimmed, (await JobAsync(id))!.Status);
    }

    [VideoStackFact]
    public async Task A_file_that_keeps_killing_the_process_is_given_up_on()
    {
        var album = await AlbumAsync();
        var id = await UploadAsync(album, await MediumAsync());
        var before = await PhotoAsync(id);
        await stack.WriteAsync(async db => (await db.VideoTrimJobs.FirstAsync(j => j.Id == id)).Attempts = VideoTrimService.MaxAttempts);

        Assert.True(await TrimAsync());

        Assert.Equal(VideoTrimStatus.Kept, (await JobAsync(id))!.Status);
        Assert.Equal(0, stack.Tools.Compressions);
        Assert.Equal(before.Url, (await PhotoAsync(id)).Url);
    }

    // ----- deleted while it runs -----

    [VideoStackFact]
    public async Task A_photo_deleted_mid_job_is_not_brought_back_and_the_new_file_goes()
    {
        var album = await AlbumAsync();
        var id = await UploadAsync(album, await MediumAsync());
        var before = await PhotoAsync(id);
        stack.Tools.BeforeCompress = async () =>
        {
            var gone = await album.Client.DeleteAsync($"/api/campaigns/{album.Campaign}/photos/{id}");
            Assert.True(gone.IsSuccessStatusCode, await gone.Content.ReadAsStringAsync());
        };

        Assert.True(await TrimAsync());

        var after = await PhotoAsync(id);
        Assert.NotNull(after.DeletedAt);
        Assert.Equal(before.Url, after.Url);
        Assert.Equal(VideoTrimStatus.Gone, (await JobAsync(id))!.Status);
        Assert.Equal(0, await UsedAsync(album.Bucket));
        var left = await stack.S3.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = stack.Bucket, Prefix = $"buckets/{album.Bucket:N}/" });
        Assert.Empty(left.S3Objects ?? []);
    }

    [VideoStackFact]
    public async Task A_photo_removed_outright_mid_job_as_an_account_deletion_does_is_safe()
    {
        var album = await AlbumAsync();
        var id = await UploadAsync(album, await MediumAsync());
        stack.Tools.BeforeCompress = async () => await stack.WriteAsync(async db =>
        {
            await db.EventPhotos.Where(p => p.Id == id).ExecuteDeleteAsync();
        });

        Assert.True(await TrimAsync());

        Assert.Null(await JobAsync(id));   // gone with the photo
        var left = await stack.S3.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = stack.Bucket, Prefix = $"buckets/{album.Bucket:N}/media/{id:N}_v" });
        Assert.Empty(left.S3Objects ?? []);
    }

    [VideoStackFact]
    public async Task A_delete_that_read_the_photo_before_the_swap_credits_the_new_size_and_removes_the_new_file()
    {
        var album = await AlbumAsync();
        var id = await UploadAsync(album, await MediumAsync());

        // The photo as the delete reads it, then the whole trim, then the delete's save.
        using var scope = stack.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stale = await db.EventPhotos.FirstAsync(p => p.Id == id);
        var staleUrl = stale.Url;

        Assert.True(await TrimAsync());
        var trimmed = await PhotoAsync(id);
        Assert.NotEqual(staleUrl, trimmed.Url);

        stale.DeletedAt = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());

        // And through the service, whose retry sees what is there now.
        var gone = await album.Client.DeleteAsync($"/api/campaigns/{album.Campaign}/photos/{id}");
        Assert.True(gone.IsSuccessStatusCode);
        Assert.Equal(0, await UsedAsync(album.Bucket));
        Assert.Null(await ObjectSizeAsync(trimmed.Url));
    }

    // ----- the old route -----

    [VideoStackFact]
    public async Task The_old_route_still_works_and_its_video_gets_cleaned_by_the_job()
    {
        var album = await AlbumAsync();
        var file = await MediumAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(file)) { Headers = { ContentType = new("video/mp4") } }, "file", "clip.mp4");

        var added = await album.Client.PostAsync($"/api/campaigns/{album.Campaign}/photos", form);   // no still: the server draws one

        var id = (await DataAsync(added))[0].GetProperty("id").GetGuid();
        var before = await PhotoAsync(id);
        Assert.False((await JobAsync(id))!.MetadataClean);
        await AssertSpaceIsWhatIsStoredAsync(album, before);

        stack.Tools.Compress = (_, _, _, _) => Task.FromResult(VideoCompressOutcome.Failed);
        Assert.True(await TrimAsync());

        // Not compressed, but the job still took the location out, and the old file goes at once.
        var after = await PhotoAsync(id);
        Assert.NotEqual(before.Url, after.Url);
        await TestVideos.AssertNoCameraMetadataAsync(await FetchAsync(after.Url, "cleaned"));
        Assert.Equal(VideoTrimStatus.Kept, (await JobAsync(id))!.Status);
        var removal = await stack.ReadAsync(db => db.PendingRemovals.AsNoTracking().FirstAsync(r => r.Target == KeyOf(before.Url)));
        Assert.True(removal.NextAttemptAt <= DateTimeOffset.UtcNow);
        await AssertSpaceIsWhatIsStoredAsync(album, after);
    }

    [VideoStackFact]
    public async Task With_a_failed_strip_the_video_is_still_stored_and_the_job_tries_again()
    {
        var album = await AlbumAsync();
        stack.Tools.StripFails = () => true;
        var id = await UploadAsync(album, await MediumAsync());
        var job = (await JobAsync(id))!;
        Assert.False(job.MetadataClean);

        stack.Tools.Reset();
        Assert.True(await TrimAsync());
        await TestVideos.AssertNoCameraMetadataAsync(await FetchAsync((await PhotoAsync(id)).Url, "second-go"));
    }
}

/// <summary>Real ffmpeg on real files, with no database: what the commands themselves do.</summary>
public sealed class FfmpegToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ib-ff").FullName;
    private FfmpegVideoTools Tools(params (string, string?)[] settings) => new(
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(settings.Select(s => KeyValuePair.Create(s.Item1, s.Item2)))
            {
                ["Video:Preset"] = "ultrafast",
                ["Video:WorkDir"] = Path.Combine(_dir, "work"),
            }).Build(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegVideoTools>.Instance);
    private static readonly Application.Common.VideoKind Mp4 = new("video/mp4", ".mp4", "mov", "mp4");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    [FfmpegFact]
    public async Task Stripping_removes_the_location_and_keeps_rotation_and_every_frame()
    {
        await TestVideos.MakeAsync(P("a.mp4"), 640, 360, 2, 15);
        Assert.True((await File.ReadAllBytesAsync(P("a.mp4"))).AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(TestVideos.SecretPlace)) >= 0, "the test video has no location to remove");

        Assert.True(await Tools().StripMetadataAsync(P("a.mp4"), P("b.mp4"), Mp4));

        await TestVideos.AssertNoCameraMetadataAsync(P("b.mp4"));
        var (was, now) = (await TestVideos.ProbeAsync(P("a.mp4")), await TestVideos.ProbeAsync(P("b.mp4")));
        Assert.Equal((360, 640), (now.Width, now.Height));
        Assert.Equal(was.Seconds, now.Seconds, 0.2);
        Assert.Equal(was.Codec, now.Codec);
        Assert.True(now.HasAudio);
    }

    [FfmpegFact]
    public async Task Compressing_turns_it_upright_caps_the_edge_and_puts_the_index_first()
    {
        await TestVideos.MakeAsync(P("a.mp4"), 2560, 1440, 1, 10);

        Assert.Equal(VideoCompressOutcome.Done, await Tools().CompressAsync(P("a.mp4"), P("b.mp4"), Mp4));

        await TestVideos.AssertNoCameraMetadataAsync(P("b.mp4"));
        var made = await TestVideos.ProbeAsync(P("b.mp4"));
        Assert.Equal("h264", made.Codec);
        Assert.Equal((1080, 1920), (made.Width, made.Height));
        var head = (await File.ReadAllBytesAsync(P("b.mp4"))).AsSpan(0, 4096);
        Assert.True(head.IndexOf("moov"u8) >= 0, "+faststart should put moov at the front");
    }

    [FfmpegFact]
    public async Task A_small_video_is_not_made_larger()
    {
        await TestVideos.MakeAsync(P("a.mp4"), 320, 240, 1, 10, rotate: 0);

        Assert.Equal(VideoCompressOutcome.Done, await Tools().CompressAsync(P("a.mp4"), P("b.mp4"), Mp4));

        var made = await TestVideos.ProbeAsync(P("b.mp4"));
        Assert.Equal((320, 240), (made.Width, made.Height));
    }

    [FfmpegFact]
    public async Task A_broken_file_fails_rather_than_hanging_and_a_probe_says_so()
    {
        await File.WriteAllBytesAsync(P("bad.mp4"), [0, 0, 0, 0x20, .. "ftypisom"u8, .. new byte[9000]]);
        var tools = Tools();

        Assert.Null(await tools.ProbeAsync(P("bad.mp4"), Mp4));
        Assert.Equal(VideoCompressOutcome.Failed, await tools.CompressAsync(P("bad.mp4"), P("out.mp4"), Mp4));
        Assert.False(await tools.StripMetadataAsync(P("bad.mp4"), P("out2.mp4"), Mp4));
        Assert.Null(await tools.PosterAsync(P("bad.mp4"), Mp4));
    }

    [FfmpegFact]
    public async Task The_time_limit_stops_the_encoder()
    {
        await TestVideos.MakeAsync(P("a.mp4"), 1280, 720, 6, 30, "-crf 10");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await Tools(("Video:TimeLimitMinutes", "0.01"), ("Video:Preset", "veryslow")).CompressAsync(P("a.mp4"), P("b.mp4"), Mp4);

        Assert.Equal(VideoCompressOutcome.TimedOut, outcome);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    [FfmpegFact]
    public async Task A_stop_kills_the_encoder_and_throws()
    {
        await TestVideos.MakeAsync(P("a.mp4"), 1280, 720, 6, 30, "-crf 10");
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tools(("Video:Preset", "veryslow")).CompressAsync(P("a.mp4"), P("b.mp4"), Mp4, stop.Token));
    }

    [FfmpegFact]
    public async Task A_truncated_copy_does_not_count_as_playing()
    {
        await TestVideos.MakeAsync(P("a.mp4"), 640, 360, 10, 15, rotate: 0);
        var tools = Tools();
        Assert.True(await tools.PlaysAsync(P("a.mp4"), Mp4, 10));

        var bytes = await File.ReadAllBytesAsync(P("a.mp4"));
        await File.WriteAllBytesAsync(P("cut.mp4"), bytes[..(bytes.Length / 2)]);

        Assert.False(await tools.PlaysAsync(P("cut.mp4"), Mp4, 10));
    }

    [FfmpegFact]
    public async Task The_encoder_runs_without_the_apis_secrets_in_its_environment()
    {
        var script = P("fake-ffmpeg");
        await File.WriteAllTextAsync(script, $"#!/bin/sh\nenv > {P("env.txt")}\nexit 0\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("IB_TEST_SECRET_VALUE", "do-not-leak");
        try
        {
            await Tools(("Video:Ffmpeg", script)).CompressAsync(P("in"), P("out"), Mp4);
        }
        finally
        {
            Environment.SetEnvironmentVariable("IB_TEST_SECRET_VALUE", null);
        }
        Assert.DoesNotContain("do-not-leak", await File.ReadAllTextAsync(P("env.txt")));
    }

    [FfmpegFact]
    public async Task The_commands_refuse_to_follow_anything_but_a_local_file()
    {
        // A real HLS playlist pointing at a local file: named as MP4, ffmpeg must not read it as a playlist.
        await File.WriteAllTextAsync(P("x.mp4"), $"#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:1.0,\nfile://{P("a.mp4")}\n#EXT-X-ENDLIST\n");
        await TestVideos.MakeAsync(P("a.mp4"), 320, 240, 1, 10, rotate: 0);

        Assert.Null(await Tools().ProbeAsync(P("x.mp4"), Mp4));
    }
}

public sealed class FfmpegFactAttribute : FactAttribute
{
    private static readonly bool Present = Check();
    public FfmpegFactAttribute()
    {
        if (!Present) Skip = "ffmpeg isn't installed here.";
    }
    private static bool Check()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
