using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Guests;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Rules;
using InvitesBlog.Infrastructure.Delivery;
using InvitesBlog.Infrastructure.Email;
using InvitesBlog.Infrastructure.Otp;
using InvitesBlog.Infrastructure.Payments;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Seed;
using InvitesBlog.Infrastructure.Storage;
using InvitesBlog.Infrastructure.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInvitesBlogInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        // Persistence
        var conn = config.GetConnectionString("Postgres")
                   ?? "Host=localhost;Port=5432;Database=invites_blog;Username=invites;Password=invites_password";
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conn));
        services.AddScoped<Application.Abstractions.Persistence.IUnitOfWork, Repositories.UnitOfWork>();

        // Repositories — base + entity repositories auto-registered by convention (spec §Repositories).
        services.AddScoped(typeof(Application.Abstractions.Persistence.IRepository<>), typeof(Repositories.BaseRepository<>));
        services.Scan(scan => scan.FromAssemblyOf<AppDbContext>()
            .AddClasses(c => c.Where(t => t.Name.EndsWith("Repository")
                && !t.IsGenericTypeDefinition && !t.IsAbstract), publicOnly: false)
            .AsImplementedInterfaces().WithScopedLifetime());

        // Feature services (Application layer) auto-registered by convention (spec §Services).
        services.Scan(scan => scan.FromAssemblyOf<PhoneNormalizer>()
            .AddClasses(c => c.Where(t => t.Name.EndsWith("Service") && !t.IsAbstract), publicOnly: false)
            .AsImplementedInterfaces().WithScopedLifetime());

        // Request validators (spec §Validation).
        services.AddValidatorsFromAssemblyContaining<PhoneNormalizer>();

        // Pure application services
        services.AddSingleton<PhoneNormalizer>();
        services.AddSingleton<RuleEngine>();
        services.AddScoped<GuestUploadParser>();
        services.AddScoped<RbacSeeder>();
        services.AddScoped<Application.Plans.IPriceBook, Application.Plans.PriceBook>();

        // Invitee JWT (issuer + validation params), exposed to Application via IInviteeTokenIssuer.
        services.AddSingleton<Security.InviteeJwt>();
        services.AddSingleton<IInviteeTokenIssuer>(sp => sp.GetRequiredService<Security.InviteeJwt>());

        // Designer OAuth sign-in. Registered unconditionally — each provider reports itself
        // unconfigured until its OAuth:<Provider>:ClientId is set, so email + password works regardless.
        // Singletons so the providers' fetched signing keys are cached across requests.
        services.AddHttpClient();
        services.AddSingleton<IExternalAuthProvider, Security.GoogleAuthProvider>();
        services.AddSingleton<IExternalAuthProvider, Security.MicrosoftAuthProvider>();

        // Storage (Local by default; MinIO/S3 when configured)
        var storageProvider = config["Storage:Provider"] ?? "Local";
        if (storageProvider.Equals("Minio", StringComparison.OrdinalIgnoreCase) ||
            storageProvider.Equals("S3", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IStorageService, S3StorageService>();
        else
            services.AddSingleton<IStorageService, LocalFileStorageService>();

        services.AddScoped<TemplatePackagePublisher>();
        services.AddScoped<RawTemplatePackager>();
        services.AddScoped<ITemplatePackager, TemplatePackagerAdapter>();
        services.AddScoped<IDesignEngine, DesignEngine>();

        // The designer's art library: searched and downloaded server-side through a client that only
        // connects to public addresses (library results link to hosts we don't control).
        services.AddHttpClient(Templates.Art.ArtHttp.ClientName, c =>
            {
                // Each call sets its own, shorter limit (ArtLibrary); this only stops a runaway.
                c.Timeout = TimeSpan.FromSeconds(120);
                c.DefaultRequestHeaders.UserAgent.ParseAdd(Templates.Art.ArtHttp.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(Templates.Art.ArtHttp.Handler);
        services.AddSingleton<Templates.Art.IArtSource, Templates.Art.OpenverseSource>();
        services.AddSingleton<Templates.Art.IArtSource, Templates.Art.OpenClipartSource>();
        services.AddSingleton<Templates.Art.IArtSource, Templates.Art.FreeSvgSource>();
        services.AddScoped<IArtLibrary, Templates.Art.ArtLibrary>();
        services.AddScoped<TemplateSeeder>();
        services.AddScoped<RawTemplateSeeder>();
        services.AddScoped<TemplateManifestRefresher>();
        services.AddScoped<TemplateTypeSeeder>();
        services.AddScoped<DesignFontSeeder>();
        services.AddScoped<Rendering.InviteRenderService>();

        // Email — Resend at launch (provider guide), Console for dev when no key is configured.
        var emailProvider = config["Email:Provider"] ?? "Console";
        if (emailProvider.Equals("Resend", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<Email.ResendClient>(c =>
            {
                c.BaseAddress = new Uri("https://api.resend.com");
                var key = config["Email:ApiKey"];
                if (!string.IsNullOrWhiteSpace(key))
                    c.DefaultRequestHeaders.Authorization = new("Bearer", key);
            }).AddStandardResilienceHandler(); // retries with backoff; respects 429 (guide §2.6)
            services.AddScoped<IEmailSender, Email.ResendEmailSender>();
        }
        else
        {
            services.AddScoped<IEmailSender, ConsoleEmailSender>();
        }
        services.AddSingleton<Email.ResendWebhookVerifier>();

        // Stateless and thread-safe, so one instance serves every upload. Named *Optimizer rather
        // than *Service, so Scrutor's convention scan doesn't pick it up — registered explicitly.
        services.AddSingleton<IImageOptimizer, Images.ImageSharpOptimizer>();
        services.AddHttpClient<IRemoteImageFetcher, Images.RemoteImageFetcher>(c => c.Timeout = TimeSpan.FromSeconds(5));

        // Drawing a QR is pure computation over a string — no state, no connection, nothing to scope.
        services.AddSingleton<IQrCodeRenderer, QrCodes.QrCoderRenderer>();

        // OTP senders (email + sms), resolved by channel. Only channels in Otp:Channels are enabled.
        // SMS goes through MsgOwl once Sms:MsgOwl:ApiKey is set; without a key we fall back to the
        // console sender so local development still shows the code instead of failing.
        services.AddScoped<EmailOtpSender>();
        services.AddScoped<IOtpSender>(sp => sp.GetRequiredService<EmailOtpSender>());

        if (!string.IsNullOrWhiteSpace(config[$"{MsgOwlSmsOtpSender.ConfigSection}:ApiKey"]))
        {
            services.AddScoped<MsgOwlSmsOtpSender>();
            services.AddScoped<IOtpSender>(sp => sp.GetRequiredService<MsgOwlSmsOtpSender>());
        }
        else
        {
            services.AddScoped<ConsoleSmsOtpSender>();
            services.AddScoped<IOtpSender>(sp => sp.GetRequiredService<ConsoleSmsOtpSender>());
        }

        // Delivery: each guest is sent their own tokenized /i/{token} link, by Viber when they have a
        // phone number and Viber is configured, else by email (Application.Delivery.GuestRoute). The
        // first send (CampaignService.FinalizeAsync) and resends (DispatchService) share these providers.
        services.AddScoped<IInviteDeliveryProvider, EmailInviteDeliveryProvider>();
        if (!string.IsNullOrWhiteSpace(config["Infobip:ApiKey"]) && !string.IsNullOrWhiteSpace(config["Infobip:BaseUrl"]))
        {
            services.AddHttpClient<InfobipViberSender>(c =>
            {
                // Ends in "/" so request paths stay relative to it (an account host can carry a path).
                c.BaseAddress = new Uri(config["Infobip:BaseUrl"]!.TrimEnd('/') + "/");
                c.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("App", config["Infobip:ApiKey"]);
                c.Timeout = TimeSpan.FromSeconds(15);
            });
            services.AddScoped<IInviteDeliveryProvider>(sp => sp.GetRequiredService<InfobipViberSender>());
        }
        // Without a key there is no Viber provider at all, so every guest is emailed: a log-only stand-in
        // would "succeed" and swallow the invitations of everyone with a phone number.
        services.AddScoped<Application.Services.Delivery.IInfobipReportHandler, InfobipReportHandler>();

        services.AddScoped<DispatchService>();
        services.AddScoped<Payments.PaymentOutcomes>();

        // Payments: Bank of Maldives (BML Connect) when configured, the no-network fake otherwise.
        if (string.Equals(config["Payments:Provider"], "Bml", StringComparison.OrdinalIgnoreCase))
        {
            // No resilience handler on purpose: a retried "create transaction" or "charge card" is a
            // second charge. BmlPaymentProvider lets a failure surface instead.
            services.AddHttpClient(BmlPaymentProvider.ClientName, c =>
            {
                c.BaseAddress = new Uri((config["Payments:Bml:BaseUrl"] ?? BmlPaymentProvider.UatBaseUrl).TrimEnd('/') + "/");
                c.Timeout = TimeSpan.FromSeconds(30);
            });
            services.AddSingleton<BmlPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<BmlPaymentProvider>());
            services.AddSingleton<IRecurringPaymentProvider>(sp => sp.GetRequiredService<BmlPaymentProvider>());
        }
        else
        {
            services.AddSingleton<FakePaymentProvider>();
            services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<FakePaymentProvider>());
            services.AddSingleton<IRecurringPaymentProvider>(sp => sp.GetRequiredService<FakePaymentProvider>());
        }

        return services;
    }
}
