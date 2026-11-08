using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using InvitesBlog.Application.Services.Designs.Assistant;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Assistant;

/// <summary>
/// The designer assistant's calls to Claude (Messages API, official SDK). One call per step; the
/// tool loop itself runs across HTTP requests in <see cref="DesignAssistantService"/>, because the
/// tools run in the person's browser.
///
/// <para>Cost: the tools and system prompt carry a cache breakpoint and never change, and the
/// request-level cache marker caches the conversation as it grows within an exchange. The model and
/// effort come per account (AssistantOptions.ModelFor).</para>
/// </summary>
public sealed class AnthropicAssistantModel(AssistantOptions options, ILogger<AnthropicAssistantModel> log) : IAssistantModel
{
    private readonly AnthropicClient? client = string.IsNullOrWhiteSpace(options.ApiKey)
        ? null
        : new AnthropicClient { ApiKey = options.ApiKey, Timeout = TimeSpan.FromSeconds(280) };

    private static readonly List<ToolUnion> Tools = AssistantPrompt.Tools.Select((t, i) => (ToolUnion)new Tool
    {
        Name = t.Name,
        Description = t.Description,
        InputSchema = SchemaOf(t.Schema),
        // The last tool closes the cached tools block.
        CacheControl = i == AssistantPrompt.Tools.Count - 1 ? new CacheControlEphemeral() : null,
    }).ToList();

    public bool Configured => client is not null;

    public async Task<AssistantModelReply> CompleteAsync(AssistantModelRequest request, CancellationToken ct = default)
    {
        if (client is null) throw new InvalidOperationException("The assistant has no API key.");
        var o = options;
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = o.MaxTokens,
            System = new List<TextBlockParam> { new() { Text = AssistantPrompt.System(request.Vocabulary), CacheControl = new CacheControlEphemeral() } },
            Tools = Tools,
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Effort = EffortOf(request.Effort) },
            // Caches the conversation up to its last block, so the next step of an exchange reads it back.
            CacheControl = new CacheControlEphemeral(),
            Messages = request.Messages.Select(ToParam).ToList(),
        };

        Message response;
        try
        {
            response = await client.Messages.Create(parameters, ct);
        }
        catch (AnthropicBadRequestException e) when (e.Message.Contains("signature", StringComparison.OrdinalIgnoreCase))
        {
            // A thinking block the API no longer accepts: answer without the earlier reasoning, once.
            log.LogWarning("Designer assistant: thinking blocks refused ({Message}); retrying without them.", e.Message);
            response = await client.Messages.Create(parameters with
            {
                Messages = request.Messages.Select(m => m with { Blocks = m.Blocks.Where(b => b.Type is not ("thinking" or "redacted_thinking")).ToList() })
                    .Where(m => m.Blocks.Count > 0).Select(ToParam).ToList(),
            }, ct);
        }

        var usage = response.Usage;
        log.LogInformation("Designer assistant step ({Model}): stop {Stop}, in {In}, cache write {Write}, cache read {Read}, out {Out}",
            request.Model, response.StopReason?.ToString(), usage.InputTokens, usage.CacheCreationInputTokens ?? 0, usage.CacheReadInputTokens ?? 0, usage.OutputTokens);

        return new AssistantModelReply(
            response.Content.Select(FromBlock).OfType<AssistantBlock>().ToList(),
            StopReasonOf(response),
            new AssistantUsageCount(usage.InputTokens, usage.CacheCreationInputTokens ?? 0, usage.CacheReadInputTokens ?? 0, usage.OutputTokens));
    }

    /// <summary>end_turn, tool_use, max_tokens, refusal… The SDK's enum wrapper prints its raw value
    /// (quoted); serializing it gave "{}", which hid refusals and cut-off replies.</summary>
    public static string StopReasonOf(Message response) =>
        response.StopReason?.ToString()?.Trim('"') is { Length: > 0 } r ? r : "end_turn";

    private static Effort EffortOf(string effort) => effort switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        _ => Effort.Low,
    };

    public static MessageParam ToParam(AssistantTurnMessage m)
    {
        var blocks = new List<ContentBlockParam>();
        foreach (var b in m.Blocks)
        {
            switch (b.Type)
            {
                case "text" or "context":
                    if (!string.IsNullOrWhiteSpace(b.Text)) blocks.Add(new TextBlockParam { Text = b.Text! });
                    break;
                case "thinking":
                    blocks.Add(new ThinkingBlockParam { Thinking = b.Text ?? "", Signature = b.Signature ?? "" });
                    break;
                case "redacted_thinking":
                    blocks.Add(new RedactedThinkingBlockParam { Data = b.Data ?? "" });
                    break;
                case "tool_use":
                    blocks.Add(new ToolUseBlockParam { ID = b.Id!, Name = b.Name!, Input = InputOf(b.Input) });
                    break;
                case "tool_result" when b.Data is { } picture && picture.StartsWith("data:image/", StringComparison.Ordinal):
                {
                    // A tool that looked at the design: its words and the picture it took.
                    var comma = picture.IndexOf(',');
                    var media = picture[5..picture.IndexOf(';')];
                    var image = new ImageBlockParam
                    {
                        Source = new Base64ImageSource { Data = picture[(comma + 1)..], MediaType = media switch { "image/png" => MediaType.ImagePng, "image/webp" => MediaType.ImageWebP, _ => MediaType.ImageJpeg } },
                    };
                    blocks.Add(new ToolResultBlockParam
                    {
                        ToolUseID = b.Id!, IsError = b.IsError,
                        Content = new List<Block> { new TextBlockParam { Text = string.IsNullOrEmpty(b.Text) ? "(no output)" : b.Text! }, image },
                    });
                    break;
                }
                case "tool_result":
                    blocks.Add(new ToolResultBlockParam { ToolUseID = b.Id!, Content = string.IsNullOrEmpty(b.Text) ? "(no output)" : b.Text, IsError = b.IsError });
                    break;
            }
        }
        return m.Role == "assistant" ? new AssistantMessageParam { Content = blocks } : new UserMessageParam { Content = blocks };
    }

    private static AssistantBlock? FromBlock(ContentBlock block)
    {
        if (block.TryPickText(out var text)) return new AssistantBlock("text", text.Text);
        if (block.TryPickThinking(out var thinking)) return new AssistantBlock("thinking", thinking.Thinking, Signature: thinking.Signature);
        if (block.TryPickRedactedThinking(out var redacted)) return new AssistantBlock("redacted_thinking", Data: redacted.Data);
        if (block.TryPickToolUse(out var tool))
            return new AssistantBlock("tool_use", Id: tool.ID, Name: tool.Name, Input: JsonSerializer.SerializeToElement(tool.Input));
        return null;
    }

    private static Dictionary<string, JsonElement> InputOf(JsonElement? input)
    {
        var result = new Dictionary<string, JsonElement>();
        if (input is { ValueKind: JsonValueKind.Object } o)
            foreach (var p in o.EnumerateObject()) result[p.Name] = p.Value.Clone();
        return result;
    }

    private static InputSchema SchemaOf(JsonElement schema) => new()
    {
        Properties = schema.TryGetProperty("properties", out var props)
            ? props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : new Dictionary<string, JsonElement>(),
        Required = schema.TryGetProperty("required", out var req) ? req.EnumerateArray().Select(r => r.GetString()!).ToList() : null,
    };
}
