namespace InvitesBlog.Domain.Authorization;

/// <summary>
/// The single source of truth for every permission string. Controllers reference these via
/// <c>[HasPermission(...)]</c>; the permission seeder materializes them and assigns them to roles.
/// Naming is "entity.action" (spec §Roles and Permission Seeders — consistent naming, no hardcoding).
/// </summary>
public static class Permissions
{
    public static class Templates
    {
        public const string Read = "templates.read";
        public const string Manage = "templates.manage";   // admin create/publish/unpublish
    }

    public static class Designer
    {
        public const string Manage = "designer.manage";
        public const string Review = "designer.review";     // admin submission queue
    }

    public static class Campaigns
    {
        public const string Create = "campaigns.create";
        public const string Read = "campaigns.read";
        public const string Write = "campaigns.write";
        public const string Delete = "campaigns.delete";
        public const string Checkout = "campaigns.checkout";
        public const string Cancel = "campaigns.cancel";
        public const string Dispatch = "campaigns.dispatch";
    }

    public static class Guests
    {
        public const string Read = "guests.read";
        public const string Upload = "guests.upload";
        public const string Write = "guests.write";
        public const string Resend = "guests.resend";
    }

    public static class Payments
    {
        public const string Read = "payments.read";
        public const string Refund = "payments.refund";
    }

    public static class Invites
    {
        public const string View = "invites.view";          // public token view
        public const string Rsvp = "invites.rsvp";
        public const string Claim = "invites.claim";
    }

    public static class Inbox
    {
        public const string Read = "inbox.read";
    }

    public static class Otp
    {
        public const string Request = "otp.request";
        public const string Verify = "otp.verify";
    }

    public static class Privacy
    {
        public const string Remove = "privacy.remove";
    }

    public static class Dashboard
    {
        public const string Read = "dashboard.read";
    }

    public static class Admin
    {
        public const string Access = "admin.access";
        public const string ManageUsers = "admin.users.manage";
        public const string ManageSuppression = "admin.suppression.manage";
        public const string ReadAudit = "admin.audit.read";
    }

    /// <summary>Every permission with its group + human description, for the seeder.</summary>
    public static IReadOnlyList<(string Name, string Group, string Description)> All { get; } = new[]
    {
        (Templates.Read, "templates", "Browse active templates"),
        (Templates.Manage, "templates", "Create and publish platform templates"),
        (Designer.Manage, "designer", "Create and submit custom templates"),
        (Designer.Review, "designer", "Review community template submissions"),
        (Campaigns.Create, "campaigns", "Create a campaign"),
        (Campaigns.Read, "campaigns", "Read a campaign"),
        (Campaigns.Write, "campaigns", "Edit a campaign"),
        (Campaigns.Delete, "campaigns", "Delete a campaign and its data"),
        (Campaigns.Checkout, "campaigns", "Start checkout / top-up"),
        (Campaigns.Cancel, "campaigns", "Cancel a campaign"),
        (Campaigns.Dispatch, "campaigns", "Dispatch invites"),
        (Guests.Read, "guests", "Read guests"),
        (Guests.Upload, "guests", "Upload a guest list"),
        (Guests.Write, "guests", "Add or edit guests"),
        (Guests.Resend, "guests", "Resend an invite"),
        (Payments.Read, "payments", "Read payments"),
        (Payments.Refund, "payments", "Issue refunds"),
        (Invites.View, "invites", "View an invite by token"),
        (Invites.Rsvp, "invites", "RSVP to an invite"),
        (Invites.Claim, "invites", "Claim an invite to the inbox"),
        (Inbox.Read, "inbox", "Read the invite inbox"),
        (Otp.Request, "otp", "Request an OTP code"),
        (Otp.Verify, "otp", "Verify an OTP code"),
        (Privacy.Remove, "privacy", "Remove guest data"),
        (Dashboard.Read, "dashboard", "Read the campaign dashboard"),
        (Admin.Access, "admin", "Access the admin area"),
        (Admin.ManageUsers, "admin", "Manage users and roles"),
        (Admin.ManageSuppression, "admin", "Manage the suppression list"),
        (Admin.ReadAudit, "admin", "Read audit logs"),
    };
}

/// <summary>Built-in roles and the permissions each holds. Seeded and non-deletable.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Inviter = "Inviter";       // possession-token principals
    public const string Invitee = "Invitee";       // OTP-JWT principals
    public const string Public = "Public";         // anonymous callers

    public static IReadOnlyDictionary<string, string[]> Definitions { get; } = new Dictionary<string, string[]>
    {
        [Admin] = Permissions.All.Select(p => p.Name).ToArray(), // all permissions

        [Inviter] = new[]
        {
            Permissions.Templates.Read, Permissions.Designer.Manage,
            Permissions.Campaigns.Create, Permissions.Campaigns.Read, Permissions.Campaigns.Write,
            Permissions.Campaigns.Delete, Permissions.Campaigns.Checkout, Permissions.Campaigns.Cancel,
            Permissions.Guests.Read, Permissions.Guests.Upload, Permissions.Guests.Write, Permissions.Guests.Resend,
            Permissions.Payments.Read, Permissions.Dashboard.Read,
        },

        [Invitee] = new[]
        {
            Permissions.Invites.View, Permissions.Invites.Rsvp, Permissions.Invites.Claim,
            Permissions.Inbox.Read,
        },

        [Public] = new[]
        {
            Permissions.Templates.Read,
            Permissions.Invites.View, Permissions.Invites.Rsvp,
            Permissions.Otp.Request, Permissions.Otp.Verify,
            Permissions.Privacy.Remove, Permissions.Dashboard.Read,
        },
    };
}
