namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A setting an admin changes at runtime, stored as JSON under a key — prices, for one. Code holds
/// the defaults; a row here only exists once somebody has changed them.
/// </summary>
public class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}
