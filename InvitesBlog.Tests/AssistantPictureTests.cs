using System.Text.Json;
using InvitesBlog.Application.Services.Designs.Assistant;
using InvitesBlog.Infrastructure.Assistant;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>The assistant's `look` tool hands back a picture of a screen; it reaches Claude as an image in the tool's result.</summary>
public class AssistantPictureTests
{
    private const string Jpeg = "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAAA//EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AN//Z";

    [Fact]
    public void A_picture_in_a_tool_result_is_sent_as_an_image_beside_its_words()
    {
        var message = new AssistantTurnMessage("user", [new AssistantBlock("tool_result", "Screen 2 (scroll 844-1688).", "t1", Data: Jpeg)]);
        var json = JsonSerializer.Serialize(AnthropicAssistantModel.ToParam(message));
        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        var parts = result.GetProperty("content");
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("image", parts[1].GetProperty("type").GetString());
        Assert.Equal("base64", parts[1].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/jpeg", parts[1].GetProperty("source").GetProperty("media_type").GetString());
        Assert.StartsWith("/9j/", parts[1].GetProperty("source").GetProperty("data").GetString());
    }

    [Theory]
    [InlineData("data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAAB", true)]
    [InlineData("data:text/html;base64,PGI+PGI+PGI+PGI+PGI+PGI+PGI+PGI+", false)]
    [InlineData("https://evil.example/pic.jpg?aaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData(null, false)]
    public void Only_a_real_picture_of_a_sensible_size_is_kept(string? image, bool kept) =>
        Assert.Equal(kept, DesignAssistantService.Picture(image) is not null);
}
