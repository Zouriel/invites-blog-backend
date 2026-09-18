using System.Text.Json;
using FluentValidation;
using InvitesBlog.Application.Dtos.Campaigns;

namespace InvitesBlog.Application.Validation.Campaigns;

public sealed class UpdateDeliverySettingsRequestValidator : AbstractValidator<UpdateDeliverySettingsRequest>
{
    // "share" = give the inviter the link to pass on (the open link, or the OTP-gated /e/{id});
    // "email" = also mail every guest their own /i/{token} link. Viber/whatsapp/telegram/sms are
    // disabled for now. Read back by Delivery.DeliverySettings, for every send.
    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { "email", "share" };

    public UpdateDeliverySettingsRequestValidator()
    {
        RuleFor(x => x.DeliverySettingsJson).NotEmpty();

        RuleFor(x => x.DeliverySettingsJson)
            .Must(BeValidJson).WithMessage("Delivery settings must be valid JSON.")
            .Must(HaveOnlyAllowedChannels)
            // Name the channels that are actually allowed. This used to say "viber, email, direct",
            // two of which have never been accepted, so the one message a caller gets when they get
            // this wrong sent them looking for channels that do not exist.
            .WithMessage("Delivery channels must be one of: email, share.");
    }

    private static bool BeValidJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { using var _ = JsonDocument.Parse(json); return true; }
        catch (JsonException) { return false; }
    }

    private static bool HaveOnlyAllowedChannels(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return true; // defaults are fine
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return true; } // BeValidJson already reports the parse failure

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return true;

            if (root.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in channels.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String && !Allowed.Contains(c.GetString() ?? ""))
                        return false;
            }

            if (root.TryGetProperty("fallbackChannel", out var fb) && fb.ValueKind == JsonValueKind.String)
                if (!Allowed.Contains(fb.GetString() ?? "")) return false;

            return true;
        }
    }
}
