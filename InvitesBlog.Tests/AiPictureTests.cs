using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Application.Services.Designs.AiPictures;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Images;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Repositories;
using InvitesBlog.Infrastructure.Templates;
using InvitesBlog.Infrastructure.Templates.Art;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// "Create with AI" pictures: only for accounts an admin allowed, a monthly limit counted on pictures
/// that arrived, one at a time per account, the cheapest Ideogram settings with a see-through
/// background, and refusals that read as plain messages without the key reaching the logs.
/// </summary>
public class AiPictureTests
{
    private const string Key = "ideogram-test-key-0987654321";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"ai-pictures-{Guid.NewGuid()}").Options);
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly AiPictureOptions _options = new() { ApiKey = Key, MonthlyLimit = 3 };
    private readonly Guid _me = Guid.NewGuid();

    public AiPictureTests()
    {
        _currentUser.UserId.Returns(_me);
        _currentUser.IsAuthenticated.Returns(true);
        _db.Users.Add(new AppUser { Id = _me, Email = "maker@example.com", DisplayName = "Maker", IsActive = true, AiPicturesAllowed = true, CreatedAt = DateTimeOffset.UtcNow });
        _db.SaveChanges();
    }

    private static byte[] Png(bool seeThrough = true)
    {
        using var image = new Image<Rgba32>(64, 32, new Rgba32(0, 0, 0, 0));
        for (var x = 16; x < 48; x++)
        for (var y = 8; y < 24; y++)
            image[x, y] = new Rgba32(250, 250, 245, seeThrough ? (byte)200 : (byte)255);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private sealed class FakeIdeogram : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];
        public Func<HttpResponseMessage>? Answer { get; set; }
        public TaskCompletionSource? Hold { get; set; }
        public byte[] Picture { get; set; } = Png();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            if (request.RequestUri!.Host == "ideogram.ai")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Picture) { Headers = { ContentType = new("image/png") } } };
            if (Hold is { } hold) await hold.Task;
            if (Answer is { } answer) return answer();
            return Json(HttpStatusCode.OK, """
                {"generation_id":"g1","seed":7,"data":[{"prompt":"Delicate white lace","resolution":"1024x1024","is_image_safe":true,"seed":7,"url":"https://ideogram.ai/api/images/ephemeral/abc.png?exp=1"}]}
                """);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Capture : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string category) => new L(this, category);
        public void Dispose() { }
        private sealed class L(Capture c, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> f)
            {
                lock (c.Lines) c.Lines.Add($"{category}: {f(state, e)}");
            }
        }
    }

    private (AiPictureService Service, FakeIdeogram Fake, Capture Logs) Make()
    {
        var fake = new FakeIdeogram();
        var logs = new Capture();
        var services = new ServiceCollection().AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        IdeogramPictureGenerator.AddClient(services).ConfigurePrimaryHttpMessageHandler(() => fake);
        var provider = services.BuildServiceProvider();
        var generator = new IdeogramPictureGenerator(provider.GetRequiredService<IHttpClientFactory>(), _options, provider.GetRequiredService<ILogger<IdeogramPictureGenerator>>());
        var engine = new DesignEngine(new RawTemplatePackager(Substitute.For<IStorageService>()), new ImageSharpOptimizer(NullLogger<ImageSharpOptimizer>.Instance),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Urls:AssetsBase"] = "/assets" }).Build());
        var art = new ArtLibrary([], engine, _currentUser, NullLogger<ArtLibrary>.Instance);
        var service = new AiPictureService(new BaseRepository<AppUser>(_db), new BaseRepository<AiPictureUsage>(_db), new UnitOfWork(_db),
            _currentUser, generator, art, _options, provider.GetRequiredService<ILogger<AiPictureService>>());
        return (service, fake, logs);
    }

    private void Allow(bool allowed)
    {
        _db.Users.Single(u => u.Id == _me).AiPicturesAllowed = allowed;
        _db.SaveChanges();
    }

    // ----- Who may use it -----------------------------------------------------------------------

    [Fact]
    public async Task Accounts_an_admin_has_not_allowed_are_refused_and_nothing_is_sent()
    {
        Allow(false);
        var (service, fake, _) = Make();
        var status = await service.StatusAsync();
        Assert.False(status.Allowed);
        Assert.Equal("not_allowed", status.Reason);
        await Assert.ThrowsAsync<ForbiddenException>(() => service.GenerateAsync(new("white lace", null)));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Without_a_key_it_says_unavailable()
    {
        _options.ApiKey = null;
        var (service, fake, _) = Make();
        Assert.Equal("unavailable", (await service.StatusAsync()).Reason);
        await Assert.ThrowsAsync<InvalidStateException>(() => service.GenerateAsync(new("white lace", null)));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task An_admin_turns_it_on_and_off_from_the_designers_list()
    {
        Allow(false);
        _db.TemplateDesigns.Add(new TemplateDesign { Id = Guid.NewGuid(), OwnerUserId = _me, Name = "Mine", SceneJson = "{}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _db.SaveChanges();
        var admin = new DesignerAdminService(new BaseRepository<AppUser>(_db), new BaseRepository<UserExternalLogin>(_db),
            new TemplateRepository(_db), new BaseRepository<TemplateDesign>(_db), new UnitOfWork(_db));

        var on = await admin.SetAiPicturesAsync(_me, true);
        Assert.True(on.AiPicturesAllowed);
        Assert.True((await Make().Service.StatusAsync()).Available);
        Assert.Contains((await admin.ListAsync(new())).Items, d => d.UserId == _me && d.AiPicturesAllowed);

        var off = await admin.SetAiPicturesAsync(_me, false);
        Assert.False(off.AiPicturesAllowed);
        Assert.False((await Make().Service.StatusAsync()).Available);
    }

    // ----- Making one ---------------------------------------------------------------------------

    [Fact]
    public async Task Asks_ideogram_for_a_see_through_picture_at_the_cheapest_settings_and_places_it_like_art()
    {
        var (service, fake, _) = Make();
        var art = await service.GenerateAsync(new("  delicate white floral lace border, top-down  ", "strip"));

        var (request, body) = fake.Requests[0];
        Assert.Equal(IdeogramPictureGenerator.Endpoint, request.RequestUri!.ToString());
        Assert.Equal(Key, request.Headers.GetValues("Api-Key").Single());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("delicate white floral lace border, top-down", (string?)json["prompt"]);
        Assert.Equal("4x1", (string?)json["aspect_ratio"]);
        Assert.Equal("turbo", (string?)json["rendering_speed"]);
        Assert.Equal("1k", (string?)json["output_resolution"]);
        Assert.Equal(1, (int?)json["num_images"]);
        Assert.True((bool?)json["enable_copyright_detection"]);

        // The short-lived link is downloaded at once; the picture comes back ready to place, see-through kept.
        Assert.Equal("ideogram.ai", fake.Requests[1].Request.RequestUri!.Host);
        var asset = Assert.Single(art.Assets);
        Assert.Equal("image", asset.Kind);
        Assert.True(asset.Alpha);
        Assert.Equal("Delicate white floral lace border", art.Name);
        Assert.Equal("Ideogram", art.Credit!.Source);
        Assert.Equal(1, (await service.StatusAsync()).Used);
    }

    [Theory]
    [InlineData(null, "1x1")]
    [InlineData("square", "1x1")]
    [InlineData("wide", "2x1")]
    [InlineData("tall", "1x2")]
    public async Task Each_shape_asks_for_its_aspect(string? shape, string aspect)
    {
        var (service, fake, _) = Make();
        await service.GenerateAsync(new("a wax seal", shape));
        Assert.Equal(aspect, (string?)JsonNode.Parse(fake.Requests[0].Body)!["aspect_ratio"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_or_too_long_description_or_unknown_shape_is_refused_before_anything_is_sent(string prompt)
    {
        var (service, fake, _) = Make();
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.GenerateAsync(new(prompt, null)));
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.GenerateAsync(new(new string('a', AiPictureService.MaxPromptLength + 1), null)));
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.GenerateAsync(new("lace", "circle")));
        Assert.Empty(fake.Requests);
    }

    // ----- Limits -------------------------------------------------------------------------------

    [Fact]
    public async Task The_monthly_limit_stops_it_and_only_pictures_that_arrived_count()
    {
        var (service, fake, _) = Make();
        fake.Answer = () => Json(HttpStatusCode.UnprocessableEntity, """{"error":"Prompt failed safety check"}""");
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.GenerateAsync(new("something", null)));
        Assert.Equal(0, (await service.StatusAsync()).Used);

        fake.Answer = null;
        for (var i = 0; i < 3; i++) await service.GenerateAsync(new("lace " + i, null));
        var status = await service.StatusAsync();
        Assert.Equal(3, status.Used);
        Assert.Equal("limit", status.Reason);
        var sent = fake.Requests.Count;
        var e = await Assert.ThrowsAsync<BusinessRuleException>(() => service.GenerateAsync(new("one more", null)));
        Assert.Equal("ai_picture_limit", e.ErrorCode);
        Assert.Equal(sent, fake.Requests.Count);
    }

    [Fact]
    public async Task One_picture_at_a_time_per_account()
    {
        var (service, fake, _) = Make();
        fake.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = service.GenerateAsync(new("lace", null));
        while (fake.Requests.Count == 0) await Task.Delay(5);

        var (second, _, _) = Make();
        var e = await Assert.ThrowsAsync<BusinessRuleException>(() => second.GenerateAsync(new("more lace", null)));
        Assert.Equal("ai_picture_busy", e.ErrorCode);

        fake.Hold.SetResult();
        await first;
        Assert.Single(fake.Requests, r => r.Request.RequestUri!.Host == "api.ideogram.ai");
    }

    // ----- Refusals -----------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "ai_picture_unsafe")]
    [InlineData(HttpStatusCode.TooManyRequests, "ai_picture_busy")]
    [InlineData(HttpStatusCode.InternalServerError, "ai_picture_failed")]
    public async Task Refusals_read_as_plain_messages(HttpStatusCode status, string code)
    {
        var (service, fake, _) = Make();
        fake.Answer = () => Json(status, """{"error":"nope"}""");
        var e = await Assert.ThrowsAsync<BusinessRuleException>(() => service.GenerateAsync(new("lace", null)));
        Assert.Equal(code, e.ErrorCode);
    }

    [Fact]
    public async Task An_empty_balance_pauses_it_rather_than_blaming_the_person()
    {
        var (service, fake, _) = Make();
        fake.Answer = () => Json(HttpStatusCode.PaymentRequired, """{"error":"Insufficient funds","reject_reason":"insufficient_funds"}""");
        var e = await Assert.ThrowsAsync<InvalidStateException>(() => service.GenerateAsync(new("lace", null)));
        Assert.Equal("ai_picture_unavailable", e.ErrorCode);
    }

    [Fact]
    public async Task A_picture_that_fails_their_checks_is_refused_and_not_counted()
    {
        var (service, fake, _) = Make();
        fake.Answer = () => Json(HttpStatusCode.OK, """{"generation_id":"g","seed":1,"data":[{"prompt":"p","resolution":"1024x1024","is_image_safe":false,"seed":1,"url":"https://ideogram.ai/api/images/ephemeral/x.png"}]}""");
        var e = await Assert.ThrowsAsync<BusinessRuleException>(() => service.GenerateAsync(new("a famous logo", null)));
        Assert.Equal("ai_picture_unsafe", e.ErrorCode);
        Assert.Equal(0, (await service.StatusAsync()).Used);
    }

    [Fact]
    public async Task The_key_and_the_signed_link_never_reach_the_logs()
    {
        var (service, fake, logs) = Make();
        await service.GenerateAsync(new("lace", null));
        fake.Answer = () => Json(HttpStatusCode.BadRequest, """{"error":"bad"}""");
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.GenerateAsync(new("lace", null)));
        Assert.NotEmpty(logs.Lines);
        Assert.DoesNotContain(logs.Lines, l => l.Contains(Key) || l.Contains("ephemeral"));
    }

    [Theory]
    [InlineData("delicate white floral lace border, top-down", "Delicate white floral lace border")]
    [InlineData("!!!", "AI picture")]
    [InlineData("a wax seal", "A wax seal")]
    public void The_title_is_the_descriptions_first_words(string prompt, string title) =>
        Assert.Equal(title, AiPictureService.Title(prompt));
}
