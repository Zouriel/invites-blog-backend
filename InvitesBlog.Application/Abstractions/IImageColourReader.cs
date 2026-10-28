using InvitesBlog.Application.Campaigns;

namespace InvitesBlog.Application.Abstractions;

/// <summary>
/// Reads the colours out of a picture, for suggesting a palette that matches an uploaded design.
/// Null for anything it cannot decode: a suggestion is a nicety, never a failure.
/// </summary>
public interface IImageColourReader
{
    ImageColours? Read(byte[] content);
}
