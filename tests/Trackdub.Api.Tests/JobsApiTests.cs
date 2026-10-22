using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Trackdub.Cloud.Jobs;
using Trackdub.TestDoubles;

namespace Trackdub.Api.Tests;

public sealed class JobsApiTests
{
    private sealed class Factory(bool processing = true) : WebApplicationFactory<Program>
    {
        public FakeJobStore Store { get; } = new();
        public RsaSecurityKey Key { get; } = new(RSA.Create(2048)) { KeyId = "test-key" };
        protected override void Dispose(bool disposing)
        {
            if (disposing) Key.Rsa?.Dispose();
            base.Dispose(disposing);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Cloud:JwtIssuer", "https://issuer.example");
            builder.UseSetting("Cloud:JwtAudience", "test-api");
            builder.UseSetting("Cloud:JwtClientId", "poc-client");
            builder.UseSetting("Cloud:TableName", "unused");
            builder.UseSetting("Cloud:BucketName", "unused");
            builder.UseSetting("Cloud:ProcessingEnabled", processing.ToString());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJobStore>(); services.RemoveAll<IArtifactStore>();
                services.AddSingleton<IJobStore>(Store); services.AddSingleton<IArtifactStore>(new FakeCloudArtifactStore());
                services.AddSingleton<IJobScheduler>(new FakeJobScheduler());
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var config = new OpenIdConnectConfiguration { Issuer = "https://issuer.example" };
                    config.SigningKeys.Add(Key);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                });
            });
        }
        public string Token(string owner = "owner", string issuer = "https://issuer.example", string audience = "test-api", bool expired = false, bool forged = false,
            string tokenUse = "access", string clientId = "poc-client")
        {
            using var forgedRsa = forged ? RSA.Create(2048) : null;
            var jwt = new JwtSecurityToken(issuer, audience, [new Claim("sub", owner), new Claim("token_use", tokenUse), new Claim("client_id", clientId)], DateTime.UtcNow.AddHours(-2),
                expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1),
                new SigningCredentials(forgedRsa is null ? Key : new RsaSecurityKey(forgedRsa) { KeyId = Key.KeyId }, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(jwt);
        }
    }

    [Theory]
    [InlineData("access", "poc-client", HttpStatusCode.NotFound)]
    [InlineData("access", "wrong-client", HttpStatusCode.Unauthorized)]
    [InlineData("id", "poc-client", HttpStatusCode.Unauthorized)]
    public async Task CognitoAccessTokensWithoutAudienceBindTheConfiguredClient(string use, string clientId, HttpStatusCode expected)
    {
        await using var factory = new Factory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(audience: "", tokenUse: use, clientId: clientId));
        Assert.Equal(expected, (await client.GetAsync($"/jobs/{Guid.NewGuid()}", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("forged")]
    [InlineData("issuer")]
    [InlineData("audience")]
    public async Task InvalidJwtCannotReadJobs(string mode)
    {
        await using var factory = new Factory(); using var client = factory.CreateClient();
        if (mode != "missing") client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(
            expired: mode == "expired", forged: mode == "forged", issuer: mode == "issuer" ? "https://wrong.example" : "https://issuer.example", audience: mode == "audience" ? "wrong-api" : "test-api"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/jobs/{Guid.NewGuid()}", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(factory.Store.Jobs);
    }

    [Fact]
    public async Task AcceptedSubmissionIsDurableBefore202AndForeignOwnerGets404()
    {
        await using var factory = new Factory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        client.DefaultRequestHeaders.Add("Idempotency-Key", "request-1");
        var id = Guid.NewGuid();
        factory.Store.Uploads[id] = new(id, "owner", new("input", "v1", 10, new string('a', 64)), "input", 10, new string('a', 64), "video/mp4", DateTimeOffset.UtcNow.AddMinutes(15));
        var request = new CreateJobRequest(1, "Project", new(id), "en", "es", "poc-server-v1");
        var response = await client.PostAsJsonAsync("/jobs", request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); Assert.Single(factory.Store.Jobs); Assert.Single(factory.Store.Outbox);
        string location = response.Headers.Location!.ToString();
        var owned = await client.GetAsync(location, TestContext.Current.CancellationToken); Assert.Equal(HttpStatusCode.OK, owned.StatusCode);
        Assert.DoesNotContain("versionId", await owned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("foreign"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(location, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task FailedTransactionReturns503WithoutAcceptance()
    {
        await using var factory = new Factory(); using var client = factory.CreateClient();
        factory.Store.FailAcceptance = true;
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token()); client.DefaultRequestHeaders.Add("Idempotency-Key", "key");
        var id = Guid.NewGuid(); factory.Store.Uploads[id] = new(id, "owner", new("input", "v1", 10, new string('a', 64)), "input", 10, new string('a', 64), "video/mp4", DateTimeOffset.UtcNow.AddMinutes(15));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/jobs", new CreateJobRequest(1, "P", new(id), "en", "es", "poc-server-v1"), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(factory.Store.Jobs); Assert.Empty(factory.Store.Outbox);
    }

    [Fact]
    public async Task DisabledProcessorRejectsIntake()
    {
        await using var factory = new Factory(false); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/uploads", new UploadRequest(10, new string('a', 64), "video/mp4"), TestContext.Current.CancellationToken)).StatusCode);
    }
}
