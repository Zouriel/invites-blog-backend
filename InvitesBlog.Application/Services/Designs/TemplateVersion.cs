namespace InvitesBlog.Application.Services.Designs;

/// <summary>Template version numbers: each publish of a design bumps the patch component.</summary>
public static class TemplateVersion
{
    /// <summary>Bumps the patch component: <c>1.0.0</c> → <c>1.0.1</c>. Anything unparseable restarts at 1.0.1.</summary>
    public static string Next(string current)
    {
        var parts = (current ?? string.Empty).Split('.');
        if (parts.Length == 3 && int.TryParse(parts[2], out var patch))
            return $"{parts[0]}.{parts[1]}.{patch + 1}";
        return "1.0.1";
    }
}
