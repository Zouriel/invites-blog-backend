using System.Net;
using System.Text;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Infrastructure.Images;
using InvitesBlog.Infrastructure.Templates;
using InvitesBlog.Infrastructure.Templates.Art;
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
/// Pixabay in the art library: shown only to the owner until Pixabay approves our use, searched for
/// illustrations and vectors, imported as their picture until full access, and its key (which travels in
/// the URL) never written to the logs.
/// </summary>
public class PixabaySourceTests
{
    private const string Key = "test-key-1234567890";
    private static readonly Guid Owner = Guid.NewGuid();

    private sealed class FakePixabay : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public byte[] Png { get; } = MakePng();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var url = request.RequestUri!;
            if (url.AbsolutePath.EndsWith(".png"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) { Headers = { ContentType = new("image/png") } } });
            const string hit = """
                {"id":12345,"tags":"roses, wreath, wedding, floral","user":"artist","pageURL":"https://pixabay.com/illustrations/roses-12345/","previewURL":"https://cdn.pixabay.com/photo/a_150.png","webformatURL":"https://pixabay.com/get/a_640.png","largeImageURL":"https://pixabay.com/get/a_1280.png","imageURL":"https://pixabay.com/get/a_original.png","vectorURL":"https://pixabay.com/get/a.svg","imageWidth":2000,"imageHeight":1500}
                """;
            var json = "{\"total\":1,\"totalHits\":1,\"hits\":[" + hit + "]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        private static byte[] MakePng()
        {
            using var image = new Image<Rgba32>(40, 30, new Rgba32(200, 120, 140));
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }
    }

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

    private static (PixabaySource Source, FakePixabay Fake, Capture Logs, IConfiguration Config) Make(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["ArtLibrary:Pixabay:ApiKey"] = Key, ["ArtLibrary:Pixabay:AllowedUserIds"] = Owner.ToString() };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var fake = new FakePixabay();
        var logs = new Capture();
        var services = new ServiceCollection().AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        PixabaySource.AddClient(services).ConfigurePrimaryHttpMessageHandler(() => fake);
        var provider = services.BuildServiceProvider();
        var source = new PixabaySource(provider.GetRequiredService<IHttpClientFactory>(), config, provider.GetRequiredService<ILogger<PixabaySource>>());
        return (source, fake, logs, config);
    }

    private static ArtLibrary Library(PixabaySource source, Guid? user)
    {
        var current = Substitute.For<ICurrentUser>();
        current.UserId.Returns(user);
        var engine = new DesignEngine(new RawTemplatePackager(Substitute.For<IStorageService>()), new ImageSharpOptimizer(NullLogger<ImageSharpOptimizer>.Instance),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Urls:AssetsBase"] = "/assets" }).Build());
        return new ArtLibrary([source], engine, current, NullLogger<ArtLibrary>.Instance);
    }

    [Fact]
    public async Task Only_the_allowed_accounts_see_it_until_it_is_made_public()
    {
        var (source, _, _, _) = Make();
        Assert.Contains(Library(source, Owner).Sources(), s => s.Id == "pixabay" && s.Available);
        var someone = Library(source, Guid.NewGuid());
        Assert.DoesNotContain(someone.Sources(), s => s.Id == "pixabay");
        await Assert.ThrowsAsync<NotFoundException>(() => someone.SearchAsync("pixabay", "roses", "vector", 1));
        await Assert.ThrowsAsync<NotFoundException>(() => someone.ImportAsync(new("pixabay", "12345", null)));
        Assert.DoesNotContain(Library(source, null).Sources(), s => s.Id == "pixabay");

        var (open, _, _, _) = Make(new() { ["ArtLibrary:Pixabay:Public"] = "true" });
        Assert.Contains(Library(open, Guid.NewGuid()).Sources(), s => s.Id == "pixabay");
    }

    [Fact]
    public async Task Searches_vectors_and_illustrations_and_says_where_they_are_from()
    {
        var (source, fake, _, _) = Make();
        var result = await Library(source, Owner).SearchAsync("pixabay", "roses", "vector", 1);
        var item = Assert.Single(result.Items);
        Assert.Equal("Roses, wreath, wedding", item.Title);
        Assert.Equal("Pixabay Content License", item.License);
        Assert.Equal("https://pixabay.com/illustrations/roses-12345/", item.PageUrl);
        Assert.Contains("image_type=vector", fake.Requests[0].Query);

        await Library(source, Owner).SearchAsync("pixabay", "lanterns", "picture", 1);
        Assert.Contains("image_type=illustration", fake.Requests[^1].Query);
    }

    [Fact]
    public async Task Search_results_are_kept_for_a_day_as_pixabay_asks()
    {
        var (source, fake, _, _) = Make();
        await Library(source, Owner).SearchAsync("pixabay", "peonies-cache-test", "vector", 1);
        await Library(source, Owner).SearchAsync("pixabay", "peonies-cache-test", "vector", 1);
        Assert.Single(fake.Requests, r => r.Query.Contains("peonies-cache-test"));
    }

    [Fact]
    public async Task Imports_their_picture_until_full_access_then_the_svg()
    {
        var (source, fake, _, _) = Make();
        var file = await source.FetchAsync("12345", null, CancellationToken.None);
        Assert.Equal("/get/a_1280.png", fake.Requests[^1].AbsolutePath);
        Assert.Equal("Pixabay", file.Credit.Source);

        var (full, fullFake, _, _) = Make(new() { ["ArtLibrary:Pixabay:FullAccess"] = "true" });
        await full.FetchAsync("12345", null, CancellationToken.None);
        Assert.Equal("/get/a.svg", fullFake.Requests[^1].AbsolutePath);
    }

    /// <summary>
    /// .NET's own HTTP logging already leaves the query (and so the key) out of URLs; the client also has its
    /// loggers removed as a second guard. This pins the outcome, whichever guard holds.
    /// </summary>
    [Fact]
    public async Task The_key_never_reaches_the_logs()
    {
        var (source, fake, logs, _) = Make();
        await source.SearchAsync("logging-test", "vector", 1, CancellationToken.None);
        await source.FetchAsync("12345", null, CancellationToken.None);
        Assert.Contains(fake.Requests, r => r.Query.Contains(Key));
        Assert.DoesNotContain(logs.Lines, l => l.Contains(Key));
    }
}
