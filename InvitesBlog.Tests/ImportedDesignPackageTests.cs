using System.IO.Compression;
using System.Text;
using InvitesBlog.Application.Designs;
using InvitesBlog.Application.Exceptions;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Unpacking a design somebody uploaded.
///
/// <para>This is the one place in the product that takes an archive from an account that needed no
/// review to open and writes its contents to storage under our name. Every test here is a refusal,
/// because the interesting behaviour of this code is what it declines to do.</para>
/// </summary>
public class ImportedDesignPackageTests
{
    private static byte[] Zip(params (string Path, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private static (string, byte[]) File(string path, string content = "x") =>
        (path, Encoding.UTF8.GetBytes(content));

    // ---------- what it accepts ----------

    [Fact]
    public void Takes_the_document_and_its_assets()
    {
        var unpacked = ImportedDesignPackage.Unpack(Zip(
            File("index.html", "<h1>Amira &amp; Yusuf</h1>"),
            File("images/bg.png"),
            File("style.css")));

        Assert.Equal("index.html", unpacked.Document.Path);
        Assert.Equal(2, unpacked.Assets.Count);
    }

    /// <summary>
    /// Design tools bury their output a folder deep about half the time. Making somebody re-zip
    /// their own export to satisfy us is not a real requirement.
    /// </summary>
    [Fact]
    public void Finds_the_document_when_the_export_is_nested()
    {
        var unpacked = ImportedDesignPackage.Unpack(Zip(
            File("My Design/page.html"),
            File("My Design/assets/bg.png")));

        Assert.Equal("My Design/page.html", unpacked.Document.Path);
    }

    /// <summary>A root index.html wins over a shallower-sorted sibling document.</summary>
    [Fact]
    public void Prefers_index_html_at_the_root()
    {
        var unpacked = ImportedDesignPackage.Unpack(Zip(
            File("a.html"),
            File("index.html")));

        Assert.Equal("index.html", unpacked.Document.Path);
    }

    // ---------- what it refuses ----------

    /// <summary>
    /// Zip slip. A member may call itself <c>../../etc/passwd</c>, and a naive extractor writes
    /// exactly there. The backslash form matters as much: a zip written on Windows uses them, and a
    /// check that only knows about '/' reads <c>..\\..\\x</c> as an ordinary filename.
    /// </summary>
    [Theory]
    [InlineData("../escape.html")]
    [InlineData("a/../../escape.html")]
    [InlineData("..\\..\\escape.html")]
    [InlineData("/etc/passwd.html")]
    public void Refuses_a_path_that_climbs_out(string path)
    {
        var e = Assert.Throws<BusinessRuleException>(
            () => ImportedDesignPackage.Unpack(Zip(File(path))));
        Assert.Equal("design_bad_path", e.ErrorCode);
    }

    [Fact]
    public void Refuses_a_file_type_a_design_has_no_use_for()
    {
        var e = Assert.Throws<BusinessRuleException>(
            () => ImportedDesignPackage.Unpack(Zip(File("index.html"), File("shell.php"))));
        Assert.Equal("design_bad_file_type", e.ErrorCode);
    }

    /// <summary>
    /// The bomb guard is on what an entry EXPANDS to, not on the zip's own size — a few hundred
    /// kilobytes of zip can claim to be a hundred gigabytes, and it is the writing that hurts.
    /// </summary>
    [Fact]
    public void Refuses_an_entry_that_expands_past_the_ceiling()
    {
        var huge = new byte[ImportedDesignPackage.MaxEntryBytes + 1024];
        var e = Assert.Throws<BusinessRuleException>(
            () => ImportedDesignPackage.Unpack(Zip(File("index.html"), ("big.png", huge))));
        Assert.Equal("design_file_too_big", e.ErrorCode);
    }

    [Fact]
    public void Refuses_a_zip_with_no_page_in_it()
    {
        var e = Assert.Throws<BusinessRuleException>(
            () => ImportedDesignPackage.Unpack(Zip(File("bg.png"), File("style.css"))));
        Assert.Equal("design_no_document", e.ErrorCode);
    }

    [Fact]
    public void Refuses_something_that_is_not_a_zip()
    {
        var e = Assert.Throws<BusinessRuleException>(
            () => ImportedDesignPackage.Unpack(Encoding.UTF8.GetBytes("this is not a zip")));
        Assert.Equal("design_not_a_zip", e.ErrorCode);
    }

    [Fact]
    public void Refuses_an_empty_zip()
    {
        var e = Assert.Throws<BusinessRuleException>(() => ImportedDesignPackage.Unpack(Zip()));
        Assert.Equal("design_empty", e.ErrorCode);
    }

    // ---------- rewriting ----------

    /// <summary>
    /// Once assets are in storage under a campaign's own prefix, every relative reference in the
    /// document is wrong. References appear in attributes and inside `url()`, so both shapes move.
    /// </summary>
    [Fact]
    public void Points_references_at_where_the_assets_actually_went()
    {
        var html = """<img src="images/bg.png"><div style="background:url(images/bg.png)"></div>""";
        var rewritten = ImportedDesignPackage.Rewrite(
            html, new Dictionary<string, string> { ["images/bg.png"] = "/assets/campaigns/x/design/images/bg.png" });

        Assert.DoesNotContain("\"images/bg.png\"", rewritten);
        Assert.DoesNotContain("(images/bg.png)", rewritten);
        Assert.Equal(2, rewritten.Split("/assets/campaigns/x/design/images/bg.png").Length - 1);
    }

    /// <summary>
    /// Longest path first, or the shorter name corrupts the longer path it sits inside —
    /// <c>bg.png</c> would otherwise rewrite the middle of <c>images/bg.png</c>.
    /// </summary>
    [Fact]
    public void Rewrites_the_longer_path_before_the_name_inside_it()
    {
        var html = """<img src="images/bg.png"><img src="bg.png">""";
        var rewritten = ImportedDesignPackage.Rewrite(html, new Dictionary<string, string>
        {
            ["bg.png"] = "/a/bg.png",
            ["images/bg.png"] = "/a/images/bg.png",
        });

        Assert.Contains("\"/a/images/bg.png\"", rewritten);
        Assert.Contains("\"/a/bg.png\"", rewritten);
        Assert.DoesNotContain("/a/images//a/bg.png", rewritten);
    }
}
