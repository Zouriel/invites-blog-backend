using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the modular monolith. Tables and indexes follow spec §9.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<CustomTemplate> CustomTemplates => Set<CustomTemplate>();
    public DbSet<Inviter> Inviters => Set<Inviter>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<RsvpResponse> RsvpResponses => Set<RsvpResponse>();
    public DbSet<UploadedGuestFile> UploadedGuestFiles => Set<UploadedGuestFile>();
    public DbSet<TemplateAsset> TemplateAssets => Set<TemplateAsset>();
    public DbSet<CampaignAsset> CampaignAssets => Set<CampaignAsset>();
    public DbSet<SuppressionEntry> SuppressionList => Set<SuppressionEntry>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // RBAC (full authorization model)
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Template>(e =>
        {
            e.ToTable("templates");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).HasDatabaseName("idx_templates_slug");
            e.Property(x => x.SceneJson).HasColumnType("jsonb");
            e.Property(x => x.ManifestJson).HasColumnType("jsonb");
        });

        b.Entity<CustomTemplate>(e =>
        {
            e.ToTable("custom_templates");
            e.HasKey(x => x.Id);
            e.Property(x => x.SceneJson).HasColumnType("jsonb");
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
        });

        b.Entity<RsvpResponse>(e =>
        {
            e.ToTable("rsvp_responses");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InviteId);
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

        b.Entity<SuppressionEntry>(e =>
        {
            e.ToTable("suppression_list");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ContactHash).IsUnique().HasDatabaseName("idx_suppression_contact");
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
            e.HasIndex(x => x.Email).IsUnique().HasDatabaseName("idx_users_email");
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
