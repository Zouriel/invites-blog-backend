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

    /// <summary>The visual template designer. Anyone signed in can build templates in it.</summary>
    public static class Designs
    {
        public const string Manage = "designs.manage";
        /// <summary>Handle reports, and unlist or remove gallery templates.</summary>
        public const string Moderate = "designs.moderate";
    }

    public static class Designer
    {
        public const string Manage = "designer.manage";
        /// <summary>Admin: manage designer accounts — the list, and suspending or reinstating one.</summary>
        public const string Review = "designer.review";
    }

    public static class Campaigns
    {
        public const string Create = "campaigns.create";
        public const string Read = "campaigns.read";
        public const string Write = "campaigns.write";
        public const string Delete = "campaigns.delete";
        public const string Checkout = "campaigns.checkout";
        public const string Cancel = "campaigns.cancel";
    }

    public static class Guests
    {
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
    }

    public static class Inbox
    {
        public const string Read = "inbox.read";
    }

    /// <summary>
    /// The event photo box (§5). Read and Upload are GUEST rights — the people at the party are the
    /// ones with cameras. Moderate is the host's, and is what "delete someone else's photo" needs;
    /// removing your OWN photo is checked against the uploader, not against this.
    /// </summary>
    public static class Photos
    {
        public const string Read = "photos.read";
        public const string Upload = "photos.upload";
        public const string Moderate = "photos.moderate";
    }

    /// <summary>
    /// Media buckets (§5). <see cref="Buckets.Manage"/> is the OWNER's right — rename, re-cover,
    /// set its window, and hand out or revoke a contribution code. Like every other permission here it says
    /// "may do this KIND of thing", never "may do it to THIS bucket": which bucket is decided by
    /// ownership, checked separately on every call.
    ///
    /// <para>There is deliberately no "contribute" permission. Somebody who scanned a printed code
    /// holds no role and has no session — the token IS their authorization, and it authorizes one
    /// action on one bucket. A permission would imply an identity they do not have.</para>
    /// </summary>
    public static class Buckets
    {
        public const string Read = "buckets.read";
        public const string Manage = "buckets.manage";
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
        public const string ManageUsers = "admin.users.manage";
        public const string ManageSuppression = "admin.suppression.manage";
        public const string ReadAudit = "admin.audit.read";
        public const string ManagePrices = "admin.prices.manage";
    }

    /// <summary>Every permission with its group + human description, for the seeder.</summary>
    public static IReadOnlyList<(string Name, string Group, string Description)> All { get; } = new[]
    {
        (Templates.Read, "templates", "Browse active templates"),
        (Templates.Manage, "templates", "Create and publish platform templates"),
        (Designer.Manage, "designer", "Manage their own published templates"),
        (Designs.Manage, "designs", "Build and publish templates in the designer"),
        (Designs.Moderate, "designs", "Handle template reports; unlist or remove gallery templates"),
        (Designer.Review, "designer", "Manage designer accounts"),
        (Campaigns.Create, "campaigns", "Create a campaign"),
        (Campaigns.Read, "campaigns", "Read a campaign"),
        (Campaigns.Write, "campaigns", "Edit a campaign"),
        (Campaigns.Delete, "campaigns", "Delete a campaign and its data"),
        (Campaigns.Checkout, "campaigns", "Start checkout / top-up"),
        (Campaigns.Cancel, "campaigns", "Cancel a campaign"),
        (Guests.Upload, "guests", "Upload a guest list"),
        (Guests.Write, "guests", "Add or edit guests"),
        (Guests.Resend, "guests", "Resend an invite"),
        (Payments.Read, "payments", "Read payments"),
        (Payments.Refund, "payments", "Issue refunds"),
        (Invites.View, "invites", "View an invite by token"),
        (Invites.Rsvp, "invites", "RSVP to an invite"),
        (Inbox.Read, "inbox", "Read the invite inbox"),
        (Photos.Read, "photos", "See an event's photo box"),
        (Photos.Upload, "photos", "Add a photo to an event"),
        (Photos.Moderate, "photos", "Remove any photo from an event"),
        (Buckets.Read, "albums", "See your albums"),
        (Buckets.Manage, "albums", "Create, name and share an album"),
        (Otp.Request, "otp", "Request an OTP code"),
        (Otp.Verify, "otp", "Verify an OTP code"),
        (Privacy.Remove, "privacy", "Remove guest data"),
        (Dashboard.Read, "dashboard", "Read the campaign dashboard"),
        (Admin.ManageUsers, "admin", "Manage users and roles"),
        (Admin.ManagePrices, "admin", "Change what plans, passes and sending cost"),
        (Admin.ManageSuppression, "admin", "Manage the suppression list"),
        (Admin.ReadAudit, "admin", "Read audit logs"),
    };
}

/// <summary>Built-in roles and the permissions each holds. Seeded and non-deletable.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Designer = "Designer";     // community template authors (account-backed)
    public const string Customer = "Customer";     // account-backed inviters: their own history
    public const string Inviter = "Inviter";       // possession-token principals
    public const string Invitee = "Invitee";       // OTP-JWT principals
    public const string Public = "Public";         // anonymous callers


    /// <summary>
    /// The roles an admin may grant or take away by hand.
    ///
    /// <para><b>Not simply "all of them".</b> <see cref="Inviter"/>, <see cref="Invitee"/> and
    /// <see cref="Public"/> are not memberships anybody holds — they describe how a caller ARRIVED
    /// (a possession token, a one-time code, nothing at all) and are attached to a request rather
    /// than to an account. Granting one to a user would put a row in the table that no sign-in ever
    /// reads, which looks like it worked and does nothing.</para>
    /// </summary>
    // Designer is not granted by hand: it comes with the Studio plan (DesignerAccessService).
    public static IReadOnlyList<string> Grantable { get; } = [Admin, Customer];

    public static IReadOnlyDictionary<string, string[]> Definitions { get; } = new Dictionary<string, string[]>
    {
        [Admin] = Permissions.All.Select(p => p.Name).ToArray(), // all permissions

        // A designer makes templates in the designer and publishes them. Managing designers is an
        // ADMIN act (Designer.Review), deliberately withheld here.
        // They also get the customer's read permissions: a designer is a person who receives
        // invitations too, and their account page would 403 without them.
        [Designer] = new[]
        {
            Permissions.Templates.Read, Permissions.Designer.Manage, Permissions.Designs.Manage,
            Permissions.Dashboard.Read, Permissions.Campaigns.Read, Permissions.Inbox.Read,
            Permissions.Photos.Read, Permissions.Buckets.Read,
        },

        // A signed-in customer is the same person as an Inviter — the difference is only which key
        // they arrived with, an account instead of the emailed link. So they hold the same campaign
        // permissions: this is what lets someone open a campaign from Sent and actually run it
        // (add a guest, resend, cancel) without digging the original email out.
        //
        // These permissions say "may do this KIND of thing", never "may do it to THIS campaign".
        // Every campaign-scoped action re-checks ownership through ICampaignOwnershipService, so a
        // customer still cannot touch a campaign that isn't theirs. Designer.Manage and Designs.Manage
        // are withheld: making and publishing templates is the Designer role's.
        [Customer] = new[]
        {
            Permissions.Templates.Read, Permissions.Dashboard.Read, Permissions.Inbox.Read,
            Permissions.Campaigns.Create, Permissions.Campaigns.Read, Permissions.Campaigns.Write,
            Permissions.Campaigns.Delete, Permissions.Campaigns.Checkout, Permissions.Campaigns.Cancel,
            Permissions.Guests.Upload, Permissions.Guests.Write,
            Permissions.Guests.Resend, Permissions.Payments.Read,
            Permissions.Photos.Read, Permissions.Photos.Upload, Permissions.Photos.Moderate,
            Permissions.Buckets.Read, Permissions.Buckets.Manage,
        },

        [Inviter] = new[]
        {
            Permissions.Templates.Read,
            Permissions.Campaigns.Create, Permissions.Campaigns.Read, Permissions.Campaigns.Write,
            Permissions.Campaigns.Delete, Permissions.Campaigns.Checkout, Permissions.Campaigns.Cancel,
            Permissions.Guests.Upload, Permissions.Guests.Write, Permissions.Guests.Resend,
            Permissions.Payments.Read, Permissions.Dashboard.Read,
            Permissions.Photos.Read, Permissions.Photos.Upload, Permissions.Photos.Moderate,
            Permissions.Buckets.Read, Permissions.Buckets.Manage,
        },

        [Invitee] = new[]
        {
            Permissions.Invites.View, Permissions.Invites.Rsvp,
            Permissions.Inbox.Read,
            // A guest sees the box and shoots into it; they may remove their own photo, which is
            // checked against the uploader rather than granted as a permission.
            Permissions.Photos.Read, Permissions.Photos.Upload,
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
