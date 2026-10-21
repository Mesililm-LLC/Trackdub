using System.Security.Claims;
using Amazon.DynamoDBv2;
using Amazon.S3;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Trackdub.Cloud.Aws;
using Trackdub.Cloud.Jobs;

var builder = WebApplication.CreateBuilder(args);
string Required(string key) => builder.Configuration[key] is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Missing required configuration: {key}");
string issuer = Required("Cloud:JwtIssuer");
string audience = Required("Cloud:JwtAudience");
if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != "https")
    throw new InvalidOperationException("Cloud:JwtIssuer must be HTTPS.");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16384);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());
string table = Required("Cloud:TableName");
string bucket = Required("Cloud:BucketName");
builder.Services.AddSingleton<IJobStore>(services => new DynamoDbJobStore(services.GetRequiredService<IAmazonDynamoDB>(), table));
builder.Services.AddSingleton<IArtifactStore>(services => new S3ArtifactStore(services.GetRequiredService<IAmazonS3>(), bucket, services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<JobService>();
builder.Services.AddSingleton<UploadService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = issuer;
    options.Audience = audience;
    options.MapInboundClaims = false;
    options.RequireHttpsMetadata = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = issuer, ValidateAudience = true, ValidAudience = audience,
        ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
        ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.FromSeconds(30),
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        AudienceValidator = (audiences, securityToken, _) =>
        {
            string[] supplied = audiences?.ToArray() ?? [];
            if (supplied.Length > 0) return supplied.Contains(audience, StringComparer.Ordinal);
            string? client = securityToken switch
            {
                System.IdentityModel.Tokens.Jwt.JwtSecurityToken jwt => jwt.Claims.FirstOrDefault(c => c.Type == "client_id")?.Value,
                Microsoft.IdentityModel.JsonWebTokens.JsonWebToken jwt => jwt.TryGetClaim("client_id", out var claim) ? claim.Value : null,
                _ => null,
            };
            return builder.Configuration["Cloud:JwtClientId"] is { Length: > 0 } expected && client == expected;
        },
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            if (context.Principal?.FindFirstValue("token_use") != "access" || string.IsNullOrEmpty(context.Principal.FindFirstValue("sub")))
                context.Fail("An access token with a subject is required.");
            return Task.CompletedTask;
        },
    };
});
builder.Services.AddAuthorization();
string[] origins = builder.Configuration.GetSection("Cloud:AllowedOrigins").Get<string[]>() ?? [];
if (origins.Any(o => o.Contains('*') || !Uri.TryCreate(o, UriKind.Absolute, out _)))
    throw new InvalidOperationException("CORS requires exact origins.");
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(origins)
    .WithMethods("GET", "POST").WithHeaders("Authorization", "Content-Type", "Idempotency-Key")
    .WithExposedHeaders("Location", "Retry-After")));
builder.Services.AddHostedService<JobControlBackgroundService>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (BadHttpRequestException ex)
    {
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = new { code = "INVALID_INPUT", message = "Request body is invalid.", requestId = context.TraceIdentifier } });
    }
    catch (JobException ex)
    {
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = new { code = ex.Code, message = ex.Message, requestId = context.TraceIdentifier } });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        app.Logger.LogError(ex, "Cloud operation failed: {RequestId}", context.TraceIdentifier);
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { error = new { code = "SERVICE_UNAVAILABLE", message = "Operation unavailable. Retry safely.", requestId = context.TraceIdentifier } });
    }
});
app.UseCors();
app.UseStatusCodePages(async status =>
    await status.HttpContext.Response.WriteAsJsonAsync(new { error = new
    { code = status.HttpContext.Response.StatusCode == 401 ? "UNAUTHORIZED" : "REQUEST_REJECTED", message = "Request rejected.", requestId = status.HttpContext.TraceIdentifier } }));
app.UseAuthentication();
app.UseAuthorization();
static string Owner(ClaimsPrincipal user) => user.FindFirstValue("sub") is { Length: > 0 } subject
    ? subject : throw new JobException("UNAUTHORIZED", 401, "Token requires a subject.");
bool IntakeEnabled() => app.Configuration.GetValue<bool>("Cloud:ProcessingEnabled")
    && app.Services.GetService<IJobScheduler>() is not null;
static void RequireIntake(bool enabled)
{
    if (!enabled) throw new JobException("PROCESSOR_DISABLED", 503, "Job intake awaits scheduler and preset qualification.");
}
app.MapGet("/health", () => Results.Ok(new { status = "available", processingEnabled = IntakeEnabled() }));
var api = app.MapGroup("").RequireAuthorization();
api.MapPost("/jobs", async (CreateJobRequest request, HttpContext context, JobService jobs, CancellationToken token) =>
{
    RequireIntake(IntakeEnabled());
    if (context.Request.Headers["Idempotency-Key"].Count != 1) throw new JobException("INVALID_INPUT", 400, "One Idempotency-Key is required.");
    var job = await jobs.SubmitAsync(Owner(context.User), context.Request.Headers["Idempotency-Key"].ToString(), request, token);
    context.Response.Headers.RetryAfter = "5";
    return Results.Accepted($"/jobs/{job.Id}", new { job = JobDto.FromRecord(job) });
});
api.MapGet("/jobs/{id:guid}", async (Guid id, HttpContext context, JobService jobs, CancellationToken token) =>
    Results.Ok(new { job = JobDto.FromRecord(await jobs.GetOwnedAsync(Owner(context.User), id, token)) }));
api.MapPost("/jobs/{id:guid}/cancel", async (Guid id, HttpContext context, JobService jobs, CancellationToken token) =>
{
    var job = await jobs.CancelAsync(Owner(context.User), id, token);
    return job.IsTerminal ? Results.Ok(new { job = JobDto.FromRecord(job) }) : Results.Accepted($"/jobs/{id}", new { job = JobDto.FromRecord(job) });
});
api.MapGet("/jobs/{id:guid}/artifacts", async (Guid id, HttpContext context, JobService jobs, IArtifactStore store, CancellationToken token) =>
{
    var job = await jobs.GetOwnedAsync(Owner(context.User), id, token);
    var items = new List<object>();
    foreach (var artifact in job.Artifacts)
        items.Add(new { artifact.Id, artifact.Kind, artifact.FileName, artifact.ContentType, artifact.Object.SizeBytes,
            artifact.Object.Sha256, available = true, downloadUrl = await store.SignDownloadAsync(artifact.Object, token), expiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
    return Results.Ok(new { items });
});
api.MapPost("/uploads", async (UploadRequest request, HttpContext context, UploadService uploads, CancellationToken token) =>
{
    RequireIntake(IntakeEnabled());
    return Results.Ok(await uploads.CreateAsync(Owner(context.User), request, token));
});
api.MapPost("/uploads/{id:guid}/complete", async (Guid id, UploadCompletion request, HttpContext context, UploadService uploads, CancellationToken token) =>
{
    var upload = await uploads.CompleteAsync(Owner(context.User), id, request.VersionId, token);
    return Results.Ok(new { uploadId = upload.Id, ready = true });
});
app.Run();

public sealed record UploadCompletion(string VersionId);
public partial class Program;

internal sealed class JobControlBackgroundService(IServiceProvider services, ILogger<JobControlBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scheduler = services.GetService<IJobScheduler>();
        if (scheduler is null) { logger.LogWarning("No scheduler selected. Dispatch and intake disabled."); return; }
        var controller = new JobController(services.GetRequiredService<IJobStore>(), services.GetRequiredService<JobService>(),
            scheduler, services.GetRequiredService<IArtifactStore>(), services.GetRequiredService<TimeProvider>(),
            (id, error) => logger.LogError(error, "Job control failed for {JobId}; next pass will retry persisted work.", id));
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { using var bound = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); bound.CancelAfter(TimeSpan.FromSeconds(50)); await controller.TickAsync(bound.Token); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Bounded job control pass failed; persisted work remains discoverable."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
