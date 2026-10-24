using ClaimPortal.Notifications;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Everything the worker needs is registered in these two calls; see
// NotificationsServiceCollectionExtensions for the reasoning behind each lifetime.
builder.Services.AddNotificationServices(builder.Configuration);
builder.Services.AddNotificationMessaging(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
