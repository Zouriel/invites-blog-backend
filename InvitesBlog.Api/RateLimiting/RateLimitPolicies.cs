using System.Security.Claims;
using System.Threading.RateLimiting;

namespace InvitesBlog.Api.RateLimiting;

/// <summary>The rate limit on starting an event, kept here so it can be tested on its own.</summary>
public static class RateLimitPolicies
{
    /// <summary>Creating an event: the three doors a visitor may use without signing in.</summary>
    public const string CreateEvent = "create-event";

    /// <summary>
    /// Every endpoint that checks a one-time code: 30 guesses per visitor per 10 minutes, across all codes.
    /// Each code also ends after 5 wrong guesses (OtpService); this stops one visitor asking for code after
    /// code and guessing at all of them.
    /// </summary>
    public const string OtpVerify = "otp-verify";
    public const int OtpVerifyPerWindow = 30;

    public const int AnonymousCreatesPerWindow = 10;
    public const int AccountCreatesPerWindow = 60;
    public static readonly TimeSpan CreateWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Anonymous creation stays open — a visitor makes a draft before they have an account, and the
    /// flows depend on it — but each one writes a campaign (and a template row, and later a bucket),
    /// so an unlimited door is a way to fill the database. Ten in ten minutes per address is far more
    /// than a person making their own event needs.
    ///
    /// <para>Only a signed-in ACCOUNT gets the larger allowance. A campaign possession token also
    /// authenticates, but anybody gets one by creating an anonymous draft, so counting it as "signed
    /// in" would let one draft's token lift the limit for every draft after it.</para>
    /// </summary>
    public static RateLimitPartition<string> CreateEventPartition(HttpContext context)
    {
        var account = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            : null;

        return account is not null
            ? RateLimitPartition.GetFixedWindowLimiter($"account:{account}", _ => Window(AccountCreatesPerWindow))
            : RateLimitPartition.GetFixedWindowLimiter(
                $"address:{ClientAddress.PartitionKey(context)}", _ => Window(AnonymousCreatesPerWindow));
    }

    /// <summary>The designer assistant: every step of an exchange is a request, so this is per account, not per address.</summary>
    public const string DesignAssistant = "design-assistant";

    public static RateLimitPartition<string> DesignAssistantPartition(HttpContext context)
    {
        var account = context.User.Identity?.IsAuthenticated == true ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        return RateLimitPartition.GetFixedWindowLimiter(account is not null ? $"account:{account}" : $"address:{ClientAddress.PartitionKey(context)}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    }

    private static FixedWindowRateLimiterOptions Window(int permits) =>
        new() { PermitLimit = permits, Window = CreateWindow, QueueLimit = 0 };
}
