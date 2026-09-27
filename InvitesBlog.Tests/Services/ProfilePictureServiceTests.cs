using System.Text;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Accounts;
using InvitesBlog.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The profile picture: Google's until the person chooses, then theirs, and never a reason a sign-in
/// fails.
/// </summary>
public class ProfilePictureServiceTests
{
    private const string GooglePicture = "https://lh3.googleusercontent.com/a/ACg8ocK=s96-c";

    private readonly IStorageService _storage = Substitute.For<IStorageService>();
    private readonly IImageOptimizer _optimizer = Substitute.For<IImageOptimizer>();
    private readonly IRemoteImageFetcher _fetcher = Substitute.For<IRemoteImageFetcher>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    public ProfilePictureServiceTests()
    {
        _optimizer.Optimize(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<int?>())
            .Returns(c => new OptimizedImage(c.ArgAt<byte[]>(0), "image/jpeg", 512, 512, true));
        _storage.PutAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => "/assets/" + c.ArgAt<string>(0));
    }

    private ProfilePictureService Sut() =>
        new(_storage, _optimizer, _fetcher, _uow, NullLogger<ProfilePictureService>.Instance);

    private static AppUser User() => new() { Id = Guid.NewGuid(), DisplayName = "Test" };

    private static byte[] Jpeg()
    {
        using var image = new Image<Rgba32>(8, 8);
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder());
        return ms.ToArray();
    }

    private void GoogleServes(byte[] bytes, string type = "image/jpeg") =>
        _fetcher.FetchAsync(Arg.Any<Uri>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((bytes, type));

    [Fact]
    public async Task Google_s_picture_becomes_the_account_s_on_sign_in()
    {
        var user = User();
        GoogleServes(Jpeg());

        await Sut().ImportFromProviderAsync(user, GooglePicture);

        Assert.StartsWith($"/assets/avatars/{user.Id:N}/", user.AvatarUrl);
        // Taken from Google, not chosen: the person can still pick their own later.
        Assert.Null(user.AvatarChosenAt);
        // At a size worth keeping, not the 96px thumbnail the token points at.
        await _fetcher.Received().FetchAsync(
            new Uri("https://lh3.googleusercontent.com/a/ACg8ocK=s512-c"), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_picture_the_person_chose_is_never_replaced_by_google()
    {
        var user = User();
        user.AvatarUrl = "/assets/avatars/mine.jpg";
        user.AvatarChosenAt = DateTimeOffset.UtcNow;

        await Sut().ImportFromProviderAsync(user, GooglePicture);

        Assert.Equal("/assets/avatars/mine.jpg", user.AvatarUrl);
        await _fetcher.DidNotReceiveWithAnyArgs().FetchAsync(default!, default);
    }

    [Fact]
    public async Task Removing_the_picture_keeps_google_from_putting_it_back()
    {
        var user = User();
        user.AvatarUrl = "/assets/avatars/from-google.jpg";

        await Sut().RemoveAsync(user);
        await Sut().ImportFromProviderAsync(user, GooglePicture);

        Assert.Null(user.AvatarUrl);
        await _fetcher.DidNotReceiveWithAnyArgs().FetchAsync(default!, default);
    }

    [Theory]
    [InlineData("https://evil.example.com/a.jpg")]
    [InlineData("https://googleusercontent.com.evil.example/a.jpg")]
    [InlineData("http://lh3.googleusercontent.com/a/x")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("not a url")]
    public async Task Only_google_s_image_host_is_ever_fetched(string url)
    {
        await Sut().ImportFromProviderAsync(User(), url);

        await _fetcher.DidNotReceiveWithAnyArgs().FetchAsync(default!, default);
    }

    [Fact]
    public async Task A_fetch_that_fails_leaves_the_account_as_it_was_and_does_not_throw()
    {
        var user = User();
        _fetcher.FetchAsync(Arg.Any<Uri>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("storage down"));

        await Sut().ImportFromProviderAsync(user, GooglePicture);

        Assert.Null(user.AvatarUrl);
    }

    [Fact]
    public async Task Something_that_is_not_a_photo_is_not_imported()
    {
        var user = User();
        GoogleServes(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>"), "image/svg+xml");

        await Sut().ImportFromProviderAsync(user, GooglePicture);

        Assert.Null(user.AvatarUrl);
    }

    [Fact]
    public async Task An_upload_replaces_the_picture_and_counts_as_the_person_s_choice()
    {
        var user = User();
        user.AvatarUrl = "/assets/avatars/old.jpg";

        await Sut().SetAsync(user, Jpeg(), "image/jpeg");

        Assert.NotEqual("/assets/avatars/old.jpg", user.AvatarUrl);
        Assert.NotNull(user.AvatarChosenAt);
        await _uow.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>It's served on the app's own origin, where an SVG is a script, not a picture.</summary>
    [Fact]
    public async Task An_svg_upload_is_refused_whatever_it_is_labelled()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");

        await Assert.ThrowsAsync<BusinessRuleException>(() => Sut().SetAsync(User(), svg, "image/jpeg"));
    }
}
