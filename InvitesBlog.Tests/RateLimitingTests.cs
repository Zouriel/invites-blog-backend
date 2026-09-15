using System.Net;
using System.Security.Claims;
using InvitesBlog.Api.RateLimiting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// What a per-client limit is keyed on, and the limit on starting events. A limiter is only as good as
/// its partition key: one that differs per request limits nothing, and one a caller can choose limits
/// nothing either.
/// </summary>
public class RateLimitingTests
{
    private static HttpContext From(string remote, string? cloudflareVisitor = null, ClaimsPrincipal? user = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (cloudflareVisitor is not null) context.Request.Headers[ClientAddress.CloudflareHeader] = cloudflareVisitor;
        if (user is not null) context.User = user;
        return context;
    }

    // ----- who the client is ----------------------------------------------------------------------

    [Fact]
    public void A_visitor_reaching_the_proxy_directly_is_keyed_on_their_own_address() =>
        Assert.Equal("203.0.113.7", ClientAddress.PartitionKey(From("203.0.113.7")));

    /// <summary>
    /// Behind Cloudflare the proxy's peer is an edge server, and a visitor's requests leave through
    /// several of them. Keyed on the edge, every request would get a fresh quota.
    /// </summary>
    [Fact]
    public void Behind_cloudflare_every_edge_server_maps_back_to_the_one_visitor()
    {
        var first = ClientAddress.PartitionKey(From("172.68.10.1", cloudflareVisitor: "198.51.100.23"));
        var second = ClientAddress.PartitionKey(From("104.23.4.5", cloudflareVisitor: "198.51.100.23"));

        Assert.Equal("198.51.100.23", first);
        Assert.Equal(first, second);
    }

    /// <summary>Somebody who reaches the proxy without Cloudflare can send the header too. It is ignored.</summary>
    [Fact]
    public void The_cloudflare_header_is_ignored_when_the_request_did_not_come_through_cloudflare() =>
        Assert.Equal("203.0.113.7", ClientAddress.PartitionKey(From("203.0.113.7", cloudflareVisitor: "1.2.3.4")));

    [Fact]
    public void A_nonsense_cloudflare_header_falls_back_to_the_edge_address() =>
        Assert.Equal("172.68.10.1", ClientAddress.PartitionKey(From("172.68.10.1", cloudflareVisitor: "not-an-ip")));

    /// <summary>One IPv6 connection is routinely handed a whole /64; each address in it is not a new visitor.</summary>
    [Fact]
    public void Ipv6_visitors_are_grouped_by_their_64()
    {
        var one = ClientAddress.PartitionKey(From("2001:db8:1:2::1"));
        var other = ClientAddress.PartitionKey(From("2001:db8:1:2:ffff:eeee:dddd:cccc"));

        Assert.Equal(one, other);
        Assert.NotEqual(one, ClientAddress.PartitionKey(From("2001:db8:1:3::1")));
    }

    [Fact]
    public void An_ipv4_address_carried_in_ipv6_is_the_same_visitor() =>
        Assert.Equal("203.0.113.7", ClientAddress.PartitionKey(From("::ffff:203.0.113.7")));

    // ----- starting events --------------------------------------------------------------------------

    private static int Admitted(HttpContext context, int attempts)
    {
        var partition = RateLimitPolicies.CreateEventPartition(context);
        using var limiter = partition.Factory(partition.PartitionKey);
        var admitted = 0;
        for (var i = 0; i < attempts; i++)
            if (limiter.AttemptAcquire().IsAcquired) admitted++;
        return admitted;
    }

    [Fact]
    public void An_anonymous_visitor_may_start_ten_events_in_the_window_and_no_more() =>
        Assert.Equal(RateLimitPolicies.AnonymousCreatesPerWindow, Admitted(From("203.0.113.7"), 25));

    [Fact]
    public void A_signed_in_account_is_given_a_far_larger_allowance_of_its_own()
    {
        var account = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "InvitesBlog"));
        var context = From("203.0.113.7", user: account);

        Assert.StartsWith("account:", RateLimitPolicies.CreateEventPartition(context).PartitionKey);
        Assert.Equal(RateLimitPolicies.AccountCreatesPerWindow, Admitted(context, 100));
    }

    /// <summary>
    /// A campaign possession token authenticates too, but anybody gets one by starting a draft, so it
    /// must not buy the account allowance — or one draft's token would lift the limit for the rest.
    /// </summary>
    [Fact]
    public void A_campaign_possession_token_is_still_limited_as_the_visitor_it_is()
    {
        var holder = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("campaign_id", Guid.NewGuid().ToString())], "InvitesBlog"));

        var key = RateLimitPolicies.CreateEventPartition(From("203.0.113.7", user: holder)).PartitionKey;

        Assert.Equal("address:203.0.113.7", key);
    }
}
