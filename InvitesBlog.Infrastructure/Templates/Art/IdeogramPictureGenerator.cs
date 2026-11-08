using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Designs.AiPictures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Templates.Art;

/// <summary>
/// Ideogram 4.0 with a transparent background (POST /v2/image/generate/ideogram-4-transparent): one
/// PNG with alpha per request, at the cheapest settings that still look right in an invitation
/// (<see cref="AiPictureOptions.RenderingSpeed"/>, <see cref="AiPictureOptions.Resolution"/>).
///
/// <para>The key goes in the <c>Api-Key</c> header; the picture's link is signed and short-lived, so
/// it is downloaded at once. Request logging is off on this client so neither reaches the logs.</para>
/// </summary>
public sealed class IdeogramPictureGenerator(IHttpClientFactory http, AiPictureOptions options, ILogger<IdeogramPictureGenerator> log)
    : IPictureGenerator
{
    public const string ClientName = "ai-ideogram";
    public const string Endpoint = "https://api.ideogram.ai/v2/image/generate/ideogram-4-transparent";
    private const int MaxDownloadBytes = 20 * 1024 * 1024;

    public static IHttpClientBuilder AddClient(IServiceCollection services) =>
        services.AddHttpClient(ClientName, c =>
            {
                // A turbo 1k picture takes seconds; this only stops a runaway.
                c.Timeout = TimeSpan.FromSeconds(150);
                c.DefaultRequestHeaders.UserAgent.ParseAdd(ArtHttp.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(ArtHttp.Handler)
            .RemoveAllLoggers();

    public bool Configured => !string.IsNullOrWhiteSpace(options.ApiKey);

    /// <summary>The request body; public so a test can check exactly what is sent.</summary>
    public static JsonObject Body(string prompt, string aspect, AiPictureOptions options) => new()
    {
        ["prompt"] = prompt,
        ["num_images"] = 1,
        ["aspect_ratio"] = aspect,
        ["output_resolution"] = options.Resolution,
        ["rendering_speed"] = options.RenderingSpeed,
        ["magic_prompt"] = "auto",
        // Logos and real people's likeness are refused rather than put in a design someone publishes.
        ["enable_copyright_detection"] = true,
    };

    public async Task<GeneratedPicture> GenerateAsync(string prompt, string aspect, CancellationToken ct)
    {
        if (!Configured) throw new InvalidStateException("Creating pictures with AI isn't available right now.", "ai_picture_unavailable");
        var client = http.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(Body(prompt, aspect, options)) };
        request.Headers.Add("Api-Key", options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Ideogram didn't answer: {Error}", e.GetType().Name);
            throw new BusinessRuleException("The picture maker didn't answer — try again in a moment.", "ai_picture_failed");
        }

        JsonNode? json;
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // Their error text never holds our key; still, only its first line goes in the log.
                var reason = Short(text);
                log.LogWarning("Ideogram refused a picture: {Status} {Reason}", (int)response.StatusCode, reason);
                throw response.StatusCode switch
                {
                    HttpStatusCode.UnprocessableEntity =>
                        new BusinessRuleException("That description didn't pass the picture maker's safety check — try different words.", "ai_picture_unsafe"),
                    HttpStatusCode.TooManyRequests =>
                        new BusinessRuleException("The picture maker is busy — try again in a minute.", "ai_picture_busy"),
                    HttpStatusCode.PaymentRequired or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        new InvalidStateException("Creating pictures with AI is paused right now.", "ai_picture_unavailable"),
                    _ => new BusinessRuleException("The picture maker couldn't make that one — try again.", "ai_picture_failed"),
                };
            }
            try { json = JsonNode.Parse(text); }
            catch (System.Text.Json.JsonException) { json = null; }
        }

        var image = json?["data"]?.AsArray().FirstOrDefault();
        if (image is null) throw new BusinessRuleException("The picture maker couldn't make that one — try again.", "ai_picture_failed");
        if ((bool?)image["is_image_safe"] == false || string.IsNullOrEmpty((string?)image["url"]))
            throw new BusinessRuleException("That picture didn't pass the picture maker's checks (it may have looked like a logo or a real person) — try different words.", "ai_picture_unsafe");
        if (!Uri.TryCreate((string?)image["url"], UriKind.Absolute, out var url))
            throw new BusinessRuleException("The picture maker couldn't make that one — try again.", "ai_picture_failed");

        var (content, _) = await ArtHttp.DownloadAsync(client, url, MaxDownloadBytes, ct);
        if (ImageSniffer.Detect(content) is not ("image/png" or "image/webp"))
            throw new BusinessRuleException("The picture maker sent something that isn't a picture — try again.", "ai_picture_failed");
        return new GeneratedPicture(content, (string?)image["prompt"] ?? prompt);
    }

    private static string Short(string text)
    {
        var line = text.Split('\n', 2)[0];
        return line.Length > 200 ? line[..200] : line;
    }
}
