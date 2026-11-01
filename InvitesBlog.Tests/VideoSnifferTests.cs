using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Common;
using InvitesBlog.Infrastructure.Video;
using Xunit;

namespace InvitesBlog.Tests;

public class VideoSnifferTests
{
    private static byte[] Box(string type, string brand = "") =>
        [0, 0, 0, 0x20, .. System.Text.Encoding.ASCII.GetBytes(type + brand), .. new byte[20]];

    [Theory]
    [InlineData("isom", "video/mp4")]
    [InlineData("mp42", "video/mp4")]
    [InlineData("3gp4", "video/mp4")]
    [InlineData("qt  ", "video/quicktime")]
    public void Iso_files_are_named_by_their_brand(string brand, string type) =>
        Assert.Equal(type, VideoSniffer.Detect(Box("ftyp", brand))?.ContentType);

    [Fact]
    public void An_old_quicktime_opens_on_an_ordinary_box() =>
        Assert.Equal("video/quicktime", VideoSniffer.Detect(Box("moov"))?.ContentType);

    [Fact]
    public void Webm_and_matroska_are_told_apart_by_their_doctype()
    {
        byte[] ebml = [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x82, 0x84];
        Assert.Equal("video/webm", VideoSniffer.Detect([.. ebml, .. "webm"u8])?.ContentType);
        Assert.Equal("video/x-matroska", VideoSniffer.Detect([.. ebml, .. "matroska"u8])?.ContentType);
    }

    [Theory]
    [InlineData("heic")]
    [InlineData("avif")]
    [InlineData("mif1")]
    public void A_still_in_the_same_box_is_not_a_video(string brand) => Assert.Null(VideoSniffer.Detect(Box("ftyp", brand)));

    [Theory]
    [InlineData("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:0\nfile:///etc/passwd")]
    [InlineData("<html><script>alert(1)</script></html>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("")]
    public void Anything_else_is_refused_and_never_reaches_ffmpeg(string text) =>
        Assert.Null(VideoSniffer.Detect(System.Text.Encoding.UTF8.GetBytes(text)));
}

public class VideoTrimRulesTests
{
    private static VideoProbe P(string codec = "h264", int w = 1920, int h = 1080, long rate = 20_000_000, double fps = 30, bool hdr = false, bool audio = true, double seconds = 60) =>
        new(codec, w, h, seconds, rate, fps, audio, hdr);
    private const long Big = 150 * MediaLimits.Mb;

    [Fact]
    public void A_heavy_1080p_clip_is_compressed() => Assert.Null(VideoTrimRules.WhyNotCompress(P(), Big));

    [Fact]
    public void Four_k_is_compressed_even_if_efficient() =>
        Assert.Null(VideoTrimRules.WhyNotCompress(P("hevc", 3840, 2160, 1_000_000), Big));

    [Fact]
    public void A_file_already_small_is_left_alone() =>
        Assert.Equal("already small", VideoTrimRules.WhyNotCompress(P(), VideoTrimRules.SmallEnoughBytes - 1));

    [Fact]
    public void One_at_the_size_line_is_considered() => Assert.Null(VideoTrimRules.WhyNotCompress(P(), VideoTrimRules.SmallEnoughBytes));

    [Fact]
    public void An_efficient_1080p_file_is_left_alone() =>
        Assert.Equal("already efficient", VideoTrimRules.WhyNotCompress(P(rate: 3_000_000), Big));

    [Fact]
    public void An_old_codec_is_compressed_however_low_its_rate() =>
        Assert.Null(VideoTrimRules.WhyNotCompress(P("mpeg4", rate: 3_000_000), Big));

    [Fact]
    public void Hdr_is_left_alone_so_it_does_not_wash_out() =>
        Assert.Equal("hdr", VideoTrimRules.WhyNotCompress(P(hdr: true), Big));

    [Theory]
    [InlineData(100, 90, true)]
    [InlineData(100, 91, false)]
    [InlineData(100, 120, false)]
    public void Only_a_real_saving_is_kept(long old, long made, bool keep) => Assert.Equal(keep, VideoTrimRules.WorthKeeping(old, made));

    [Fact]
    public void A_copy_must_be_the_same_video()
    {
        var o = P(w: 1080, h: 1920);
        Assert.True(VideoTrimRules.Matches(o, P(w: 1080, h: 1920)));
        Assert.False(VideoTrimRules.Matches(o, P(w: 1920, h: 1080)), "turned on its side");
        Assert.False(VideoTrimRules.Matches(o, P(w: 1080, h: 1920, audio: false)), "lost its sound");
        Assert.False(VideoTrimRules.Matches(o, P(w: 1080, h: 1920, seconds: 40)), "cut short");
        Assert.False(VideoTrimRules.Matches(o, P("hevc", 1080, 1920)), "not what was asked for");
        Assert.False(VideoTrimRules.Matches(P(w: 640, h: 360), P(w: 1280, h: 720)), "made larger");
        Assert.False(VideoTrimRules.Matches(P(w: 3840, h: 2160), P(w: 3840, h: 2160)), "over the cap");
    }
}

public class VideoProbeReadingTests
{
    [Fact]
    public void Rotation_swaps_what_is_displayed()
    {
        var p = FfmpegVideoTools.Read("""
        {"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"30000/1001",
          "side_data_list":[{"side_data_type":"Display Matrix","rotation":-90}]},
          {"codec_type":"audio","codec_name":"aac"}],
         "format":{"duration":"12.5","bit_rate":"1000"}}
        """)!;
        Assert.Equal((1080, 1920), (p.Width, p.Height));
        Assert.True(p.HasAudio);
        Assert.Equal(12.5, p.Seconds);
        Assert.Equal(29.97, p.Fps, 2);
    }

    [Fact]
    public void Cover_art_is_not_the_video() =>
        Assert.Null(FfmpegVideoTools.Read("""{"streams":[{"codec_type":"video","codec_name":"mjpeg","width":10,"height":10,"disposition":{"attached_pic":1}}]}"""));

    [Fact]
    public void Sound_alone_is_not_a_video() =>
        Assert.Null(FfmpegVideoTools.Read("""{"streams":[{"codec_type":"audio","codec_name":"aac"}]}"""));

    [Theory]
    [InlineData("smpte2084", true)]
    [InlineData("arib-std-b67", true)]
    [InlineData("bt709", false)]
    public void Hdr_is_read_from_the_transfer(string transfer, bool hdr) =>
        Assert.Equal(hdr, FfmpegVideoTools.Read($$"""{"streams":[{"codec_type":"video","codec_name":"hevc","width":10,"height":10,"color_transfer":"{{transfer}}"}]}""")!.IsHdr);
}
