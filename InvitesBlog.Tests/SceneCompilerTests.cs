using InvitesBlog.TemplateCompiler;
using Xunit;

namespace InvitesBlog.Tests;

public class SceneCompilerTests
{
    private static Scene SampleScene() => new()
    {
        Slug = "golden-envelope",
        Version = "1.0.0",
        Name = "Golden Envelope",
        Sections =
        {
            new SceneSection
            {
                Id = "hero", Type = "hero",
                Content = new() { ["title"] = "{{event.title}}", ["subtitle"] = "Dear {{guest.name}}" }
            },
            new SceneSection
            {
                Id = "maleDress", Type = "dressCode", Reveal = "curtain",
                Content = new() { ["heading"] = "Dress code", ["body"] = "Formal suits." },
                Visibility = new SceneVisibility { Type = "block", Block = "maleDressCode" }
            }
        }
    };

    [Fact]
    public void Compile_extracts_variables_from_content()
    {
        var pkg = new SceneCompiler().Compile(SampleScene());
        Assert.Contains("event.title", pkg.Manifest.Variables);
        Assert.Contains("guest.name", pkg.Manifest.Variables);
    }

    [Fact]
    public void Compile_collects_content_blocks_from_gated_sections()
    {
        var pkg = new SceneCompiler().Compile(SampleScene());
        Assert.Contains("maleDressCode", pkg.Manifest.ContentBlocks);
    }

    [Fact]
    public void Compiled_html_binds_via_data_var_and_never_inlines_guest_content()
    {
        var pkg = new SceneCompiler().Compile(SampleScene());
        Assert.Contains("data-var=\"event.title\"", pkg.IndexHtml);
        Assert.Contains("data-var=\"guest.name\"", pkg.IndexHtml);
        // gated section carries a data-block hook the injector filters on
        Assert.Contains("data-block=\"maleDressCode\"", pkg.IndexHtml);
    }

    [Fact]
    public void Injector_uses_textContent_not_innerHTML()
    {
        var pkg = new SceneCompiler().Compile(SampleScene());
        Assert.Contains("textContent", pkg.TemplateJs);
        Assert.DoesNotContain("innerHTML", pkg.TemplateJs);
    }

    [Fact]
    public void Template_ships_reduced_motion_variant()
    {
        var pkg = new SceneCompiler().Compile(SampleScene());
        Assert.Contains("prefers-reduced-motion", pkg.StylesCss);
    }

    [Fact]
    public void Small_template_is_within_budget()
    {
        var pkg = new SceneCompiler().Compile(SampleScene());
        Assert.False(pkg.OverBudget);
        Assert.True(pkg.CriticalPathBytes < SceneCompiler.CriticalPathBudgetBytes);
    }
}
