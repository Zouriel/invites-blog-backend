using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.MediaBuckets;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.MediaBuckets;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Security;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Media buckets. Two things here are worth protecting with tests and the rest is plumbing: the
/// QUOTA, because it is the only thing standing between a product we sell by the gigabyte and
/// storage we give away, and the CODES, because a printed QR is the least revocable object in the
/// product and the rules about what one admits are the whole of its security.
/// </summary>
public class MediaBucketServiceTests
{
    private readonly IRepository<MediaBucket> _buckets = Substitute.For<IRepository<MediaBucket>>();
    private readonly IRepository<MediaBucketQr> _qrs = Substitute.For<IRepository<MediaBucketQr>>();
    private readonly IRepository<MediaBucketMember> _members =
        Substitute.For<IRepository<MediaBucketMember>>();
    private readonly IRepository<EventPhoto> _photos = Substitute.For<IRepository<EventPhoto>>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly IInviterRepository _inviters = Substitute.For<IInviterRepository>();
    private readonly IStorageService _storage = Substitute.For<IStorageService>();
    private readonly IQrCodeRenderer _renderer = Substitute.For<IQrCodeRenderer>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    private readonly Guid _me = Guid.NewGuid();

    public MediaBucketServiceTests()
    {
        _currentUser.UserId.Returns(_me);
        _renderer.Png(Arg.Any<string>(), Arg.Any<int>()).Returns([1, 2, 3]);
        _storage.PutAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => $"/assets/{c.ArgAt<string>(0)}");
        _photos.Query().Returns(Array.Empty<EventPhoto>().AsAsyncQueryable());
        _qrs.Query(Arg.Any<bool>()).Returns(Array.Empty<MediaBucketQr>().AsAsyncQueryable());
        _members.Query(Arg.Any<bool>()).Returns(Array.Empty<MediaBucketMember>().AsAsyncQueryable());
    }

    private MediaBucketService Sut() => new(
        _buckets, _qrs, _members, _users, _photos, _campaigns,
        new CampaignOwnershipService(_currentUser, _users, _campaigns, _inviters),
        _currentUser, _storage, _renderer, new PhoneNormalizer(), _config,
        Options.Create(new MediaBucketOptions()), _uow);

    private MediaBucket Mine(long capacityGb = 10, long used = 0) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = _me,
        Title = "The wedding weekend",
        Tier = MediaBucketTier.Gb10,
        CapacityBytes = capacityGb * MediaBucketPlans.BytesPerGb,
        UsedBytes = used,
        // Tonight, so the bucket is inside its window — these tests are about the quota and the
        // codes, and a closed bucket would answer every one of them "no" for the wrong reason.
        EventDate = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private void Stored(MediaBucket bucket)
    {
        _buckets.GetByIdAsync(bucket.Id, Arg.Any<CancellationToken>()).Returns(bucket);
        _buckets.Query(Arg.Any<bool>()).Returns(new[] { bucket }.AsAsyncQueryable());
    }

    // ---------- the quota ----------

    [Fact]
    public async Task An_upload_that_fits_is_allowed()
    {
        var bucket = Mine(capacityGb: 10, used: 1 * MediaBucketPlans.BytesPerGb);
        Stored(bucket);

        await Sut().EnsureRoomAsync(bucket.Id, 5 * MediaBucketPlans.BytesPerGb);
    }

    [Fact]
    public async Task An_upload_that_would_overflow_the_bucket_is_refused()
    {
        var bucket = Mine(capacityGb: 10, used: 9 * MediaBucketPlans.BytesPerGb);
        Stored(bucket);

        var e = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().EnsureRoomAsync(bucket.Id, 2 * MediaBucketPlans.BytesPerGb));
        Assert.Equal("bucket_full", e.ErrorCode);
    }

    /// <summary>
    /// The boundary belongs to the customer. A bucket sold as 10 GB has to accept the upload that
    /// takes it to exactly 10 GB, or it is a 10 GB bucket that holds slightly less than 10 GB.
    /// </summary>
    [Fact]
    public async Task An_upload_that_exactly_fills_the_bucket_is_allowed()
    {
        var bucket = Mine(capacityGb: 10, used: 9 * MediaBucketPlans.BytesPerGb);
        Stored(bucket);

        await Sut().EnsureRoomAsync(bucket.Id, 1 * MediaBucketPlans.BytesPerGb);
    }

    [Fact]
    public async Task A_full_free_bucket_says_what_to_do_about_it()
    {
        var bucket = Mine();
        bucket.Tier = MediaBucketTier.Free;
        bucket.CapacityBytes = 2 * MediaBucketPlans.BytesPerGb;
        bucket.UsedBytes = bucket.CapacityBytes;
        Stored(bucket);

        var e = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().EnsureRoomAsync(bucket.Id, 1024));
        Assert.Contains("Choose a bucket size", e.Message);
    }

    // ---------- tiers ----------

    /// <summary>
    /// Shrinking below what is already stored has no honest outcome — somebody would be over their
    /// limit for photographs they have already been told are kept, and nothing here gets to pick
    /// which of them to stop keeping.
    /// </summary>
    [Fact]
    public async Task A_bucket_cannot_be_resized_below_what_is_already_in_it()
    {
        var bucket = Mine(capacityGb: 50, used: 30 * MediaBucketPlans.BytesPerGb);
        bucket.Tier = MediaBucketTier.Gb50;
        Stored(bucket);

        var e = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().ChooseTierAsync(bucket.Id, new ChooseMediaBucketTierRequest("Gb20")));
        Assert.Equal("tier_below_usage", e.ErrorCode);
    }

    /// <summary>Topping up mid-term must not cost somebody the months they had left.</summary>
    [Fact]
    public async Task Buying_again_extends_the_term_rather_than_restarting_it()
    {
        var bucket = Mine();
        var remaining = DateTimeOffset.UtcNow.AddMonths(5);
        bucket.TermStartAt = DateTimeOffset.UtcNow.AddMonths(-1);
        bucket.TermEndAt = remaining;
        Stored(bucket);

        await Sut().ChooseTierAsync(bucket.Id, new ChooseMediaBucketTierRequest("Gb20"));

        // Six more months on top of the five still outstanding, not six from today.
        Assert.True(bucket.TermEndAt > remaining.AddMonths(5));
        Assert.Equal(20 * MediaBucketPlans.BytesPerGb, bucket.CapacityBytes);
    }

    [Fact]
    public async Task An_unknown_tier_is_refused()
    {
        var bucket = Mine();
        Stored(bucket);

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().ChooseTierAsync(bucket.Id, new ChooseMediaBucketTierRequest("Gb999")));
    }

    [Fact]
    public async Task Somebody_elses_bucket_is_not_readable()
    {
        var bucket = Mine();
        bucket.OwnerUserId = Guid.NewGuid();
        Stored(bucket);

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().GetAsync(bucket.Id));
    }

    /// <summary>
    /// A campaign's box predates buckets, so its photographs carry no bucket id. The bucket
    /// provisioned for it has to take them on, or it under-reports its own contents forever — the
    /// dashboard showing a box of eleven while the bucket page shows none, and a quota measured
    /// against a fraction of what is really stored.
    /// </summary>
    [Fact]
    public async Task Provisioning_a_campaigns_bucket_adopts_the_photos_it_already_had()
    {
        var campaignId = Guid.NewGuid();
        _campaigns.GetByIdAsync(campaignId, Arg.Any<CancellationToken>())
            .Returns(new Campaign { Id = campaignId, Title = "The wedding", EventStartAt = DateTimeOffset.UtcNow });
        _buckets.FirstOrDefaultAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<MediaBucket, bool>>>(),
            Arg.Any<CancellationToken>()).Returns((MediaBucket?)null);

        EventPhoto[] already =
        [
            new() { Id = Guid.NewGuid(), CampaignId = campaignId, SizeBytes = 400 },
            // Soft-deleted still counts: the stored objects outlive the row, which is the same reason
            // UsedBytes never goes down on a delete.
            new() { Id = Guid.NewGuid(), CampaignId = campaignId, SizeBytes = 600, DeletedAt = DateTimeOffset.UtcNow },
        ];
        _photos.Query(Arg.Any<bool>()).Returns(already.AsAsyncQueryable());

        var bucket = await Sut().ForCampaignAsync(campaignId);

        Assert.All(already, p => Assert.Equal(bucket.Id, p.BucketId));
        Assert.Equal(1000, bucket.UsedBytes);
    }

    // ---------- who may look ----------

    /// <summary>
    /// The gap this list closed: before it, a standalone bucket was visible to exactly one account,
    /// so everybody who filled one was locked out of what they had filled.
    /// </summary>
    [Fact]
    public async Task A_bucket_is_not_visible_to_somebody_who_is_not_on_its_list()
    {
        var bucket = Mine();
        bucket.OwnerUserId = Guid.NewGuid();
        Stored(bucket);
        _users.GetByIdAsync(_me, Arg.Any<CancellationToken>())
            .Returns(new AppUser { Id = _me, Email = "stranger@example.com" });

        Assert.False(await Sut().MayViewAsync(bucket.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().ViewAsync(bucket.Id));
    }

    [Fact]
    public async Task A_bucket_is_visible_to_a_contact_the_owner_added()
    {
        var bucket = Mine();
        bucket.OwnerUserId = Guid.NewGuid();
        Stored(bucket);
        _users.GetByIdAsync(_me, Arg.Any<CancellationToken>())
            .Returns(new AppUser { Id = _me, Email = "guest@example.com" });
        OnTheList(bucket.Id, "guest@example.com");

        Assert.True(await Sut().MayViewAsync(bucket.Id));
    }

    /// <summary>An address differing only in case is plainly the same person to whoever typed it.</summary>
    [Fact]
    public async Task Membership_ignores_the_case_of_an_email()
    {
        var bucket = Mine();
        bucket.OwnerUserId = Guid.NewGuid();
        Stored(bucket);
        _users.GetByIdAsync(_me, Arg.Any<CancellationToken>())
            .Returns(new AppUser { Id = _me, Email = "Guest@Example.com" });
        OnTheList(bucket.Id, "guest@example.com");

        Assert.True(await Sut().MayViewAsync(bucket.Id));
    }

    /// <summary>The owner always sees their own, list or no list.</summary>
    [Fact]
    public async Task The_owner_sees_their_own_bucket()
    {
        var bucket = Mine();
        Stored(bucket);

        Assert.True(await Sut().MayViewAsync(bucket.Id));
    }

    /// <summary>
    /// Membership is matched on an identifier the account has PROVED. A member row is a promise about
    /// a contact, never a way in for whoever claims it.
    /// </summary>
    [Fact]
    public async Task An_account_with_no_verified_contact_matches_nobodys_list()
    {
        var bucket = Mine();
        bucket.OwnerUserId = Guid.NewGuid();
        Stored(bucket);
        _users.GetByIdAsync(_me, Arg.Any<CancellationToken>()).Returns(new AppUser { Id = _me });
        OnTheList(bucket.Id, "guest@example.com");

        Assert.False(await Sut().MayViewAsync(bucket.Id));
    }

    /// <summary>
    /// A phone typed the way an owner types one has to match the E.164 the account proved, or phone
    /// membership fails silently for everybody who uses it — the member row simply never matches.
    /// </summary>
    [Fact]
    public async Task A_phone_member_is_stored_in_the_form_an_account_proves()
    {
        var bucket = Mine();
        Stored(bucket);

        MediaBucketMember? saved = null;
        await _members.AddAsync(Arg.Do<MediaBucketMember>(m => saved = m), Arg.Any<CancellationToken>());

        await Sut().AddMemberAsync(bucket.Id, new AddMediaBucketMemberRequest("781 9157", "Rani"));

        Assert.NotNull(saved);
        Assert.Equal("phone", saved!.ContactType);
        // Malé's country code, from the same default region every other phone in the product uses.
        Assert.Equal("+9607819157", saved.Contact);
    }

    [Fact]
    public async Task A_contact_that_is_neither_an_email_nor_a_phone_is_refused()
    {
        var bucket = Mine();
        Stored(bucket);

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().AddMemberAsync(bucket.Id, new AddMediaBucketMemberRequest("not a contact", null)));
    }

    /// <summary>Adding the same person twice is what happens working down a list; it is not an error.</summary>
    [Fact]
    public async Task Adding_the_same_contact_twice_does_not_make_two_rows()
    {
        var bucket = Mine();
        Stored(bucket);
        var existing = OnTheList(bucket.Id, "guest@example.com", name: "Amira");

        var again = await Sut().AddMemberAsync(
            bucket.Id, new AddMediaBucketMemberRequest("GUEST@example.com", "Amira"));

        Assert.Equal(existing.Id, again.Id);
        await _members.DidNotReceive().AddAsync(Arg.Any<MediaBucketMember>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Puts one contact on the bucket's list, and returns the row.</summary>
    private MediaBucketMember OnTheList(Guid bucketId, string contact, string? name = null)
    {
        var member = new MediaBucketMember
        {
            Id = Guid.NewGuid(),
            BucketId = bucketId,
            Contact = contact,
            ContactType = "email",
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _members.Query(Arg.Any<bool>()).Returns(new[] { member }.AsAsyncQueryable());
        _members.AnyAsync(
                Arg.Is<System.Linq.Expressions.Expression<Func<MediaBucketMember, bool>>>(
                    e => e.Compile()(member)),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _members.FirstOrDefaultAsync(
                Arg.Is<System.Linq.Expressions.Expression<Func<MediaBucketMember, bool>>>(
                    e => e.Compile()(member)),
                Arg.Any<CancellationToken>())
            .Returns(member);
        return member;
    }

    // ---------- codes ----------

    private MediaBucketQr Code(Guid bucketId, string token, bool anonymous = false) => new()
    {
        Id = Guid.NewGuid(),
        BucketId = bucketId,
        TokenHash = TokenService.Hash(token),
        TokenHint = token[..6],
        ImageUrl = "/assets/qr.png",
        AllowAnonymous = anonymous,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task A_live_code_admits_a_contributor_to_its_bucket()
    {
        var bucket = Mine();
        Stored(bucket);
        const string token = "a-printed-token";
        _qrs.Query(Arg.Any<bool>()).Returns(new[] { Code(bucket.Id, token, anonymous: true) }.AsAsyncQueryable());

        var admission = await Sut().AdmitAsync(token);

        Assert.NotNull(admission);
        Assert.Equal(bucket.Id, admission!.BucketId);
        Assert.True(admission.AllowAnonymous);
    }

    /// <summary>
    /// The entire point of codes being rows: a card already on a table has to be refusable.
    /// </summary>
    [Fact]
    public async Task A_revoked_code_admits_nobody()
    {
        var bucket = Mine();
        Stored(bucket);
        const string token = "a-printed-token";
        var code = Code(bucket.Id, token);
        code.RevokedAt = DateTimeOffset.UtcNow;
        _qrs.Query(Arg.Any<bool>()).Returns(new[] { code }.AsAsyncQueryable());

        Assert.Null(await Sut().AdmitAsync(token));
    }

    [Fact]
    public async Task An_unknown_token_admits_nobody_and_does_not_throw()
    {
        Assert.Null(await Sut().AdmitAsync("never-issued"));
        Assert.Null(await Sut().AdmitAsync(""));
    }

    /// <summary>
    /// A full bucket still opens the page. Refusing at the door would show somebody standing at a
    /// party a dead link instead of a sentence explaining why their photo will not go in.
    /// </summary>
    [Fact]
    public async Task A_full_bucket_still_admits_but_says_it_cannot_take_anything()
    {
        var bucket = Mine(capacityGb: 10, used: 10 * MediaBucketPlans.BytesPerGb);
        Stored(bucket);
        const string token = "a-printed-token";
        _qrs.Query(Arg.Any<bool>()).Returns(new[] { Code(bucket.Id, token, anonymous: true) }.AsAsyncQueryable());

        var admission = await Sut().AdmitAsync(token);

        Assert.NotNull(admission);
        Assert.False(admission!.CanUpload);
    }

    /// <summary>
    /// The token must not be reconstructible from what is stored — which is exactly why the rendered
    /// image is kept instead, and why a later read can still show the host their code.
    /// </summary>
    [Fact]
    public async Task A_new_code_returns_its_link_once_and_stores_only_a_hash_and_a_picture()
    {
        var bucket = Mine();
        Stored(bucket);

        MediaBucketQr? saved = null;
        await _qrs.AddAsync(Arg.Do<MediaBucketQr>(q => saved = q), Arg.Any<CancellationToken>());

        var made = await Sut().CreateQrAsync(bucket.Id, new CreateMediaBucketQrRequest("Tables", true));

        Assert.NotNull(made.Url);
        Assert.NotNull(saved);
        Assert.NotEqual(made.Url, saved!.TokenHash);
        Assert.DoesNotContain(saved.TokenHash, made.Url!);
        Assert.False(string.IsNullOrWhiteSpace(saved.ImageUrl));
    }
}
