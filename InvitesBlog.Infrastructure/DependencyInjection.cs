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

        // Invitee JWT (issuer + validation params), exposed to Application via IInviteeTokenIssuer.
        services.AddSingleton<Security.InviteeJwt>();
        services.AddSingleton<IInviteeTokenIssuer>(sp => sp.GetRequiredService<Security.InviteeJwt>());

        // Storage (Local by default; MinIO/S3 when configured)
        var storageProvider = config["Storage:Provider"] ?? "Local";
        if (storageProvider.Equals("Minio", StringComparison.OrdinalIgnoreCase) ||
            storageProvider.Equals("S3", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IStorageService, S3StorageService>();
        else
            services.AddSingleton<IStorageService, LocalFileStorageService>();

        services.AddScoped<TemplatePackagePublisher>();
        services.AddScoped<TemplateSeeder>();
        services.AddScoped<Rendering.InviteRenderService>();
        services.AddScoped<IInviteRenderer>(sp => sp.GetRequiredService<Rendering.InviteRenderService>());

        // Email
        services.AddSingleton<IEmailSender, ConsoleEmailSender>();

        // OTP senders (sms + email), resolved by channel
        services.AddSingleton<ConsoleSmsOtpSender>();
        services.AddSingleton<EmailOtpSender>();
        services.AddSingleton<IOtpSender>(sp => sp.GetRequiredService<ConsoleSmsOtpSender>());
        services.AddSingleton<IOtpSender>(sp => sp.GetRequiredService<EmailOtpSender>());

        // Delivery providers (email real; others logged for now)
        services.AddSingleton<IInviteDeliveryProvider, EmailInviteDeliveryProvider>();
        services.AddSingleton<IInviteDeliveryProvider>(sp =>
            new LogInviteDeliveryProvider("telegram", sp.GetRequiredService<ILoggerFactory>().CreateLogger("telegram")));
        services.AddSingleton<IInviteDeliveryProvider>(sp =>
            new LogInviteDeliveryProvider("sms", sp.GetRequiredService<ILoggerFactory>().CreateLogger("sms")));
        services.AddScoped<DispatchService>();
        services.AddScoped<IInviteDispatcher>(sp => sp.GetRequiredService<DispatchService>());

        // Payments
        services.AddSingleton<IPaymentProvider, FakePaymentProvider>();

        return services;
    }
}
