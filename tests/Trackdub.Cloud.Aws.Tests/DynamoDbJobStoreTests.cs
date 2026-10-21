using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Trackdub.Cloud.Aws;
using Trackdub.Cloud.Jobs;

namespace Trackdub.Cloud.Aws.Tests;

public sealed class DynamoDbJobStoreTests
{
    [Fact]
    public async Task LocalTransactionsAreAtomicDurableAndConditional()
    {
        string? endpoint = Environment.GetEnvironmentVariable("TRACKDUB_DYNAMODB_LOCAL");
        Assert.SkipWhen(endpoint is null, "Set TRACKDUB_DYNAMODB_LOCAL to the local DynamoDB fixture URL.");
        var uri = new Uri(endpoint!);
        Assert.True(uri.IsLoopback && uri.Scheme == "http", "Integration fixture must be loopback HTTP; real AWS is forbidden.");
        using var client = new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"), new AmazonDynamoDBConfig { ServiceURL = uri.ToString(), AuthenticationRegion = "us-east-1" });
        var token = TestContext.Current.CancellationToken;
        string table = "snapsoft-tests-" + Guid.NewGuid().ToString("N");
        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = table, BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = [new("pk", KeyType.HASH)],
            AttributeDefinitions = [new("pk", ScalarAttributeType.S), new("work", ScalarAttributeType.S), new("created", ScalarAttributeType.N)],
            GlobalSecondaryIndexes = [new() { IndexName = "pending-work", KeySchema = [new("work", KeyType.HASH), new("created", KeyType.RANGE)], Projection = new() { ProjectionType = ProjectionType.KEYS_ONLY } }],
        }, token);
        try
        {
            var store = new DynamoDbJobStore(client, table);
            var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            var input = new ObjectReference("input", "v1", 10, new string('a', 64));
            var job = new JobRecord { Id = Guid.NewGuid(), OwnerId = "owner", RequestHash = "hash",
                Request = new(1, "P", new(id), "en", "es", "poc-server-v1"), Input = input,
                CreatedAt = now, UpdatedAt = now, QueueDeadlineAt = now.AddHours(1) };
            // The upload condition fails: all three creates must roll back in the actual local service.
            Assert.False(await store.AcceptAsync(job, "key", id, token));
            Assert.Null(await store.GetAsync(job.Id, token));
            Assert.Null(await store.FindSubmissionAsync("owner", "key", "hash", token));
            Assert.Empty((await client.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true }, token)).Items);
            Assert.True(await store.PutUploadAsync(new(id, "owner", null, "input", 10, new string('a', 64), "video/mp4", now.AddMinutes(15)), false, token));
            Assert.True(await store.PutUploadAsync(new(id, "owner", input, "input", 10, new string('a', 64), "video/mp4", now.AddMinutes(15)), true, token));
            Assert.True(await store.AcceptAsync(job, "key", id, token));
            Assert.False(await store.AcceptAsync(job with { Id = Guid.NewGuid() }, "key", id, token));
            var restarted = new DynamoDbJobStore(client, table);
            Assert.Equal(job.Id, (await restarted.FindSubmissionAsync("owner", "key", "hash", token))!.Id);
            Assert.Equal(409, (await Assert.ThrowsAsync<JobException>(() => restarted.FindSubmissionAsync("owner", "key", "changed", token))).StatusCode);
            var mutations = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.CompareExchangeAsync(job with { Version = 1, State = JobState.Running }, 0, token)));
            Assert.Single(mutations, won => won);
            Assert.Equal(1, (await restarted.GetAsync(job.Id, token))!.Version);
            var expired = job with { Version = 2, State = JobState.Running, ExecutionId = "execution", Generation = 1, LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
            Assert.True(await store.CompareExchangeAsync(expired, 1, token));
            var claim = new ExecutionClaim(job.Id, job.AttemptId, job.DispatchId, "execution", 1);
            Assert.False(await store.CompareExchangeAsync(expired with { Version = 3, LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(90) }, 2, token, claim));
        }
        finally { await client.DeleteTableAsync(table, token); }
    }
}
