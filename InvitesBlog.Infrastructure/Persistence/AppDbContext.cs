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
    public DbSet<TemplateDesign> TemplateDesigns => Set<TemplateDesign>();
    public DbSet<TemplateDesignPublish> TemplateDesignPublishes => Set<TemplateDesignPublish>();
    public DbSet<DesignAssistantMessage> DesignAssistantMessages => Set<DesignAssistantMessage>();
    public DbSet<AssistantUsage> AssistantUsages => Set<AssistantUsage>();
    public DbSet<AiPictureUsage> AiPictureUsages => Set<AiPictureUsage>();
    public DbSet<TemplateReport> TemplateReports => Set<TemplateReport>();
    public DbSet<ContentReport> ContentReports => Set<ContentReport>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
    public DbSet<Inviter> Inviters => Set<Inviter>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<CampaignCelebrant> CampaignCelebrants => Set<CampaignCelebrant>();
    public DbSet<PostComment> PostComments => Set<PostComment>();
    public DbSet<PostLike> PostLikes => Set<PostLike>();
    public DbSet<CommentLike> CommentLikes => Set<CommentLike>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<InviteTrustedIp> InviteTrustedIps => Set<InviteTrustedIp>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<GooglePlayPurchase> GooglePlayPurchases => Set<GooglePlayPurchase>();
    public DbSet<AppStorePurchase> AppStorePurchases => Set<AppStorePurchase>();
    public DbSet<AppStoreIntent> AppStoreIntents => Set<AppStoreIntent>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<RsvpResponse> RsvpResponses => Set<RsvpResponse>();
    public DbSet<UploadedGuestFile> UploadedGuestFiles => Set<UploadedGuestFile>();
    public DbSet<TemplateAsset> TemplateAssets => Set<TemplateAsset>();
    public DbSet<CampaignAsset> CampaignAssets => Set<CampaignAsset>();
    public DbSet<EventPhoto> EventPhotos => Set<EventPhoto>();
    public DbSet<VideoTrimJob> VideoTrimJobs => Set<VideoTrimJob>();
    public DbSet<PhotoFace> PhotoFaces => Set<PhotoFace>();
    public DbSet<FacePerson> FacePeople => Set<FacePerson>();
    public DbSet<FaceJob> FaceJobs => Set<FaceJob>();
    public DbSet<MediaBucket> MediaBuckets => Set<MediaBucket>();
    public DbSet<MediaBucketQr> MediaBucketQrs => Set<MediaBucketQr>();
    public DbSet<MediaBucketMember> MediaBucketMembers => Set<MediaBucketMember>();
    public DbSet<SuppressionEntry> SuppressionList => Set<SuppressionEntry>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Inquiry> Inquiries => Set<Inquiry>();
    public DbSet<VerifiedContactLink> VerifiedContactLinks => Set<VerifiedContactLink>();
    public DbSet<PendingUpload> PendingUploads => Set<PendingUpload>();
    public DbSet<PendingRemoval> PendingRemovals => Set<PendingRemoval>();
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<VenueStaff> VenueStaff => Set<VenueStaff>();
    public DbSet<PassCredit> PassCredits => Set<PassCredit>();

    // RBAC (full authorization model)
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<UserExternalLogin> UserExternalLogins => Set<UserExternalLogin>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<Favourite> Favourites => Set<Favourite>();

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
            e.HasIndex(x => x.DesignerUserId).HasDatabaseName("idx_templates_designer_user_id");
        });

        b.Entity<Inquiry>(e =>
        {
            e.ToTable("inquiries");
            e.HasKey(x => x.Id);
            e.Property(x => x.HasAttended).HasDefaultValue(false);
            // Admin list ordering: unattended first, then oldest first.
            e.HasIndex(x => new { x.HasAttended, x.CreatedAt }).HasDatabaseName("idx_inquiries_queue");
            e.HasIndex(x => x.Email).HasDatabaseName("idx_inquiries_email");
        });

        b.Entity<TemplateDesign>(e =>
        {
            e.ToTable("template_designs");
            e.HasKey(x => x.Id);
            e.Property(x => x.SceneJson).HasColumnType("jsonb");
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasIndex(x => new { x.OwnerUserId, x.UpdatedAt }).HasDatabaseName("idx_template_designs_owner_updated");
            e.HasIndex(x => x.TemplateId).HasDatabaseName("idx_template_designs_template_id");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TemplateDesignPublish>(e =>
        {
            e.ToTable("template_design_publishes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).HasMaxLength(32);
            e.Property(x => x.Visibility).HasMaxLength(16);
            // The public-publish rate limit counts one account's recent publishes.
            e.HasIndex(x => new { x.UserId, x.PublishedAt }).HasDatabaseName("idx_template_design_publishes_user_time");
            e.HasIndex(x => x.DesignId).HasDatabaseName("idx_template_design_publishes_design_id");
            e.HasOne<TemplateDesign>().WithMany().HasForeignKey(x => x.DesignId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DesignAssistantMessage>(e =>
        {
            e.ToTable("design_assistant_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasMaxLength(16);
            // Plain text, not jsonb: thinking signatures must come back byte for byte, and jsonb rewrites.
            e.Property(x => x.ContentJson).HasColumnType("text");
            e.HasIndex(x => new { x.DesignId, x.Seq }).IsUnique().HasDatabaseName("idx_design_assistant_messages_design_seq");
            e.HasOne<TemplateDesign>().WithMany().HasForeignKey(x => x.DesignId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AssistantUsage>(e =>
        {
            e.ToTable("assistant_usage");
            e.HasKey(x => x.Id);
            e.Property(x => x.Month).HasMaxLength(7);
            e.HasIndex(x => new { x.UserId, x.Month }).IsUnique().HasDatabaseName("idx_assistant_usage_user_month");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AiPictureUsage>(e =>
        {
            e.ToTable("ai_picture_usage");
            e.HasKey(x => x.Id);
            e.Property(x => x.Month).HasMaxLength(7);
            e.HasIndex(x => new { x.UserId, x.Month }).IsUnique().HasDatabaseName("idx_ai_picture_usage_user_month");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TemplateReport>(e =>
        {
            e.ToTable("template_reports");
            e.HasKey(x => x.Id);
            e.Property(x => x.Reason).HasMaxLength(32);
            e.Property(x => x.Details).HasMaxLength(1000);
            e.Property(x => x.Resolution).HasMaxLength(16);
            e.Property(x => x.ResolutionNote).HasMaxLength(1000);
            // The admin queue: open reports, oldest first.
            e.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("idx_template_reports_queue");
            e.HasIndex(x => x.TemplateId).HasDatabaseName("idx_template_reports_template_id");
        });

        b.Entity<TemplateType>(e =>
        {
            e.ToTable("template_types");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("idx_template_types_slug");
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
            // Events made before cover photos existed start with none picked.
            e.Property(x => x.PostCoverPhotoIds).HasDefaultValueSql("'{}'::uuid[]");
            e.ToTable("campaigns");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Status).HasDatabaseName("idx_campaigns_status");
            // Every campaign before save the dates was an invitation: 0, and no all-day dates.
            e.Property(x => x.Kind).HasDefaultValue(CampaignKind.Invitation);
            e.Property(x => x.AllDay).HasDefaultValue(false);
            e.Property(x => x.OpenGuestList).HasDefaultValue(false);
            // Every event before time zones existed was a Maldives event: they keep its clock.
            e.Property(x => x.TimeZone).HasMaxLength(64).HasDefaultValue(InvitesBlog.Domain.Events.EventZones.Legacy);
            e.Property(x => x.CardThemeJson).HasColumnType("jsonb").HasDefaultValue("{}");
            e.HasIndex(x => x.AccessTokenHash).IsUnique().HasDatabaseName("idx_campaigns_access_token_hash");
            e.HasIndex(x => x.DashboardTokenHash).HasDatabaseName("idx_campaigns_dashboard_token_hash");
            // Every open link resolves through this index, and the lookup runs before anything is
            // authorised — so it is the one query an attacker can aim at freely. Indexed, rate
            // limited at the route, and UNIQUE: two events answering to one code would mean the
            // wrong invitation opening, and at 59 bits a collision is a bug rather than bad luck.
            e.HasIndex(x => x.OpenLinkCode).IsUnique().HasDatabaseName("idx_campaigns_open_link_code");
            // A venue's events page lists by this. Leaving the venue plan keeps the events; they just stop being its.
            e.HasIndex(x => x.VenueId).HasDatabaseName("idx_campaigns_venue_id");
            e.HasOne<Venue>().WithMany().HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.CustomContentJson).HasColumnType("jsonb");
            e.Property(x => x.ThemeOverridesJson).HasColumnType("jsonb");
            e.Property(x => x.DeliverySettingsJson).HasColumnType("jsonb");
            e.Property(x => x.RulesJson).HasColumnType("jsonb");
            // Valid JSON default so adding this NOT NULL jsonb column to existing rows succeeds.
            e.Property(x => x.RolesJson).HasColumnType("jsonb").HasDefaultValue("{\"roles\":[]}");
            e.Property(x => x.RsvpQuestionsJson).HasColumnType("jsonb").HasDefaultValue("{\"questions\":[]}");
            e.Property(x => x.TemplateManifestJson).HasColumnType("jsonb").HasDefaultValue("{}");
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
            // Not for lookups: the principal key MediaBucketMember points at, so a bucket can only
            // ever admit a guest of its OWN event.
            e.HasIndex(x => new { x.CampaignId, x.Id })
                .IsUnique()
                .HasDatabaseName("idx_guests_campaign_id_id");
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
            e.Property(x => x.Roles).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
            e.Property(x => x.SelfAdded).HasDefaultValue(false);
            e.Property(x => x.TypedContact).HasMaxLength(120);
        });

        b.Entity<CampaignCelebrant>(e =>
        {
            e.ToTable("campaign_celebrants");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CampaignId).HasDatabaseName("idx_campaign_celebrants_campaign_id");
            // Looked up by the signed-in account's contacts on every events list and access check.
            e.HasIndex(x => x.Email).HasDatabaseName("idx_campaign_celebrants_email");
            e.HasIndex(x => x.PhoneE164).HasDatabaseName("idx_campaign_celebrants_phone_e164");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.InheritsOwnership).HasDefaultValue(false);
            e.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
        });

        // The feed: comments, and likes on posts and on comments.
        b.Entity<PostComment>(e =>
        {
            e.ToTable("post_comments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CampaignId, x.CreatedAt }).HasDatabaseName("idx_post_comments_campaign_created");
            e.HasIndex(x => x.ParentId).HasDatabaseName("idx_post_comments_parent_id");
            e.Property(x => x.Body).HasMaxLength(1000);
            e.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PostLike>(e =>
        {
            e.ToTable("post_likes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CampaignId, x.UserId }).IsUnique().HasDatabaseName("ux_post_likes_campaign_user");
            e.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CommentLike>(e =>
        {
            e.ToTable("comment_likes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CommentId, x.UserId }).IsUnique().HasDatabaseName("ux_comment_likes_comment_user");
            e.HasOne<PostComment>().WithMany().HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
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
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ProviderSessionId);
            e.Property(x => x.Quantity).HasDefaultValue(1);
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Amount).HasColumnType("numeric(10,2)");
            e.Property(x => x.TermsVersion).HasMaxLength(32);
            e.Property(x => x.IsTest).HasDefaultValue(false);
            // A store's order is paid for once, whichever way word of it arrives. Only for the store:
            // the gateway's references have never been unique and aren't promised to be.
            e.HasIndex(x => x.ProviderPaymentId).IsUnique()
                .HasFilter("provider = 'google_play' AND provider_payment_id IS NOT NULL")
                .HasDatabaseName("ux_payments_google_play_order");
            // Named, so it is a second index on the column and not the one above reconfigured.
            e.HasIndex(x => x.ProviderPaymentId, "ux_payments_app_store_transaction").IsUnique()
                .HasFilter("provider = 'app_store' AND provider_payment_id IS NOT NULL");
        });

        b.Entity<GooglePlayPurchase>(e =>
        {
            e.ToTable("google_play_purchases");
            e.HasKey(x => x.Id);
            e.Property(x => x.PurchaseToken).HasMaxLength(2048);
            e.HasIndex(x => x.PurchaseToken).IsUnique().HasDatabaseName("ux_google_play_purchases_token");
            e.Property(x => x.ProductId).HasMaxLength(64);
            e.Property(x => x.OrderId).HasMaxLength(64);
            e.Property(x => x.SubscriptionState).HasMaxLength(64);
            e.Property(x => x.IsTest).HasDefaultValue(false);
            e.Property(x => x.AutoRenewing).HasDefaultValue(false);
            e.HasIndex(x => x.UserId);
            // No foreign keys, like payments: these rows outlive what they name, as the accounts do.
        });

        b.Entity<AppStorePurchase>(e =>
        {
            e.ToTable("app_store_purchases");
            e.HasKey(x => x.Id);
            e.Property(x => x.OriginalTransactionId).HasMaxLength(64);
            e.HasIndex(x => x.OriginalTransactionId).IsUnique().HasDatabaseName("ux_app_store_purchases_original");
            e.Property(x => x.TransactionId).HasMaxLength(64);
            e.Property(x => x.ProductId).HasMaxLength(64);
            e.Property(x => x.IsTest).HasDefaultValue(false);
            e.Property(x => x.AutoRenewing).HasDefaultValue(false);
            e.HasIndex(x => x.UserId);
            // No foreign keys, like payments: these rows outlive what they name.
        });

        b.Entity<AppStoreIntent>(e =>
        {
            e.ToTable("app_store_intents");
            e.HasKey(x => x.Id);
            e.Property(x => x.ProductId).HasMaxLength(64);
            e.HasIndex(x => x.UserId);
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

        // Reports about people's posts, comments, photos and profiles (guideline 1.2), and blocks.
        b.Entity<ContentReport>(e =>
        {
            e.ToTable("content_reports");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Reason).HasMaxLength(32);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.Snapshot).HasMaxLength(2000);
            e.Property(x => x.Resolution).HasMaxLength(16);
            e.Property(x => x.ResolutionNote).HasMaxLength(1000);
            // The admin queue: open reports, oldest first.
            e.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("idx_content_reports_queue");
            // A reporter's own hide list, read on every feed and album page they open.
            e.HasIndex(x => new { x.ReporterUserId, x.Kind }).HasDatabaseName("idx_content_reports_reporter_kind");
            e.HasIndex(x => new { x.Kind, x.TargetId }).HasDatabaseName("idx_content_reports_target");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ReporterUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserBlock>(e =>
        {
            e.ToTable("user_blocks");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.BlockerUserId, x.BlockedUserId }).IsUnique().HasDatabaseName("ux_user_blocks_pair");
            e.HasIndex(x => x.BlockedUserId).HasDatabaseName("idx_user_blocks_blocked");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.BlockerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.BlockedUserId).OnDelete(DeleteBehavior.Cascade);
        });

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
            // Trimming a video moves it to a new file. A delete that read the photo just before that
            // would credit the old size and remove the old file, so it has to notice (see RemoveAsync).
            e.Property(x => x.Url).IsConcurrencyToken();
            e.Property(x => x.SizeBytes).IsConcurrencyToken();
            e.Property(x => x.KeepWhenTrimmed).HasDefaultValue(false);
        });

        b.Entity<VideoTrimJob>(e =>
        {
            e.ToTable("video_trim_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("idx_video_trim_jobs_due");
            // A photo's row can be removed outright (a deleted account); nothing about the job outlives it.
            e.HasOne<EventPhoto>().WithMany().HasForeignKey(x => x.Id).OnDelete(DeleteBehavior.Cascade);
        });

        // Face grouping (biometric data): everything cascades from the album, and faces and jobs from the photo too,
        // so no hard delete of either can leave a face behind. Soft deletes are handled in FaceGroupingService.
        b.Entity<FacePerson>(e =>
        {
            e.ToTable("face_people");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(FacePerson.MaxNameLength);
            e.HasIndex(x => x.BucketId).HasDatabaseName("idx_face_people_bucket");
            e.HasOne<MediaBucket>().WithMany().HasForeignKey(x => x.BucketId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PhotoFace>(e =>
        {
            e.ToTable("photo_faces");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PhotoId).HasDatabaseName("idx_photo_faces_photo");
            e.HasIndex(x => new { x.BucketId, x.PersonId }).HasDatabaseName("idx_photo_faces_bucket_person");
            e.HasOne<MediaBucket>().WithMany().HasForeignKey(x => x.BucketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<EventPhoto>().WithMany().HasForeignKey(x => x.PhotoId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FacePerson>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FaceJob>(e =>
        {
            e.ToTable("face_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("idx_face_jobs_due");
            e.HasIndex(x => x.BucketId).HasDatabaseName("idx_face_jobs_bucket");
            e.HasOne<EventPhoto>().WithMany().HasForeignKey(x => x.Id).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MediaBucket>().WithMany().HasForeignKey(x => x.BucketId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MediaBucket>(e =>
        {
            e.Property(x => x.FacesEndNoticeStage).HasDefaultValue(0);
            e.ToTable("media_buckets");
            e.HasKey(x => x.Id);
            // Written on INSERT only. Every later change goes through IMediaBucketUsageRepository as an
            // atomic "used_bytes = used_bytes + n". Without this, any save of a tracked bucket — a
            // rename, a resize, an access change, all of which call Update() and so mark every column
            // modified — wrote back whatever figure it had loaded, erasing uploads counted meanwhile.
            e.Property(x => x.UsedBytes).Metadata.SetAfterSaveBehavior(
                Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
            // The two ways a bucket is ever looked up: everything an account owns, and everything
            // belonging to a given event.
            e.HasIndex(x => x.OwnerUserId);
            e.Property(x => x.Name).HasMaxLength(80);
            // No longer unique. It was, because "the campaign's bucket" had to mean exactly one
            // thing and a provisioning race on a first upload would otherwise split an event's media
            // across two boxes. A pass now allows several on one event — the ceremony and
            // the after-party — so what a race must not do is create a second FREE one, and that is
            // now settled in the service by asking for the DEFAULT bucket rather than by the schema.
            e.HasIndex(x => x.CampaignId).HasDatabaseName("idx_media_buckets_campaign");
            // Not for lookups: this is the principal key MediaBucketMember's composite foreign key
            // points at, which is what makes "a guest of another event" unrepresentable.
            e.HasIndex(x => new { x.CampaignId, x.Id })
                .IsUnique()
                .HasDatabaseName("idx_media_buckets_campaign_id");
        });

        b.Entity<MediaBucketMember>(e =>
        {
            e.ToTable("media_bucket_members");
            // The pair IS the row — a guest is either admitted to a bucket or not, and there is
            // nothing to say twice.
            e.HasKey(x => new { x.BucketId, x.GuestId });
            // Read one way in the guest's editor ("which buckets may this person see") and the other
            // when a bucket asks who is on it. Both are covered: the key serves the second, this the first.
            e.HasIndex(x => x.GuestId);
            e.HasIndex(x => new { x.CampaignId, x.BucketId });

            // Composite, and pointed at the unique indexes above rather than at the primary keys.
            // Admitting a guest of one event to another event's bucket is then not a rule somebody
            // has to remember — the row cannot be written. Both cascade: taking a guest off the
            // list, or deleting a bucket, takes the admission with it, which is the entire reason
            // this is a pivot and not a copy of the guest list.
            e.HasOne<MediaBucket>()
                .WithMany()
                .HasForeignKey(x => new { x.CampaignId, x.BucketId })
                .HasPrincipalKey(x => new { x.CampaignId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne<Guest>()
                .WithMany()
                .HasForeignKey(x => new { x.CampaignId, x.GuestId })
                .HasPrincipalKey(x => new { x.CampaignId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
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

        b.Entity<SuppressionEntry>(e =>
        {
            e.ToTable("suppression_list");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ContactHash).IsUnique().HasDatabaseName("idx_suppression_contact");
        });

        b.Entity<PendingUpload>(e =>
        {
            e.ToTable("pending_uploads");
            e.HasKey(x => x.Id);
            e.Property(x => x.IncomingKey).HasMaxLength(200);
            e.Property(x => x.ContentType).HasMaxLength(120);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.UploaderName).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(500);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("idx_pending_uploads_status");
        });

        b.Entity<PendingRemoval>(e =>
        {
            e.ToTable("pending_removals");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Target).HasMaxLength(2048);
            e.HasIndex(x => x.NextAttemptAt).HasDatabaseName("idx_pending_removals_next_attempt");
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
            e.Property(x => x.PaymentCustomerId).HasMaxLength(64);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.PushMuted).HasDefaultValueSql("'{}'::text[]");
            e.Property(x => x.GuidanceTipsOff).HasDefaultValue(false);
            e.Property(x => x.GuidanceSeen).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
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

        b.Entity<Favourite>(e =>
        {
            e.ToTable("favourites");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.PhotoId }).IsUnique().HasDatabaseName("ux_favourites_user_photo");
            e.HasIndex(x => x.PhotoId).HasDatabaseName("idx_favourites_photo_id");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<EventPhoto>().WithMany().HasForeignKey(x => x.PhotoId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeviceToken>(e =>
        {
            e.ToTable("device_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Token).HasMaxLength(DeviceToken.MaxTokenLength);
            e.Property(x => x.Platform).HasMaxLength(16);
            e.Property(x => x.Environment).HasMaxLength(16);
            // One row per device: a token registered again moves rather than duplicates.
            e.HasIndex(x => x.Token).IsUnique().HasDatabaseName("idx_device_tokens_token");
            e.HasIndex(x => x.UserId).HasDatabaseName("idx_device_tokens_user_id");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
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

        // Settings an admin changes at runtime (prices), one JSON value per key.
        b.Entity<AppSetting>(e =>
        {
            e.ToTable("app_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.ValueJson).HasColumnType("jsonb");
        });

        // Legacy tables kept rather than dropped (owner, 2026-11-12): the old venue page's venues and staff, and the
        // retired passes held for clients when Premium was called Studio. Nothing reads venues or staff any more.
        b.Entity<Venue>(e =>
        {
            e.ToTable("venues");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OwnerUserId).IsUnique().HasDatabaseName("idx_venues_owner_user_id");
            e.HasIndex(x => x.Code).IsUnique().HasDatabaseName("idx_venues_code");
            e.Property(x => x.Code).HasMaxLength(12);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Place).HasMaxLength(120);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<VenueStaff>(e =>
        {
            e.ToTable("venue_staff");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.VenueId, x.Email }).IsUnique().HasDatabaseName("idx_venue_staff_venue_email");
            // Looked up by the signed-in account's email on every access check.
            e.HasIndex(x => x.Email).HasDatabaseName("idx_venue_staff_email");
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasOne<Venue>().WithMany().HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PassCredit>(e =>
        {
            e.ToTable("pass_credits");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OwnerUserId).HasDatabaseName("idx_pass_credits_owner_user_id");
            e.Property(x => x.Price).HasPrecision(10, 2);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Campaign>().WithMany().HasForeignKey(x => x.UsedOnCampaignId).OnDelete(DeleteBehavior.SetNull);
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
