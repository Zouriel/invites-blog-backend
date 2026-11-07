using System.Text.Json;
using Anthropic.Models.Messages;
using InvitesBlog.Infrastructure.Assistant;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>The designer assistant decides what to do from Claude's stop reason; it must read the real value.</summary>
public class StopReasonTests
{
    [Theory]
    [InlineData("tool_use")]
    [InlineData("end_turn")]
    [InlineData("refusal")]
    [InlineData("max_tokens")]
    public void Reads_the_stop_reason_from_a_real_response(string reason)
    {
        var json = """{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":"REASON","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}"""
            .Replace("REASON", reason);
        var message = JsonSerializer.Deserialize<Message>(json)!;
        Assert.Equal(reason, AnthropicAssistantModel.StopReasonOf(message));
    }
}
