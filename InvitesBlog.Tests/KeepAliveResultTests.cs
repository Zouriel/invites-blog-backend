using System.Text;
using System.Text.Json;
using InvitesBlog.Api.Controllers;
using InvitesBlog.Application.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// A design assistant step can take minutes; Cloudflare drops a request that sends nothing for 100 s.
/// These pin that a slow answer starts at once with spaces and still ends as the usual JSON envelope.
/// </summary>
[Collection(nameof(KeepAliveResultTests))]
public class KeepAliveResultTests
{
    private record Answer(string Text);

    private static (ActionContext Context, MemoryStream Body) Context()
    {
        var services = new ServiceCollection().AddLogging().AddMvcCore().AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase).Services.BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var body = new MemoryStream();
        http.Response.Body = body;
        return (new ActionContext(http, new RouteData(), new ActionDescriptor()), body);
    }

    private static async Task<T> Later<T>(int ms, Func<T> value)
    {
        await Task.Delay(ms);
        return value();
    }

    [Fact]
    public async Task A_quick_answer_goes_out_as_usual()
    {
        KeepAliveResult<Answer>.Grace = TimeSpan.FromSeconds(2);
        var (ctx, body) = Context();
        await new KeepAliveResult<Answer>(Task.FromResult(new Answer("hi"))).ExecuteResultAsync(ctx);
        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.StartsWith("{", text);
        Assert.Equal("hi", JsonDocument.Parse(text).RootElement.GetProperty("data").GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_refusal_before_the_answer_starts_keeps_its_own_status()
    {
        KeepAliveResult<Answer>.Grace = TimeSpan.FromSeconds(2);
        var (ctx, _) = Context();
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new KeepAliveResult<Answer>(Task.FromException<Answer>(new ForbiddenException("Premium only"))).ExecuteResultAsync(ctx));
    }

    [Fact]
    public async Task A_slow_answer_starts_at_once_with_spaces_then_the_envelope()
    {
        KeepAliveResult<Answer>.Grace = TimeSpan.FromMilliseconds(30);
        KeepAliveResult<Answer>.Beat = TimeSpan.FromMilliseconds(30);
        var (ctx, body) = Context();
        // Held open until the response has kept the connection alive twice, then answered: a fixed delay
        // raced the setup and, on a busy machine, finished first (the answer then goes out the quick way).
        var answer = new TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new KeepAliveResult<Answer>(answer.Task).ExecuteResultAsync(ctx);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Encoding.UTF8.GetString(body.ToArray()).Count(c => c == ' ') < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
        answer.SetResult(new Answer("done"));
        await run;
        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.StartsWith(" ", text);
        Assert.True(text.TakeWhile(c => c == ' ').Count() >= 2, "it kept the connection alive more than once");
        Assert.Equal(200, ctx.HttpContext.Response.StatusCode);
        var json = JsonDocument.Parse(text).RootElement;
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("done", json.GetProperty("data").GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_failure_after_the_start_is_told_in_the_body()
    {
        KeepAliveResult<Answer>.Grace = TimeSpan.FromMilliseconds(30);
        KeepAliveResult<Answer>.Beat = TimeSpan.FromMilliseconds(30);
        var (ctx, body) = Context();
        // Well after the grace period, even on a busy machine (at 120 ms a loaded test run sometimes fired the
        // grace timer late, so the failure arrived first and took the quick-answer path).
        await new KeepAliveResult<Answer>(Later<Answer>(800, () => throw new BusinessRuleException("You've used this month's AI allowance."))).ExecuteResultAsync(ctx);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(body.ToArray())).RootElement;
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Contains("allowance", json.GetProperty("message").GetString());
    }
}
