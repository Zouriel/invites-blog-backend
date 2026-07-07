using InvitesBlog.Infrastructure;
using InvitesBlog.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddInvitesBlogInfrastructure(builder.Configuration);
builder.Services.AddHostedService<RetentionCleanupService>();

var host = builder.Build();
host.Run();
