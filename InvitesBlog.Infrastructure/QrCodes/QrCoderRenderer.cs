using InvitesBlog.Application.Abstractions;
using QRCoder;

namespace InvitesBlog.Infrastructure.QrCodes;

/// <inheritdoc cref="IQrCodeRenderer"/>
public sealed class QrCoderRenderer : IQrCodeRenderer
{
    /// <summary>
    /// Error correction level Q — about 25% of the code can be lost and still read.
    ///
    /// <para>High for a reason specific to what this code is for. It is printed on a card that sits
    /// on a table at a party for a whole evening: it gets a drink on it, a thumb over the corner, a
    /// fold, and it is read in low light by a phone held at an angle. L would produce a smaller,
    /// tidier code that stops working the first time somebody spills something on it.</para>
    /// </summary>
    private const QRCodeGenerator.ECCLevel Correction = QRCodeGenerator.ECCLevel.Q;

    public byte[] Png(string content, int pixelsPerModule = 12)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, Correction);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }
}
