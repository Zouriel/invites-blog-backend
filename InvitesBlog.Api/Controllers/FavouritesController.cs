using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Favourites;
using InvitesBlog.Application.Services.Favourites;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// The signed-in person's Favourites: hearting and un-hearting what they can see in any album, the
/// list of what they kept, and a zip of it. Who may heart what is decided in <see cref="IFavouriteService"/>
/// by the album reads themselves.
/// </summary>
[ApiController]
[Route("api/me/favourites")]
public sealed class FavouritesController(IFavouriteService favourites, Photos.ArchiveTickets archives) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Photos.Read)]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 60, CancellationToken ct = default) =>
        Success(await favourites.ListAsync(page, pageSize, ct));

    [HttpPost("{photoId:guid}")]
    [HasPermission(Permissions.Photos.Read)]
    public async Task<IActionResult> Add(Guid photoId, CancellationToken ct) =>
        Success(await favourites.AddAsync(photoId, ct));

    [HttpDelete("{photoId:guid}")]
    [HasPermission(Permissions.Photos.Read)]
    public async Task<IActionResult> Remove(Guid photoId, CancellationToken ct) =>
        Success(await favourites.RemoveAsync(photoId, ct));

    /// <summary>A zip of the chosen favourites, as a link the browser saves by itself (like an album's).</summary>
    [HttpPost("download-link")]
    [HasPermission(Permissions.Photos.Read)]
    public async Task<IActionResult> DownloadLink([FromBody] FavouritesDownloadRequest? req, CancellationToken ct)
    {
        var archive = await favourites.PlanArchiveAsync(req?.Ids, ct);
        return Success(new ArchiveLinkDto(
            $"/api/photos/archives/{archives.Issue(archive)}", archive.Name, archive.PhotoIds.Count, archive.Bytes));
    }
}
