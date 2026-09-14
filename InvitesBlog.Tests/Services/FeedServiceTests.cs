using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Feed;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.Feed;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>The feed: who sees an event's post, and what they may do under it.</summary>
public class FeedServiceTests
{
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IInviterRepository _inviters = Substitute.For<IInviterRepository>();
    private readonly IRepository<CampaignCelebrant> _celebrants = TestData.NoCelebrants();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly IInviteRepository _invites = Substitute.For<IInviteRepository>();
    private readonly IRepository<EventPhoto> _photos = Substitute.For<IRepository<EventPhoto>>();
    private readonly IRepository<MediaBucket> _buckets = Substitute.For<IRepository<MediaBucket>>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly IRepository<PostComment> _comments = Substitute.For<IRepository<PostComment>>();
    private readonly IRepository<PostLike> _postLikes = Substitute.For<IRepository<PostLike>>();
    private readonly IRepository<CommentLike> _commentLikes = Substitute.For<IRepository<CommentLike>>();
    private readonly ICampaignOwnershipService _ownership = Substitute.For<ICampaignOwnershipService>();
    private readonly IMediaBucketService _bucketService = Substitute.For<IMediaBucketService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    private readonly AppUser _me = new() { Id = Guid.NewGuid(), Email = "guest@test.com", DisplayName = "Mariyam" };
    private readonly Campaign _event = TestData.Campaign();
    private readonly List<PostComment> _stored = [];

    public FeedServiceTests()
    {
        _currentUser.UserId.Returns(_me.Id);
        _users.GetByIdAsync(_me.Id, Arg.Any<CancellationToken>()).Returns(_me);
        _users.Query(Arg.Any<bool>()).Returns(new[] { _me }.AsAsyncQueryable());
        _event.CreatedByUserId = Guid.NewGuid();
        _event.InviterId = null;
        _campaigns.GetByIdAsync(_event.Id, Arg.Any<CancellationToken>()).Returns(_event);
        _campaigns.Query(Arg.Any<bool>()).Returns(new[] { _event }.AsAsyncQueryable());
        _inviters.Query(Arg.Any<bool>()).Returns(Array.Empty<Inviter>().AsAsyncQueryable());
        _guests.Query(Arg.Any<bool>()).Returns(Array.Empty<Guest>().AsAsyncQueryable());
        _invites.Query(Arg.Any<bool>()).Returns(Array.Empty<Invite>().AsAsyncQueryable());
        _photos.Query(Arg.Any<bool>()).Returns(Array.Empty<EventPhoto>().AsAsyncQueryable());
        _buckets.Query(Arg.Any<bool>()).Returns(Array.Empty<MediaBucket>().AsAsyncQueryable());
        _comments.Query(Arg.Any<bool>()).Returns(_ => _stored.AsAsyncQueryable());
        _comments.AddAsync(Arg.Do<PostComment>(c => _stored.Add(c)), Arg.Any<CancellationToken>());
        _postLikes.Query(Arg.Any<bool>()).Returns(Array.Empty<PostLike>().AsAsyncQueryable());
        _commentLikes.Query(Arg.Any<bool>()).Returns(Array.Empty<CommentLike>().AsAsyncQueryable());
    }

    private FeedService Sut() => new(
        _currentUser, _users, _campaigns, _inviters, _celebrants, _guests, _invites, _photos, _buckets,
        _templates, _comments, _postLikes, _commentLikes, _ownership, _bucketService, _uow);

    private void InvitedAsGuest(InviteStatus status)
    {
        var guest = TestData.Guest(_event.Id, email: "guest@test.com", phone: null);
        _guests.Query(Arg.Any<bool>()).Returns(new[] { guest }.AsAsyncQueryable());
        _invites.Query(Arg.Any<bool>()).Returns(new[]
        {
            new Invite { Id = Guid.NewGuid(), CampaignId = _event.Id, GuestId = guest.Id, Status = status, TokenHash = "x" },
        }.AsAsyncQueryable());
    }

    [Fact]
    public async Task A_guest_sees_the_post_once_their_invitation_has_gone_out()
    {
        InvitedAsGuest(InviteStatus.Sent);

        var page = await Sut().FeedAsync(0, 10);

        var post = Assert.Single(page.Items);
        Assert.Equal("guest", post.Role);
        Assert.False(post.CanModerate);
        Assert.Equal($"/invitation/{_event.Id}", post.Link);
    }

    [Fact]
    public async Task A_guest_whose_invitation_has_not_gone_out_sees_nothing()
    {
        InvitedAsGuest(InviteStatus.Queued);

        Assert.Empty((await Sut().FeedAsync(0, 10)).Items);
    }

    [Fact]
    public async Task The_organiser_sees_their_event_from_the_start_and_can_moderate()
    {
        _event.CreatedByUserId = _me.Id;

        var post = Assert.Single((await Sut().FeedAsync(0, 10)).Items);

        Assert.Equal("host", post.Role);
        Assert.True(post.CanModerate);
    }

    [Fact]
    public async Task A_cancelled_event_leaves_the_feed()
    {
        _event.CreatedByUserId = _me.Id;
        _event.Status = CampaignStatus.Cancelled;

        Assert.Empty((await Sut().FeedAsync(0, 10)).Items);
    }

    [Fact]
    public async Task A_stranger_cannot_comment()
    {
        await Assert.ThrowsAsync<ForbiddenException>(
            () => Sut().AddCommentAsync(_event.Id, new AddFeedCommentRequest("Hello")));
    }

    [Fact]
    public async Task A_reply_to_a_reply_joins_the_comment_it_belongs_to()
    {
        InvitedAsGuest(InviteStatus.Viewed);
        var top = await Sut().AddCommentAsync(_event.Id, new AddFeedCommentRequest("Congratulations!"));
        var reply = await Sut().AddCommentAsync(_event.Id, new AddFeedCommentRequest("So happy", top.Id));

        var nested = await Sut().AddCommentAsync(_event.Id, new AddFeedCommentRequest("Me too", reply.Id));

        Assert.Equal(top.Id, nested.ParentId);
    }

    [Fact]
    public async Task A_guest_cannot_remove_someone_elses_comment()
    {
        InvitedAsGuest(InviteStatus.Sent);
        _stored.Add(new PostComment
        {
            Id = Guid.NewGuid(), CampaignId = _event.Id, UserId = Guid.NewGuid(), Body = "Hi", CreatedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().DeleteCommentAsync(_event.Id, _stored[0].Id));
    }

    [Fact]
    public async Task Only_the_organiser_can_change_the_caption()
    {
        InvitedAsGuest(InviteStatus.Sent);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Sut().SetCaptionAsync(_event.Id, new SetCaptionRequest("Our day")));
    }

    [Fact]
    public async Task An_empty_comment_is_refused()
    {
        InvitedAsGuest(InviteStatus.Sent);

        var e = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().AddCommentAsync(_event.Id, new AddFeedCommentRequest("   ")));
        Assert.Equal("comment_empty", e.ErrorCode);
    }

    [Fact]
    public void The_invitation_wording_becomes_the_caption_when_none_is_written()
    {
        var (caption, venue) = FeedService.ReadContent("{\"description\":\"Join us\",\"venueName\":\"Hulhumale Hall\"}");

        Assert.Equal("Join us", caption);
        Assert.Equal("Hulhumale Hall", venue);
    }
}
