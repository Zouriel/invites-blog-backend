using Amazon.Runtime;
using Amazon.S3;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Photos;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Application.Services.Photos;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class DirectUploadServiceTests
{
    private readonly IRepository<PendingUpload> _uploads = Substitute.For<IRepository<PendingUpload>>();
    private readonly IRepository<EventPhoto> _photos = Substitute.For<IRepository<EventPhoto>>();
    private readonly IIncomingUploadStore _incoming = Substitute.For<IIncomingUploadStore>();
    private readonly IEventPhotoService _photoService = Substitute.For<IEventPhotoService>();
    private readonly IMediaBucketService _buckets = Substitute.For<IMediaBucketService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly List<PendingUpload> _rows = [];
    private readonly List<EventPhoto> _photoRows = [];
    private readonly Guid _bucket = Guid.NewGuid();
    private string _switch = "on";

    public DirectUploadServiceTests()
    {
        _incoming.Available.Returns(true);
        _incoming.PresignPut(default!, default!, default, default).ReturnsForAnyArgs("https://r2.test/put");
        _uploads.Query(Arg.Any<bool>()).Returns(_ => _rows.AsAsyncQueryable());
        _photos.Query(Arg.Any<bool>()).Returns(_ => _photoRows.AsAsyncQueryable());
        _uploads.AddAsync(Arg.Do<PendingUpload>(_rows.Add), Arg.Any<CancellationToken>());
    }

    private DirectUploadService Sut() => new(
        _uploads, _photos, _incoming, _photoService, _buckets, _uow,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Uploads:Direct"] = _switch }).Build());

    private UploadDoor Door => new(_bucket, Guid.NewGuid(), null, "Mariyam");

    private PendingUpload Row(PendingUploadStatus status, long bytes = 5, DateTimeOffset? expires = null)
    {
        var row = new PendingUpload
        {
            Id = Guid.NewGuid(), BucketId = _bucket, IncomingKey = "incoming/x", ContentType = "image/jpeg",
            FileName = "a.jpg", DeclaredBytes = bytes, Status = status,
            ExpiresAt = expires ?? DateTimeOffset.UtcNow.AddMinutes(10),
        };
        _rows.Add(row);
        return row;
    }

    private async Task ShouldHaveGivenSpaceBackOnce(long bytes)
    {
        await _buckets.Received(1).CountUsageAsync(_bucket, -bytes, Arg.Any<CancellationToken>());
    }

    // ----- asking for a link -----

    [Fact]
    public async Task With_the_switch_off_every_file_takes_the_old_path()
    {
        _switch = "off";

        var ticket = await Sut().IssueAsync(Door, new DirectUploadRequest("a.jpg", "image/jpeg", 5));

        Assert.False(ticket.Direct);
        await _buckets.DidNotReceiveWithAnyArgs().ReserveRoomAsync(default, default, default);
    }

    [Fact]
    public async Task With_no_holding_area_every_file_takes_the_old_path()
    {
        _incoming.Available.Returns(false);

        Assert.False((await Sut().IssueAsync(Door, new DirectUploadRequest("a.jpg", "image/jpeg", 5))).Direct);
    }

    [Fact]
    public async Task The_switch_can_name_single_albums()
    {
        var sut = new DirectUploadService(
            _uploads, _photos, _incoming, _photoService, _buckets, _uow,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Uploads:DirectBuckets"] = $"{Guid.NewGuid()}, {_bucket}",
            }).Build());

        Assert.True((await sut.IssueAsync(Door, new DirectUploadRequest("a.jpg", "image/jpeg", 5))).Direct);
        Assert.False((await sut.IssueAsync(Door with { BucketId = Guid.NewGuid() }, new DirectUploadRequest("a.jpg", "image/jpeg", 5))).Direct);
    }

    [Theory]
    [InlineData("video/mp4", 5)]
    [InlineData("image/svg+xml", 5)]
    [InlineData("", 5)]
    [InlineData("text/html", 5)]
    [InlineData("image/jpeg\r\nx-evil: 1", 5)]
    [InlineData("image/jpeg", 0)]
    [InlineData("image/jpeg", DirectUploadService.MaxDirectBytes + 1)]
    public async Task Anything_but_an_ordinary_photo_takes_the_old_path(string type, long size)
    {
        var ticket = await Sut().IssueAsync(Door, new DirectUploadRequest("a", type, size));

        Assert.False(ticket.Direct);
        Assert.Empty(_rows);
        await _buckets.DidNotReceiveWithAnyArgs().ReserveRoomAsync(default, default, default);
    }

    [Fact]
    public async Task A_link_reserves_the_space_and_signs_exactly_what_was_declared()
    {
        var ticket = await Sut().IssueAsync(Door, new DirectUploadRequest("../../a.jpg", "IMAGE/JPEG", 1234));

        Assert.True(ticket.Direct);
        var row = Assert.Single(_rows);
        Assert.Equal(ticket.Id, row.Id);
        Assert.Equal(PendingUploadStatus.Issued, row.Status);
        Assert.Equal("a.jpg", row.FileName);
        Assert.StartsWith($"incoming/{_bucket:N}/", row.IncomingKey);
        await _buckets.Received(1).ReserveRoomAsync(_bucket, 1234, Arg.Any<CancellationToken>());
        _incoming.Received(1).PresignPut(row.IncomingKey, "image/jpeg", 1234, DirectUploadService.LinkLifetime);
    }

    [Fact]
    public async Task No_link_for_a_full_or_closed_album()
    {
        _buckets.ReserveRoomAsync(_bucket, 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new BusinessRuleException("This album is full.", "full"));

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().IssueAsync(Door, new DirectUploadRequest("a.jpg", "image/jpeg", 5)));
        Assert.Empty(_rows);

        _buckets.EnsureOpenAsync(_bucket, Arg.Any<CancellationToken>())
            .ThrowsAsync(new BusinessRuleException("Closed.", "closed"));
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().IssueAsync(Door, new DirectUploadRequest("a.jpg", "image/jpeg", 5)));
    }

    [Fact]
    public async Task The_space_goes_back_if_the_link_cannot_be_made()
    {
        _incoming.PresignPut(default!, default!, default, default).ThrowsForAnyArgs(new InvalidOperationException());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut().IssueAsync(Door, new DirectUploadRequest("a.jpg", "image/jpeg", 5)));

        await ShouldHaveGivenSpaceBackOnce(5);
    }

    // ----- saying it has arrived -----

    [Fact]
    public async Task Complete_before_the_bytes_arrive_changes_nothing()
    {
        var row = Row(PendingUploadStatus.Issued);
        _incoming.SizeAsync(row.IncomingKey, Arg.Any<CancellationToken>()).Returns((long?)null);

        Assert.Equal("issued", (await Sut().CompleteAsync(row.Id)).Status);
    }

    [Fact]
    public async Task Complete_hands_the_right_bytes_to_the_processor_and_can_be_repeated()
    {
        var row = Row(PendingUploadStatus.Issued);
        _incoming.SizeAsync(row.IncomingKey, Arg.Any<CancellationToken>()).Returns(5);

        Assert.Equal("processing", (await Sut().CompleteAsync(row.Id)).Status);
        Assert.Equal(PendingUploadStatus.Uploaded, row.Status);
        Assert.Equal("processing", (await Sut().CompleteAsync(row.Id)).Status);
        await _incoming.Received(1).SizeAsync(row.IncomingKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_file_of_another_size_is_refused_deleted_and_its_space_returned()
    {
        var row = Row(PendingUploadStatus.Issued);
        _incoming.SizeAsync(row.IncomingKey, Arg.Any<CancellationToken>()).Returns(999);

        Assert.Equal("failed", (await Sut().CompleteAsync(row.Id)).Status);

        await ShouldHaveGivenSpaceBackOnce(5);
        await _incoming.Received(1).DeleteAsync(row.IncomingKey, Arg.Any<CancellationToken>());
    }

    // ----- the work afterwards -----

    private void Arrived(PendingUpload row) =>
        _incoming.GetAsync(row.IncomingKey, Arg.Any<CancellationToken>()).Returns(new byte[row.DeclaredBytes]);

    [Fact]
    public async Task A_good_file_becomes_a_photo_with_the_uploads_id_and_the_incoming_copy_goes()
    {
        var row = Row(PendingUploadStatus.Processing);
        row.QrId = Guid.NewGuid();
        Arrived(row);

        await Sut().FinishAsync(row.Id);

        await _photoService.Received(1).StoreUploadedAsync(
            row.Id, Arg.Is<UploadDoor>(d => d.BucketId == _bucket), Arg.Any<byte[]>(), "image/jpeg", "a.jpg", Arg.Any<CancellationToken>());
        Assert.Equal(PendingUploadStatus.Done, row.Status);
        await _incoming.Received(1).DeleteAsync(row.IncomingKey, Arg.Any<CancellationToken>());
        await _buckets.Received(1).CountContributionAsync(row.QrId.Value, Arg.Any<CancellationToken>());
        await _buckets.DidNotReceiveWithAnyArgs().CountUsageAsync(default, default, default);
    }

    [Fact]
    public async Task A_file_the_rules_refuse_fails_at_once_with_the_reason()
    {
        var row = Row(PendingUploadStatus.Processing);
        Arrived(row);
        _photoService.StoreUploadedAsync(default, default!, default!, default!, default!, default)
            .ThrowsForAnyArgs(new BusinessRuleException("That isn't a photo.", "bad"));

        await Sut().FinishAsync(row.Id);

        Assert.Equal(PendingUploadStatus.Failed, row.Status);
        Assert.Equal("That isn't a photo.", row.Error);
        Assert.Equal(0, row.Attempts);
        await ShouldHaveGivenSpaceBackOnce(5);
        await _incoming.Received(1).DeleteAsync(row.IncomingKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_storage_hiccup_is_retried_and_then_given_up_on_once()
    {
        var row = Row(PendingUploadStatus.Processing);
        Arrived(row);
        _photoService.StoreUploadedAsync(default, default!, default!, default!, default!, default)
            .ThrowsForAnyArgs(new IOException("R2 is down"));

        await Sut().FinishAsync(row.Id);
        Assert.Equal(PendingUploadStatus.Uploaded, row.Status);
        Assert.True(row.NextAttemptAt > DateTimeOffset.UtcNow);
        await _buckets.DidNotReceiveWithAnyArgs().CountUsageAsync(default, default, default);

        for (var i = 1; i < DirectUploadService.MaxAttempts; i++)
        {
            row.Status = PendingUploadStatus.Processing;
            await Sut().FinishAsync(row.Id);
        }

        Assert.Equal(PendingUploadStatus.Failed, row.Status);
        await ShouldHaveGivenSpaceBackOnce(5);
    }

    [Fact]
    public async Task A_restart_after_the_photo_was_saved_does_not_make_a_second()
    {
        var row = Row(PendingUploadStatus.Processing);
        _photoRows.Add(new EventPhoto { Id = row.Id });

        await Sut().FinishAsync(row.Id);

        await _photoService.DidNotReceiveWithAnyArgs().StoreUploadedAsync(default, default!, default!, default!, default!, default);
        Assert.Equal(PendingUploadStatus.Done, row.Status);
    }

    [Fact]
    public async Task Finishing_only_touches_a_claimed_upload()
    {
        var row = Row(PendingUploadStatus.Failed);

        await Sut().FinishAsync(row.Id);

        Assert.Equal(PendingUploadStatus.Failed, row.Status);
        await _incoming.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
    }

    // ----- the sweep -----

    [Fact]
    public async Task A_lapsed_link_whose_bytes_arrived_is_not_lost()
    {
        var row = Row(PendingUploadStatus.Issued, expires: DateTimeOffset.UtcNow.AddMinutes(-1));
        _incoming.SizeAsync(row.IncomingKey, Arg.Any<CancellationToken>()).Returns(5);

        await Sut().SweepAsync();

        Assert.Equal(PendingUploadStatus.Uploaded, row.Status);
    }

    [Fact]
    public async Task A_lapsed_link_with_nothing_there_gives_its_space_back()
    {
        var row = Row(PendingUploadStatus.Issued, expires: DateTimeOffset.UtcNow.AddMinutes(-1));
        var live = Row(PendingUploadStatus.Issued);

        await Sut().SweepAsync();

        Assert.Equal(PendingUploadStatus.Expired, row.Status);
        Assert.Equal(PendingUploadStatus.Issued, live.Status);
        await ShouldHaveGivenSpaceBackOnce(5);
    }

    // ----- the link itself -----

    [Fact]
    public void The_link_signs_size_and_type_and_stays_in_the_holding_bucket()
    {
        using var client = new AmazonS3Client(new BasicAWSCredentials("k", "s"), new AmazonS3Config
        {
            ServiceURL = "https://acct.r2.cloudflarestorage.com", ForcePathStyle = true, AuthenticationRegion = "auto",
        });
        var store = new S3IncomingUploadStore(client, "holding", "https://acct.r2.cloudflarestorage.com");

        var url = store.PresignPut("incoming/x", "image/jpeg", 1234, TimeSpan.FromMinutes(15));

        Assert.Contains("/holding/incoming/x", url);
        Assert.Contains("content-length", url);
        Assert.Contains("content-type", url);
        Assert.Equal("https://acct.r2.cloudflarestorage.com", store.Origin);
    }
}
