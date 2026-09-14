using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Feed;
using InvitesBlog.Application.Services.Feed;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// The home feed and the conversation under each post. Under /api/me so the signed-in account is
/// always who acts, never a campaign's possession token. Every action re-checks involvement.
/// </summary>
[Route("api/me/feed")]
public sealed class FeedController(IFeedService feed) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> Feed([FromQuery] int skip = 0, [FromQuery] int take = 10, CancellationToken ct = default) =>
        Success(await feed.FeedAsync(skip, take, ct));

    [HttpGet("{campaignId:guid}/comments")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> Comments(Guid campaignId, CancellationToken ct) =>
        Success(await feed.CommentsAsync(campaignId, ct));

    [HttpPost("{campaignId:guid}/comments")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> AddComment(Guid campaignId, [FromBody] AddFeedCommentRequest req, CancellationToken ct) =>
        Success(await feed.AddCommentAsync(campaignId, req, ct));

    [HttpDelete("{campaignId:guid}/comments/{commentId:guid}")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> DeleteComment(Guid campaignId, Guid commentId, CancellationToken ct)
    {
        await feed.DeleteCommentAsync(campaignId, commentId, ct);
        return SuccessMessage("Removed.");
    }

    [HttpPut("{campaignId:guid}/like")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> LikePost(Guid campaignId, [FromBody] SetLikeRequest req, CancellationToken ct) =>
        Success(await feed.SetPostLikeAsync(campaignId, req, ct));

    [HttpPut("{campaignId:guid}/comments/{commentId:guid}/like")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> LikeComment(Guid campaignId, Guid commentId, [FromBody] SetLikeRequest req, CancellationToken ct) =>
        Success(await feed.SetCommentLikeAsync(campaignId, commentId, req, ct));

    [HttpGet("{campaignId:guid}/covers")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> Covers(Guid campaignId, CancellationToken ct) =>
        Success(await feed.CoversAsync(campaignId, ct));

    [HttpPut("{campaignId:guid}/covers")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> SetCovers(Guid campaignId, [FromBody] SetFeedCoversRequest req, CancellationToken ct) =>
        Success(await feed.SetCoversAsync(campaignId, req, ct));

    [HttpPut("{campaignId:guid}/caption")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> SetCaption(Guid campaignId, [FromBody] SetCaptionRequest req, CancellationToken ct) =>
        Success(await feed.SetCaptionAsync(campaignId, req, ct));
}
