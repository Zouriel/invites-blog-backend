using InvitesBlog.Domain.Enums;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the modular monolith. Tables and indexes follow spec §9.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateType> TemplateTypes => Set<TemplateType>();
    public DbSet<CustomTemplate> CustomTemplates => Set<CustomTemplate>();
    public DbSet<Inviter> Inviters => Set<Inviter>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<InviteTrustedIp> InviteTrustedIps => Set<InviteTrustedIp>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<RsvpResponse> RsvpResponses => Set<RsvpResponse>();
    public DbSet<UploadedGuestFile> UploadedGuestFiles => Set<UploadedGuestFile>();
    public DbSet<TemplateAsset> TemplateAssets => Set<TemplateAsset>();
    public DbSet<CampaignAsset> CampaignAssets => Set<CampaignAsset>();
    public DbSet<EventPhoto> EventPhotos => Set<EventPhoto>();
    public DbSet<MediaBucket> MediaBuckets => Set<MediaBucket>();
    public DbSet<MediaBucketQr> MediaBucketQrs => Set<MediaBucketQr>();
    public DbSet<MediaBucketMember> MediaBucketMembers => Set<MediaBucketMember>();
    public DbSet<SuppressionEntry> SuppressionList => Set<SuppressionEntry>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Inquiry> Inquiries => Set<Inquiry>();
    public DbSet<VerifiedContactLink> VerifiedContactLinks => Set<VerifiedContactLink>();

    // RBAC (full authorization model)
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<UserExternalLogin> UserExternalLogins => Set<UserExternalLogin>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Template>(e =>
        {
            e.ToTable("templates");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).HasDatabaseName("idx_templates_slug");
            e.Property(x => x.SceneJson).HasColumnType("jsonb");
            e.Property(x => x.ManifestJson).HasColumnType("jsonb");
            e.Property(x => x.Visibility).HasDefaultValue("Public");
            e.Property(x => x.IsUsed).HasDefaultValue(false);
            e.HasIndex(x => x.AssignedEmail).HasDatabaseName("idx_templates_assigned_email");
            e.Property(x => x.RequesterConsentToPublish).HasDefaultValue(false);
            e.Property(x => x.DesignerConsentToPublish).HasDefaultValue(false);
            e.Property(x => x.CommissionPrice).HasColumnType("numeric(12,2)");
            e.Property(x => x.UsagePrice).HasColumnType("numeric(12,2)");
            e.HasIndex(x => x.DesignerUserId).HasDatabaseName("idx_templates_designer_user_id");
        });

        b.Entity<Inquiry>(e =>
        {
            e.ToTable("inquiries");
            e.HasKey(x => x.Id);
            e.Property(x => x.HasAttended).HasDefaultValue(false);
            e.Property(x => x.TemplateIssued).HasDefaultValue(false);
            // Admin list ordering: unattended first, then oldest first.
            e.HasIndex(x => new { x.HasAttended, x.CreatedAt }).HasDatabaseName("idx_inquiries_queue");
            e.HasIndex(x => x.Email).HasDatabaseName("idx_inquiries_email");
            e.Property(x => x.CommissionPrice).HasColumnType("numeric(12,2)");
            e.Property(x => x.UsagePrice).HasColumnType("numeric(12,2)");
            // A designer's commission list: what has been handed to them.
            e.HasIndex(x => x.AssignedDesignerUserId).HasDatabaseName("idx_inquiries_assigned_designer");
            // A designer also needs to find the requests that ASKED FOR THEM but aren't theirs yet.
            e.HasIndex(x => x.RequestedDesignerUserId).HasDatabaseName("idx_inquiries_requested_designer");
        });

        b.Entity<TemplateType>(e =>
        {
            e.ToTable("template_types");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("idx_template_types_slug");
        });

        b.Entity<CustomTemplate>(e =>
        {
            e.ToTable("custom_templates");
            e.HasKey(x => x.Id);
            e.Property(x => x.ManifestJson).HasColumnType("jsonb").HasDefaultValue("{}");
            e.Property(x => x.CommissionPrice).HasColumnType("numeric(12,2)");
            e.Property(x => x.UsagePrice).HasColumnType("numeric(12,2)");
            e.Property(x => x.RequesterConsentToPublish).HasDefaultValue(false);
            e.Property(x => x.DesignerConsentToPublish).HasDefaultValue(false);
            // The review queue: pending submissions first, oldest first.
            e.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("idx_custom_templates_queue");
            e.HasIndex(x => x.DesignerUserId).HasDatabaseName("idx_custom_templates_designer_user_id");
            e.HasIndex(x => x.Slug).HasDatabaseName("idx_custom_templates_slug");
        });

        b.Entity<Inviter>(e =>
        {
            e.ToTable("inviters");
            e.HasKey(x => x.Id);
            // Unique on lower(email) — enforced via a raw index below.
            e.HasIndex(x => x.Email).IsUnique().HasDatabaseName("idx_inviters_email");
        });

        b.Entity<Campaign>(e =>
        {
            e.ToTable("campaigns");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Status).HasDatabaseName("idx_campaigns_status");
            e.HasIndex(x => x.AccessTokenHash).IsUnique().HasDatabaseName("idx_campaigns_access_token_hash");
            e.HasIndex(x => x.DashboardTokenHash).HasDatabaseName("idx_campaigns_dashboard_token_hash");
            e.Property(x => x.CustomContentJson).HasColumnType("jsonb");
            e.Property(x => x.ThemeOverridesJson).HasColumnType("jsonb");
            e.Property(x => x.DeliverySettingsJson).HasColumnType("jsonb");
            e.Property(x => x.RulesJson).HasColumnType("jsonb");
            // Valid JSON default so adding this NOT NULL jsonb column to existing rows succeeds.
            e.Property(x => x.RolesJson).HasColumnType("jsonb").HasDefaultValue("{\"roles\":[]}");
            e.Property(x => x.RsvpQuestionsJson).HasColumnType("jsonb").HasDefaultValue("{\"questions\":[]}");
            e.Property(x => x.TemplateManifestJson).HasColumnType("jsonb").HasDefaultValue("{}");
            e.Property(x => x.DesignerFee).HasColumnType("numeric(12,2)").HasDefaultValue(0m);
            e.Property(x => x.TemplatePackageUrl).HasDefaultValue(string.Empty);
            e.HasIndex(x => x.CreatedByUserId).HasDatabaseName("idx_campaign_created_by");
        });

        b.Entity<Guest>(e =>
        {
            e.ToTable("guests");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CampaignId).HasDatabaseName("idx_guests_campaign_id");
            e.HasIndex(x => x.PhoneE164).HasDatabaseName("idx_guests_phone_e164");
            e.HasIndex(x => x.Email).HasDatabaseName("idx_guests_email");
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
        });

        b.Entity<Invite>(e =>
        {
            e.ToTable("invites");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CampaignId).HasDatabaseName("idx_invites_campaign_id");
            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("idx_invites_token_hash");
            e.HasIndex(x => x.GuestId);
        });

        b.Entity<DeliveryAttempt>(e =>
        {
            e.ToTable("delivery_attempts");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InviteId).HasDatabaseName("idx_delivery_invite_id");
        });

        b.Entity<InviteTrustedIp>(e =>
        {
            e.ToTable("invite_trusted_ips");
            e.HasKey(x => x.Id);
            // One row per (invite, IP) — re-trusting an already-known IP updates LastSeenAt in place
            // rather than piling up duplicate rows.
            e.HasIndex(x => new { x.InviteId, x.IpAddress }).IsUnique()
                .HasDatabaseName("idx_invite_trusted_ips_invite_ip");
        });

        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CampaignId);
            e.HasIndex(x => x.ProviderSessionId);
            e.Property(x => x.Amount).HasColumnType("numeric(10,2)");
        });

        b.Entity<Refund>(e =>
        {
            e.ToTable("refunds");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PaymentId);
            e.Property(x => x.Amount).HasColumnType("numeric(10,2)");
        });

        b.Entity<OtpChallenge>(e =>
        {
            e.ToTable("otp_challenges");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PhoneE164, x.ExpiresAt }).HasDatabaseName("idx_otp_phone_expires");
            e.HasIndex(x => new { x.Email, x.ExpiresAt }).HasDatabaseName("idx_otp_email_expires");
            // Existing rows predate the split and were all sign-in codes; 0 is SignIn.
            e.Property(x => x.Purpose).HasDefaultValue(OtpPurpose.SignIn);
        });

        b.Entity<RsvpResponse>(e =>
        {
            e.ToTable("rsvp_responses");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InviteId);
            e.Property(x => x.AnswersJson).HasColumnType("jsonb").HasDefaultValue("{}");
        });

        b.Entity<UploadedGuestFile>(e =>
        {
            e.ToTable("uploaded_guest_files");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CampaignId);
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
        });

        b.Entity<TemplateAsset>(e => { e.ToTable("template_assets"); e.HasKey(x => x.Id); e.HasIndex(x => x.TemplateId); });
        b.Entity<CampaignAsset>(e => { e.ToTable("campaign_assets"); e.HasKey(x => x.Id); e.HasIndex(x => x.CampaignId); });

        b.Entity<EventPhoto>(e =>
        {
            e.ToTable("event_photos");
            e.HasKey(x => x.Id);
            // The photo box is read one way and one way only: this campaign's live photos, newest
            // first. An unbounded table read on every guest's page load is worth an index that
            // matches the query exactly rather than one that merely narrows it.
            e.HasIndex(x => new { x.CampaignId, x.DeletedAt, x.CreatedAt });
            e.HasIndex(x => x.GuestId);
            // A standalone bucket has no campaign to read by, and the bucket list counts live items
            // per bucket — both are this index rather than the campaign one.
            e.HasIndex(x => new { x.BucketId, x.DeletedAt, x.CreatedAt });
        });

        b.Entity<MediaBucket>(e =>
        {
            e.ToTable("media_buckets");
            e.HasKey(x => x.Id);
            // The two ways a bucket is ever looked up: everything an account owns, and the one
            // belonging to a given event.
            e.HasIndex(x => x.OwnerUserId);
            // Unique, because "the campaign's bucket" has to mean exactly one thing. Provisioning
            // races on a first upload otherwise leave a campaign with two boxes and its media split
            // between them, which is not recoverable by looking at it.
            e.HasIndex(x => x.CampaignId)
                .IsUnique()
                .HasFilter("campaign_id IS NOT NULL")
                .HasDatabaseName("idx_media_buckets_campaign");
        });

        b.Entity<MediaBucketQr>(e =>
        {
            e.ToTable("media_bucket_qrs");
            e.HasKey(x => x.Id);
            // A scan resolves a token hash to a code on every single hit — this is the read that has
            // to be an index lookup rather than a scan of every code ever printed.
            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("idx_media_bucket_qr_token");
            e.HasIndex(x => new { x.BucketId, x.CreatedAt });
        });

        b.Entity<MediaBucketMember>(e =>
        {
            e.ToTable("media_bucket_members");
            e.HasKey(x => x.Id);
            // The read that runs on every view: is this contact on this bucket's list. Unique, so
            // adding the same person twice cannot produce two rows to revoke separately.
            e.HasIndex(x => new { x.BucketId, x.Contact })
                .IsUnique()
                .HasDatabaseName("idx_media_bucket_member");
            // The other direction: every bucket a signed-in account may look at.
            e.HasIndex(x => x.Contact);
        });

        b.Entity<SuppressionEntry>(e =>
        {
            e.ToTable("suppression_list");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ContactHash).IsUnique().HasDatabaseName("idx_suppression_contact");
        });

        b.Entity<VerifiedContactLink>(e =>
        {
            e.ToTable("verified_contact_links");
            e.HasKey(x => x.Id);
            // One row per pairing; both directions look the pair up by either side.
            e.HasIndex(x => new { x.Email, x.PhoneE164 }).IsUnique().HasDatabaseName("idx_vcl_pair");
            e.HasIndex(x => x.Email).HasDatabaseName("idx_vcl_email");
            e.HasIndex(x => x.PhoneE164).HasDatabaseName("idx_vcl_phone");
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CampaignId);
            e.Property(x => x.DataJson).HasColumnType("jsonb");
        });

        b.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            // Unique WHEN SET: Postgres allows many NULLs in a unique index, which is what lets a
            // phone-only customer and an email-only designer coexist until they're linked.
            e.HasIndex(x => x.Email).IsUnique().HasDatabaseName("idx_users_email");
            e.HasIndex(x => x.PhoneE164).IsUnique().HasDatabaseName("idx_users_phone_e164");
        });

        b.Entity<UserExternalLogin>(e =>
        {
            e.ToTable("user_external_logins");
            e.HasKey(x => x.Id);
            // One external identity maps to exactly one account.
            e.HasIndex(x => new { x.Provider, x.ExternalSubjectId }).IsUnique()
                .HasDatabaseName("idx_user_external_logins_provider_subject");
            e.HasOne(x => x.User).WithMany(u => u.ExternalLogins)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("idx_roles_name");
        });

        b.Entity<Permission>(e =>
        {
            e.ToTable("permissions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("idx_permissions_name");
        });

        b.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(x => new { x.RoleId, x.PermissionId });
            e.HasOne(x => x.Role).WithMany(r => r.RolePermissions).HasForeignKey(x => x.RoleId);
            e.HasOne(x => x.Permission).WithMany(p => p.RolePermissions).HasForeignKey(x => x.PermissionId);
        });

        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.User).WithMany(u => u.UserRoles).HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Role).WithMany(r => r.UserRoles).HasForeignKey(x => x.RoleId);
        });

        base.OnModelCreating(b);

        // Snake_case all columns to match the schema in spec §9 (phone_e164, access_token_hash, ...).
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var prop in entity.GetProperties())
                prop.SetColumnName(ToSnakeCase(prop.Name));
    }

    private static string ToSnakeCase(string input)
    {
        var sb = new System.Text.StringBuilder(input.Length + 8);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (!char.IsUpper(input[i - 1]) || (i + 1 < input.Length && !char.IsUpper(input[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
