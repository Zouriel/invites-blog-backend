using InvitesBlog.Api.Controllers;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Payments;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.Payments;
using InvitesBlog.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The fake checkout pages. On a live server "complete" marks a payment paid without anybody paying,
/// so what matters here is that they are not there at all outside Development, and that while they
/// are, nothing from the query string can script the page or send the browser somewhere else.
/// </summary>
public class DevCheckoutTests
{
    private readonly IPaymentService _payments = Substitute.For<IPaymentService>();

    private static IWebHostEnvironment Env(string name)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    // DispatchService is never reached by a refused request, which is exactly what these assert.
    private PaymentsController Controller(string environment) => new(_payments, null!, Env(environment));

    private static PaymentService Service(IConfiguration? config = null)
    {
        var provider = Substitute.For<IPaymentProvider>();
        provider.Name.Returns("Fake");
        var cfg = config ?? new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls:InviterBase"] = "https://invites.blog",
            ["Urls:InviteeBase"] = "https://me.invites.blog",
        }).Build();
        return new PaymentService(
            new CampaignOwnershipService(
                Substitute.For<ICurrentUser>(), Substitute.For<IRepository<AppUser>>(),
                Substitute.For<ICampaignRepository>(), Substitute.For<IInviterRepository>(), TestData.NoCelebrants(),
                TestData.Empty<Venue>(), TestData.Empty<VenueStaff>()),
            Substitute.For<ICampaignRepository>(), Substitute.For<IPaymentRepository>(),
            Substitute.For<IGuestRepository>(), Substitute.For<IUnitOfWork>(), provider, cfg, TestData.FreePlans());
    }

    // ----- not there outside Development ---------------------------------------------------------

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void The_checkout_page_does_not_exist_outside_development(string environment)
    {
        var result = Controller(environment).DevCheckout("s", "p", 10m, "/", "/");

        Assert.IsType<NotFoundResult>(result);
        _payments.DidNotReceiveWithAnyArgs().BuildDevCheckoutPage(default!, default!, default, default!, default!);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Completing_a_checkout_is_refused_outside_development_and_marks_nothing_paid(string environment)
    {
        var result = await Controller(environment).DevCheckoutComplete("sess", "pay", "/", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        await _payments.DidNotReceiveWithAnyArgs().CompleteDevCheckoutAsync(default!, default!, default);
    }

    [Fact]
    public void In_development_the_page_is_served()
    {
        _payments.BuildDevCheckoutPage("s", "p", 10m, "/", "/").Returns("<html></html>");

        var result = Controller("Development").DevCheckout("s", "p", 10m, "/", "/");

        Assert.Equal("<html></html>", Assert.IsType<ContentResult>(result).Content);
    }

    [Fact]
    public async Task In_development_completing_redirects_only_to_a_safe_address()
    {
        _payments.CompleteDevCheckoutAsync("sess", "pay", Arg.Any<CancellationToken>())
            .Returns(new WebhookProcessResult(true, null));
        _payments.SafeReturnUrl("https://evil.example/phish").Returns("/");

        var result = await Controller("Development")
            .DevCheckoutComplete("sess", "pay", "https://evil.example/phish", CancellationToken.None);

        Assert.Equal("/", Assert.IsType<RedirectResult>(result).Url);
    }

    // ----- the page itself ------------------------------------------------------------------------

    [Fact]
    public void Nothing_from_the_query_string_reaches_the_page_unencoded()
    {
        var page = Service().BuildDevCheckoutPage(
            "\"><script>alert(1)</script>", "<img src=x onerror=alert(2)>", 12.5m,
            "/dashboard?x=\"><script>alert(3)</script>", "\"><script>alert(document.domain)</script>");

        Assert.DoesNotContain("<script>", page);
        Assert.DoesNotContain("<img", page);
        Assert.Contains("MVR 12.50", page);
    }

    [Fact]
    public void A_javascript_cancel_link_is_replaced_not_just_encoded()
    {
        var page = Service().BuildDevCheckoutPage("s", "p", 1m, "/", "javascript:alert(1)");

        Assert.DoesNotContain("javascript:", page);
        Assert.Contains("href=\"/\"", page);
    }

    // ----- where a browser may be sent ------------------------------------------------------------

    [Theory]
    [InlineData("/dashboard/123", "/dashboard/123")]
    [InlineData("/", "/")]
    [InlineData("https://invites.blog/dashboard/1", "https://invites.blog/dashboard/1")]
    [InlineData("https://me.invites.blog/inbox", "https://me.invites.blog/inbox")]
    public void A_path_or_an_address_on_the_apps_own_origins_is_kept(string given, string expected) =>
        Assert.Equal(expected, Service().SafeReturnUrl(given));

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example/phish")]
    [InlineData("/\\evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://invites.blog.evil.example/")]
    [InlineData("http://invites.blog:8443/")]
    [InlineData("dashboard/1")]
    [InlineData("")]
    [InlineData(null)]
    public void Anywhere_else_goes_home(string? given) =>
        Assert.Equal("/", Service().SafeReturnUrl(given));
}
