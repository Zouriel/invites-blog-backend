using System.Text.Json;
using FluentValidation;
using InvitesBlog.Application.Dtos.Campaigns;

namespace InvitesBlog.Application.Validation.Campaigns;

public sealed class UpdateDeliverySettingsRequestValidator : AbstractValidator<UpdateDeliverySettingsRequest>
{
    // Current mechanism: a single OTP-gated share link (/e/{id}). "share" = give the inviter the link;
    // "email" = also mail that link to guests. Viber/whatsapp/telegram/sms are disabled for now.
    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { "email", "share" };

    public UpdateDeliverySettingsRequestValidator()
    {
        RuleFor(x => x.DeliverySettingsJson).NotEmpty();

        RuleFor(x => x.DeliverySettingsJson)
            .Must(BeValidJson).WithMessage("Delivery settings must be valid JSON.")
            .Must(HaveOnlyAllowedChannels)
            .WithMessage("Delivery channels must be one of: viber, email, direct.");
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
