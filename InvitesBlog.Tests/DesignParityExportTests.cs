using System.Text.Json;
using System.Text.Json.Nodes;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Infrastructure.Templates;
using InvitesBlog.TemplateCompiler;
using InvitesBlog.TemplateCompiler.Design;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Writes what the server makes of a corpus of scenes, for the browser renderer's parity tests
/// (web-inviter <c>pages/designer/render/parity.spec.ts</c>). The editor previews with a TypeScript port
/// of <see cref="DesignCompiler"/>; this is the reference it is held to.
///
/// <para>Runs only when asked: <c>DESIGN_PARITY_SCENES</c> names a JSON array of <c>{ name, scene }</c>
/// and <c>DESIGN_PARITY_OUT</c> where to write. The starters are always included.</para>
/// </summary>
public class DesignParityExportTests
{
    [Fact]
    public void Export_parity_outputs()
    {
        var output = Environment.GetEnvironmentVariable("DESIGN_PARITY_OUT");
        if (string.IsNullOrEmpty(output)) return;
        var input = Environment.GetEnvironmentVariable("DESIGN_PARITY_SCENES");

        var engine = new DesignEngine(
            new RawTemplatePackager(Substitute.For<IStorageService>()),
            Substitute.For<IImageOptimizer>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Urls:AssetsBase"] = "/assets" }).Build());

        var corpus = new List<(string Name, string Json)>();
        foreach (var starter in DesignStarters.All) corpus.Add(($"starter-{starter.Id}", DesignStarters.Create(starter.Id)!.ToJson()));
        if (!string.IsNullOrEmpty(input))
            foreach (var item in JsonNode.Parse(File.ReadAllText(input))!.AsArray())
                corpus.Add((item!["name"]!.GetValue<string>(), item["scene"]!.ToJsonString()));

        var cases = new JsonArray();
        foreach (var (name, json) in corpus)
        {
            var entry = new JsonObject { ["name"] = name, ["scene"] = JsonNode.Parse(json) };
            DesignScene scene;
            try { scene = DesignScene.Parse(json); }
            catch (DesignSceneException e)
            {
                entry["error"] = e.Message;
                cases.Add(entry);
                continue;
            }

            var firstId = scene.Elements.FirstOrDefault()?.Id;
            entry["published"] = DesignCompiler.Compile(scene, new DesignCompileOptions { FontBaseUrl = "/assets/fonts/" });

            var previews = new JsonArray();
            (bool Editor, string Sample, string[]? Blocks, string[] Hidden, double Scroll)[] variants =
            [
                (true, "filled", null, [], 123.4567),
                (false, "empty", ["family", "bridesmaids"], firstId is null ? [] : [firstId], 0),
                (true, "roles", [], [], 99999.00049),
            ];
            foreach (var v in variants)
            {
                var build = engine.Build(json, new DesignBuildOptions(
                    EditorPreview: v.Editor, Sample: v.Sample, Blocks: v.Blocks, Hidden: v.Hidden.ToHashSet(StringComparer.Ordinal), Scroll: v.Scroll));
                previews.Add(new JsonObject
                {
                    ["editor"] = v.Editor,
                    ["sample"] = v.Sample,
                    ["blocks"] = v.Blocks is null ? null : new JsonArray(v.Blocks.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray()),
                    ["hidden"] = new JsonArray(v.Hidden.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
                    ["scroll"] = v.Scroll,
                    ["html"] = build.Html,
                    ["bytes"] = build.Bytes,
                });
            }
            entry["previews"] = previews;
            cases.Add(entry);
        }

        var result = new JsonObject
        {
            ["catalog"] = JsonSerializer.SerializeToNode(engine.Catalog()),
            ["cases"] = cases,
        };
        File.WriteAllText(output, result.ToJsonString());
    }
}
