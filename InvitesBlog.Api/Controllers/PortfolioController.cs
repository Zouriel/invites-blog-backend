using System.Globalization;
using System.Security.Cryptography;
using InvitesBlog.Api.Authorization;
using InvitesBlog.Api.Rendering;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Portfolios;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>A Studio's controls for its portfolio (docs/PORTFOLIO.md, shapes in docs/PORTFOLIO-HANDOFF.md). Studio only.</summary>
[ApiController]
[Route("api/me/portfolio")]
public sealed class PortfolioController(IPortfolioService portfolio, IConfiguration config) : BaseApiController
{
    public const long MaxLogoBytes = 10 * 1024 * 1024;

    [HttpGet]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Get(CancellationToken ct) => Success(await portfolio.GetAsync(ct));

    [HttpPut]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> Update([FromBody] UpdatePortfolioRequest req, CancellationToken ct) =>
        Success(await portfolio.UpdateAsync(req, ct));

    [HttpGet("handle-check")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> CheckHandle([FromQuery] string? handle, CancellationToken ct) =>
        Success(await portfolio.CheckHandleAsync(handle, ct));

    [HttpPut("handle")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> SetHandle([FromBody] SetHandleRequest req, CancellationToken ct) =>
        Success(await portfolio.SetHandleAsync(req.Handle, ct));

    [HttpPost("logo")]
    [HasPermission(Permissions.Campaigns.Write)]
    [RequestSizeLimit(MaxLogoBytes + 64 * 1024)]
    public async Task<IActionResult> SetLogo(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new CodedRuleException("Choose a picture.", "not_an_image");
        if (file.Length > MaxLogoBytes) throw new CodedRuleException("That picture is too big. Use one under 10 MB.", "too_big");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return Success(await portfolio.SetLogoAsync(ms.ToArray(), file.ContentType, ct));
    }

    [HttpDelete("logo")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> RemoveLogo(CancellationToken ct) => Success(await portfolio.RemoveLogoAsync(ct));

    public const long MaxCoverBytes = 15 * 1024 * 1024;

    /// <summary>A cover picture for the page itself (the client crops it first). Wins over a chosen portfolio photo.</summary>
    [HttpPost("cover")]
    [HasPermission(Permissions.Campaigns.Write)]
    [RequestSizeLimit(MaxCoverBytes + 64 * 1024)]
    public async Task<IActionResult> SetCover(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new CodedRuleException("Choose a picture.", "not_an_image");
        if (file.Length > MaxCoverBytes) throw new CodedRuleException("That picture is too big. Use one under 15 MB.", "too_big");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return Success(await portfolio.SetCoverAsync(ms.ToArray(), file.ContentType, ct));
    }

    [HttpDelete("cover")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> RemoveCover(CancellationToken ct) => Success(await portfolio.RemoveCoverAsync(ct));

    /// <summary>An event's album as the portfolio picker sees it: each photo says whether it can go in, and why not.</summary>
    [HttpGet("/api/me/campaigns/{campaignId:guid}/portfolio-photos")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> EventPhotos(Guid campaignId, CancellationToken ct) =>
        Success(await portfolio.EventPhotosAsync(campaignId, ct));

    [HttpPost("sections")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> AddSection([FromBody] SectionRequest req, CancellationToken ct) =>
        Success(await portfolio.AddSectionAsync(req, ct));

    [HttpPut("sections/order")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> OrderSections([FromBody] OrderRequest req, CancellationToken ct) =>
        Success(await portfolio.OrderSectionsAsync(req.Ids, ct));

    [HttpPut("sections/{id:guid}")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> UpdateSection(Guid id, [FromBody] SectionRequest req, CancellationToken ct) =>
        Success(await portfolio.UpdateSectionAsync(id, req, ct));

    [HttpDelete("sections/{id:guid}")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> DeleteSection(Guid id, CancellationToken ct) => Success(await portfolio.DeleteSectionAsync(id, ct));

    [HttpPost("collections")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> AddCollection([FromBody] CollectionRequest req, CancellationToken ct) =>
        Success(await portfolio.AddCollectionAsync(req, ct));

    [HttpPut("collections/{id:guid}")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> UpdateCollection(Guid id, [FromBody] CollectionRequest req, CancellationToken ct) =>
        Success(await portfolio.UpdateCollectionAsync(id, req, ct));

    [HttpDelete("collections/{id:guid}")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> DeleteCollection(Guid id, CancellationToken ct) => Success(await portfolio.DeleteCollectionAsync(id, ct));

    [HttpPut("collections/{id:guid}/order")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> OrderPhotos(Guid id, [FromBody] PhotoOrderRequest req, CancellationToken ct) =>
        Success(await portfolio.OrderPhotosAsync(id, req.PhotoIds, ct));

    /// <summary>
    /// One picture uploaded straight into an uploads collection (past work that was never on invites.blog). One file per
    /// request, so a client shows each file's progress and retries just the one that failed.
    /// </summary>
    [HttpPost("collections/{id:guid}/photos")]
    [HasPermission(Permissions.Campaigns.Write)]
    [RequestSizeLimit(PortfolioService.MaxUploadBytes + 256 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = PortfolioService.MaxUploadBytes + 256 * 1024)]
    public async Task<IActionResult> UploadPhoto(Guid id, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new CodedRuleException("Choose a picture.", "not_an_image");
        if (file.Length > PortfolioService.MaxUploadBytes)
            throw new CodedRuleException("That picture is too big. Use one under 30 MB.", "too_big", System.Net.HttpStatusCode.RequestEntityTooLarge);
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return Success(await portfolio.UploadPhotoAsync(id, ms.ToArray(), ct));
    }

    [HttpPost("photos")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> AddPhotos([FromBody] AddPortfolioPhotosRequest req, CancellationToken ct) =>
        Success(await portfolio.AddPhotosAsync(req, ct));

    [HttpDelete("photos/{id:guid}")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> RemovePhoto(Guid id, CancellationToken ct) => Success(await portfolio.RemovePhotoAsync(id, ct));

    [HttpPut("publish")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> Publish([FromBody] PublishRequest req, CancellationToken ct) =>
        Success(await portfolio.PublishAsync(req.Published, ct));

    [HttpGet("enquiries")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Enquiries(CancellationToken ct) => Success(await portfolio.EnquiriesAsync(ct));

    [HttpPut("enquiries/{id:guid}/read")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        await portfolio.MarkEnquiryReadAsync(id, ct);
        return Success(new { ok = true });
    }

    [HttpGet("designs")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Designs(CancellationToken ct) => Success(await portfolio.DesignsAsync(ct));

    /// <summary>A 30-minute link that shows the page as it is now, published or not (the editor's Preview).</summary>
    [HttpPost("preview-link")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> PreviewLink(CancellationToken ct)
    {
        var p = await portfolio.GetAsync(ct);
        if (p.Handle is null) throw new CodedRuleException("Choose your link first.", "handle_required");
        var token = PortfolioPreview.Issue(p.Id, DateTimeOffset.UtcNow.Add(PortfolioPreview.Lifetime), config);
        return Success(new { url = $"{config.InviterBase()}/@{p.Handle}?preview={Uri.EscapeDataString(token)}" });
    }

    [HttpGet("/api/me/campaigns/{campaignId:guid}/portfolio-consent")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> GetConsent(Guid campaignId, CancellationToken ct) =>
        Success(await portfolio.GetConsentAsync(campaignId, ct));

    [HttpPut("/api/me/campaigns/{campaignId:guid}/portfolio-consent")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> SetConsent(Guid campaignId, [FromBody] SetConsentRequest req, CancellationToken ct) =>
        Success(await portfolio.SetConsentAsync(campaignId, req.Consented, ct));
}

/// <summary>Signed, expiring preview links: the portfolio id and an expiry, sealed with the server's signing key.</summary>
public static class PortfolioPreview
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private const string Purpose = "portfolio-preview";

    private static string Secret(IConfiguration config) =>
        config["Jwt:SigningKey"] ?? "change-this-in-production-please-use-a-long-random-value";

    public static string Issue(Guid portfolioId, DateTimeOffset expires, IConfiguration config) =>
        TokenSeal.Seal($"{portfolioId:N}|{expires.ToUnixTimeSeconds()}", Secret(config), Purpose);

    /// <summary>The portfolio a valid, unexpired token names; else null.</summary>
    public static Guid? Open(string? token, IConfiguration config, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token) || TokenSeal.Open(token, Secret(config), Purpose) is not { } plain) return null;
        var parts = plain.Split('|');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var id)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var exp)) return null;
        return DateTimeOffset.FromUnixTimeSeconds(exp) > now ? id : null;
    }
}

/// <summary>
/// The public side: the page itself at <c>/@handle</c> (Caddy sends <c>invites.blog/@*</c> here), its data, the look's
/// options, enquiries and "Ask to remove a photo". No account.
/// </summary>
[ApiController]
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public sealed class PortfolioPublicController(IPortfolioPublicService pages, IConfiguration config) : BaseApiController
{
    public const string EnquiryPolicy = "portfolio-enquiry";
    public const string ReportPolicy = "portfolio-report";

    [HttpGet("/@{handle}")]
    public async Task<IActionResult> Page(string handle, [FromQuery] string? preview, CancellationToken ct)
    {
        var previewId = PortfolioPreview.Open(preview, config, DateTimeOffset.UtcNow);
        var found = await pages.FindAsync(handle, previewId, ct);
        var site = config.InviterBase();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (found.RedirectTo is { } moved)
            return RedirectPermanent($"/@{moved}");
        if (found.Portfolio is not { } p)
        {
            Response.Headers.CacheControl = "no-store";
            return new ContentResult
            {
                Content = PortfolioPage.Unavailable(site), ContentType = "text/html; charset=utf-8", StatusCode = StatusCodes.Status404NotFound,
            };
        }
        // A handle typed in capitals opens the one canonical address.
        if (handle != p.Handle && previewId is null) return RedirectPermanent($"/@{p.Handle}");

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var isPreview = previewId is not null;
        Response.Headers.CacheControl = isPreview ? "private, no-store" : "public, max-age=60";
        Response.Headers["Content-Security-Policy"] =
            $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline' https://fonts.googleapis.com; "
            + "font-src https://fonts.gstatic.com; img-src 'self' https: data:; connect-src 'self'; form-action 'self'; "
            + $"base-uri 'none'; frame-ancestors 'self' {config.AppBase()}";
        Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        return Content(PortfolioPage.Render(p, nonce, site, isPreview), "text/html; charset=utf-8");
    }

    /// <summary>
    /// The published portfolios for search engines (robots.txt lists it next to the site's own sitemap.xml, which is
    /// written at build time and can't know them). Caddy sends invites.blog/sitemap-portfolios.xml here.
    /// </summary>
    [HttpGet("/sitemap-portfolios.xml")]
    public async Task<IActionResult> Sitemap(CancellationToken ct)
    {
        var site = config.InviterBase().TrimEnd('/');
        var list = await pages.PublishedAsync(ct);
        var urls = string.Join("\n", list.Select(e =>
            $"  <url><loc>{System.Security.SecurityElement.Escape($"{site}/@{e.Handle}")}</loc><lastmod>{e.UpdatedAt.UtcDateTime:yyyy-MM-dd}</lastmod></url>"));
        Response.Headers.CacheControl = "public, max-age=3600";
        return Content(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n"
            + urls + (list.Count > 0 ? "\n" : "") + "</urlset>\n",
            "application/xml; charset=utf-8");
    }

    [HttpGet("/api/portfolio/options")]
    public IActionResult Options()
    {
        Response.Headers.CacheControl = "public, max-age=3600";
        return Success(PortfolioChoices.Options);
    }

    [HttpGet("/api/portfolio/@{handle}")]
    public async Task<IActionResult> Get(string handle, CancellationToken ct)
    {
        var found = await pages.FindAsync(handle, null, ct);
        if (found.RedirectTo is { } moved)
            return Success(new { redirectTo = moved });
        if (found.Portfolio is null) throw new NotFoundException("This portfolio isn't available.", "portfolio_unavailable");
        Response.Headers.CacheControl = "public, max-age=60";
        return Success(found.Portfolio);
    }

    [HttpPost("/api/portfolio/@{handle}/enquiries")]
    [EnableRateLimiting(EnquiryPolicy)]
    public async Task<IActionResult> Enquire(string handle, [FromBody] PortfolioEnquiryRequest req, CancellationToken ct) =>
        Success(await pages.EnquireAsync(handle, req, ct));

    [HttpPost("/api/portfolio/@{handle}/report")]
    [EnableRateLimiting(ReportPolicy)]
    public async Task<IActionResult> Report(string handle, [FromBody] PortfolioReportRequest req, CancellationToken ct) =>
        Success(await pages.ReportAsync(handle, req, ct));
}
