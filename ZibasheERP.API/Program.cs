using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using ZibasheERP.API.Authentication;
using ZibasheERP.API.Data;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Health;
using ZibasheERP.API.Diagnostics;
using ZibasheERP.API.N8n;
using ZibasheERP.Application.Behaviors;
using ZibasheERP.Application.Features.Orders.CreateOrder;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

// Controllers
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")));

// MediatR
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(CreateOrderCommand).Assembly);
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
});

// FluentValidation
builder.Services.AddValidatorsFromAssembly(typeof(CreateOrderValidator).Assembly);

// Repositories
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<IAdminCustomerRepository, CustomerRepository>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<ISalesListRepository, SalesListRepository>();
builder.Services.AddScoped<ISalesListRequestRepository, SalesListRequestRepository>();
builder.Services.AddScoped<IBottleRepository, BottleRepository>();
builder.Services.AddScoped<IBatchRepository, BatchRepository>();
builder.Services.AddScoped<IPerfumeRepository, PerfumeRepository>();
builder.Services.AddScoped<IReportingRepository, ReportingRepository>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IInvoiceRepository, InvoiceRepository>();
builder.Services.AddScoped<IShipmentRepository, ShipmentRepository>();
builder.Services.AddScoped<INotificationOutboxRepository, NotificationOutboxRepository>();
builder.Services.AddScoped<IAdminNotificationRepository, NotificationOutboxRepository>();
builder.Services.AddScoped<IAddressRepository, AddressRepository>();
builder.Services.AddScoped<ITelegramOrderDraftRepository, TelegramOrderDraftRepository>();
builder.Services.AddScoped<IOrderArtifactRepository, OrderArtifactRepository>();
builder.Services.AddScoped<IInvoicePaymentAccountRepository, InvoicePaymentAccountRepository>();

builder.Services.AddOptions<TelegramOptions>()
    .Bind(builder.Configuration.GetSection(TelegramOptions.SectionName))
    .Validate(options => !options.Enabled ||
        (!string.IsNullOrWhiteSpace(options.BotToken) &&
         !string.IsNullOrWhiteSpace(options.WebhookSecret) &&
         (builder.Environment.IsDevelopment() || options.WebhookSecret.Length >= 32) &&
         (builder.Environment.IsDevelopment() ||
          (options.WebhookSecret.Length <= 256 && options.WebhookSecret.All(character =>
              char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))) &&
         (builder.Environment.IsDevelopment() ||
          (long.TryParse(options.AdminChatId, out var adminChatId) && adminChatId != 0)) &&
         options.PollIntervalSeconds is >= 1 and <= 300 &&
         options.BatchSize is >= 1 and <= 100 &&
         options.MaxAttempts is >= 1 and <= 20),
        "Enabled Telegram integration has invalid or missing settings. Production also requires a valid AdminChatId.")
    .ValidateOnStart();
builder.Services.AddOptions<ApiKeyOptions>()
    .Bind(builder.Configuration.GetSection(ApiKeyOptions.SectionName))
    .Validate(options => builder.Environment.IsDevelopment() ||
        options.IsValid(builder.Configuration.GetValue<bool>("N8n:Enabled")),
        "Production API keys must be distinct and at least 32 characters long.")
    .ValidateOnStart();
builder.Services.AddOptions<N8nOptions>()
    .Bind(builder.Configuration.GetSection(N8nOptions.SectionName))
    .Validate(options => !options.Enabled ||
        (Uri.TryCreate(options.WebhookUrl, UriKind.Absolute, out var webhookUri) &&
         (builder.Environment.IsDevelopment() || webhookUri.Scheme == Uri.UriSchemeHttps) &&
         options.WebhookSecret.Length >= 32 &&
         options.PollIntervalSeconds is >= 1 and <= 300 &&
         options.BatchSize is >= 1 and <= 100 &&
         options.MaxAttempts is >= 1 and <= 20),
        "Enabled n8n integration has invalid or missing settings.")
    .ValidateOnStart();
builder.Services.AddSingleton<ITelegramMessageSender, TelegramMessageSender>();
builder.Services.AddSingleton<ITelegramUpdateDeduplicator, TelegramUpdateDeduplicator>();
builder.Services.AddSingleton<TelegramAdminSalesListDraftStore>();
builder.Services.AddSingleton<TelegramOwnerPricingDraftStore>();
builder.Services.AddSingleton<TelegramAdminRequestDraftStore>();
builder.Services.AddSingleton<TelegramTemporaryMessageCleaner>();
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<TelegramTemporaryMessageCleaner>());
builder.Services.AddScoped<TelegramUpdateDeduplicationFilter>();
builder.Services.AddScoped<ITelegramGroupMembershipTracker, TelegramGroupMembershipTracker>();
builder.Services.AddHostedService<TelegramOutboxWorker>();
builder.Services.AddSingleton<IN8nWebhookSender, N8nWebhookSender>();
builder.Services.AddHostedService<N8nOutboxWorker>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("telegram-webhook", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services
    .AddAuthentication(ApiKeyAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationDefaults.Scheme,
        _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (app.Environment.IsDevelopment())
        await SeedData.InitializeAsync(db);
    else
        await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthCheckResponseWriter.WriteAsync
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter.WriteAsync
});

app.Run();
