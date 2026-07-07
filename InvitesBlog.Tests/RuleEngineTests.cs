using InvitesBlog.Application.Rules;
using Xunit;

namespace InvitesBlog.Tests;

public class RuleEngineTests
{
    private readonly RuleEngine _engine = new();

    private const string Rules = """
    {
      "rules": [
        { "condition": { "field": "role",   "operator": "equals", "value": "bridesmaid" }, "contentBlock": "bridesmaidInstructions" },
        { "condition": { "field": "gender", "operator": "equals", "value": "male" },       "contentBlock": "maleDressCode" },
        { "condition": { "field": "gender", "operator": "equals", "value": "female" },     "contentBlock": "femaleDressCode" }
      ]
    }
    """;

    private static readonly string[] AllBlocks =
        { "welcome", "schedule", "bridesmaidInstructions", "maleDressCode", "femaleDressCode" };

    [Fact]
    public void Bridesmaid_sees_bridesmaid_block()
    {
        var blocks = _engine.Resolve(Rules,
            new Dictionary<string, string?> { ["role"] = "bridesmaid", ["gender"] = "female" }, AllBlocks);
        Assert.Contains("bridesmaidInstructions", blocks);
        Assert.Contains("femaleDressCode", blocks);
        Assert.DoesNotContain("maleDressCode", blocks);
    }

    [Fact]
    public void Male_guest_sees_male_dress_code_only()
    {
        var blocks = _engine.Resolve(Rules,
            new Dictionary<string, string?> { ["gender"] = "male" }, AllBlocks);
        Assert.Contains("maleDressCode", blocks);
        Assert.DoesNotContain("femaleDressCode", blocks);
        Assert.DoesNotContain("bridesmaidInstructions", blocks);
    }

    [Fact]
    public void Unspecified_guest_still_sees_all_neutral_blocks()
    {
        // §12.4: unspecified guests must see a complete invite (neutral defaults always render).
        var blocks = _engine.Resolve(Rules,
            new Dictionary<string, string?> { ["gender"] = "unspecified" }, AllBlocks);
        Assert.Contains("welcome", blocks);
        Assert.Contains("schedule", blocks);
        Assert.DoesNotContain("maleDressCode", blocks);
        Assert.DoesNotContain("femaleDressCode", blocks);
        Assert.DoesNotContain("bridesmaidInstructions", blocks);
    }

    [Fact]
    public void In_operator_matches_any_listed_value()
    {
        const string rules = """
        { "rules": [ { "condition": { "field": "role", "operator": "in", "value": ["vip","speaker"] }, "contentBlock": "vipSchedule" } ] }
        """;
        Assert.Contains("vipSchedule", _engine.Resolve(rules,
            new Dictionary<string, string?> { ["role"] = "speaker" }));
        Assert.DoesNotContain("vipSchedule", _engine.Resolve(rules,
            new Dictionary<string, string?> { ["role"] = "family" }));
    }

    [Fact]
    public void Empty_rules_returns_only_neutral_blocks()
    {
        var blocks = _engine.Resolve("", new Dictionary<string, string?>(), AllBlocks);
        Assert.Equal(AllBlocks.Length, blocks.Count);
    }
}
