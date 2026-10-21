using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Trackdub.Cloud.Jobs;

namespace Trackdub.Cloud.Aws;

/// <summary>Single-table durable authority. Discovery is advisory; base-table CAS controls every mutation.</summary>
public sealed class DynamoDbJobStore(IAmazonDynamoDB client, string tableName) : IJobStore
{
    private Dictionary<string, AttributeValue>? _cursor;
    private static Dictionary<string, AttributeValue> Key(string id) => new() { ["pk"] = new() { S = id } };
    private static string JobKey(Guid id) => $"job#{id}";
    private static string UploadKey(Guid id) => $"upload#{id}";
    private static string IdempotencyKey(string owner, string key) => "idempotency#" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { owner, key }))));

    private async Task<Dictionary<string, AttributeValue>?> ReadAsync(string key, CancellationToken token)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        { TableName = tableName, Key = Key(key), ConsistentRead = true }, token).ConfigureAwait(false);
        return response.Item is { Count: > 0 } ? response.Item : null;
    }
    private static T Decode<T>(Dictionary<string, AttributeValue> item) =>
        JsonSerializer.Deserialize<T>(item["payload"].S, CloudJson.Options)!;
    private static Dictionary<string, AttributeValue> Encode<T>(string key, T value)
    {
        string payload = JsonSerializer.Serialize(value, CloudJson.Options);
        if (Encoding.UTF8.GetByteCount(payload) > 128 * 1024)
            throw new JobException("INVALID_INPUT", 400, "Job metadata exceeds the supported limit.");
        var item = Key(key);
        item["payload"] = new() { S = payload };
        return item;
    }
    private static Dictionary<string, AttributeValue> EncodeJob(JobRecord job)
    {
        var item = Encode(JobKey(job.Id), job);
        item["revision"] = new() { N = job.Version.ToString(CultureInfo.InvariantCulture) };
        if (job.ExecutionId is not null && job.LeaseExpiresAt is not null)
        {
            item["activeExecution"] = new() { S = job.ExecutionId };
            item["activeAttempt"] = new() { S = job.AttemptId.ToString() };
            item["activeGeneration"] = new() { N = job.Generation.ToString(CultureInfo.InvariantCulture) };
            item["leaseUntil"] = new() { N = job.LeaseExpiresAt.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) };
        }
        if (!job.IsTerminal)
        {
            item["work"] = new() { S = "active" };
            item["created"] = new() { N = job.CreatedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) };
        }
        return item;
    }
    public async Task<JobRecord?> GetAsync(Guid id, CancellationToken token)
    {
        var item = await ReadAsync(JobKey(id), token).ConfigureAwait(false);
        return item is null ? null : Decode<JobRecord>(item);
    }
    public async Task<JobRecord?> FindSubmissionAsync(string ownerId, string key, string hash, CancellationToken token)
    {
        var item = await ReadAsync(IdempotencyKey(ownerId, key), token).ConfigureAwait(false);
        if (item is null) return null;
        if (item["hash"].S != hash) throw new JobException("IDEMPOTENCY_CONFLICT", 409, "Idempotency key was used for a different request.");
        return await GetAsync(Guid.Parse(item["jobId"].S), token).ConfigureAwait(false)
            ?? throw new JobException("STORE_UNAVAILABLE", 503, "Accepted job is temporarily unavailable.");
    }
    public async Task<bool> AcceptAsync(JobRecord job, string idempotencyKey, Guid uploadId, CancellationToken token)
    {
        var mapping = Key(IdempotencyKey(job.OwnerId, idempotencyKey));
        mapping["hash"] = new() { S = job.RequestHash };
        mapping["jobId"] = new() { S = job.Id.ToString() };
        var intent = Encode($"outbox#{job.DispatchId}", new DispatchRequest(job.Id, job.AttemptId, job.DispatchId));
        var writes = new[] { EncodeJob(job), mapping, intent }.Select(item => new TransactWriteItem
        {
            Put = new Put { TableName = tableName, Item = item, ConditionExpression = "attribute_not_exists(pk)" },
        }).ToList();
        writes.Add(new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = tableName, Key = Key(UploadKey(uploadId)),
                ConditionExpression = "#ready = :ready AND #owner = :owner AND #identity = :identity",
                ExpressionAttributeNames = new() { ["#ready"] = "ready", ["#owner"] = "owner", ["#identity"] = "identity" },
                ExpressionAttributeValues = new()
                {
                    [":ready"] = new() { BOOL = true }, [":owner"] = new() { S = job.OwnerId },
                    [":identity"] = new() { S = JsonSerializer.Serialize(job.Input, CloudJson.Options) },
                },
            },
        });
        try
        {
            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, token).ConfigureAwait(false);
            return true;
        }
        catch (TransactionCanceledException ex) when (ex.CancellationReasons?.Any(r => r.Code == "ConditionalCheckFailed") == true)
        { return false; }
    }
    public async Task<bool> CompareExchangeAsync(JobRecord next, long expectedVersion, CancellationToken token, ExecutionClaim? claim = null)
    {
        var values = new Dictionary<string, AttributeValue> { [":expected"] = new() { N = expectedVersion.ToString(CultureInfo.InvariantCulture) } };
        string condition = "revision = :expected";
        if (claim is not null)
        {
            condition += " AND activeExecution = :execution AND activeAttempt = :attempt AND activeGeneration = :generation AND leaseUntil > :now";
            values[":execution"] = new() { S = claim.ExecutionId };
            values[":attempt"] = new() { S = claim.AttemptId.ToString() };
            values[":generation"] = new() { N = claim.Generation.ToString(CultureInfo.InvariantCulture) };
            values[":now"] = new() { N = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) };
        }
        try
        {
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = tableName, Item = EncodeJob(next), ConditionExpression = condition,
                ExpressionAttributeValues = values,
            }, token).ConfigureAwait(false);
            return true;
        }
        catch (ConditionalCheckFailedException) { return false; }
    }
    // Called by one bounded host loop. Continue across pages so a slow old job cannot starve newer work.
    public async Task<IReadOnlyList<Guid>> DiscoverAsync(int limit, CancellationToken token)
    {
        var response = await client.QueryAsync(new QueryRequest
        {
            TableName = tableName, IndexName = "pending-work", KeyConditionExpression = "#work = :active",
            ExpressionAttributeNames = new() { ["#work"] = "work" },
            ExpressionAttributeValues = new() { [":active"] = new() { S = "active" } },
            Limit = Math.Clamp(limit, 1, 100), ExclusiveStartKey = _cursor,
        }, token).ConfigureAwait(false);
        _cursor = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        return (response.Items ?? []).Select(i => Guid.Parse(i["pk"].S[4..])).ToArray();
    }
    public async Task<UploadRecord?> GetUploadAsync(Guid id, CancellationToken token)
    {
        var item = await ReadAsync(UploadKey(id), token).ConfigureAwait(false);
        return item is null ? null : Decode<UploadRecord>(item);
    }
    public async Task<bool> PutUploadAsync(UploadRecord record, bool complete, CancellationToken token)
    {
        var item = Encode(UploadKey(record.Id), record);
        item["owner"] = new() { S = record.OwnerId };
        item["ready"] = new() { BOOL = complete };
        if (record.Object is not null) item["identity"] = new() { S = JsonSerializer.Serialize(record.Object, CloudJson.Options) };
        try
        {
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = tableName, Item = item,
                ConditionExpression = complete ? "#ready = :false AND #owner = :owner" : "attribute_not_exists(pk)",
                ExpressionAttributeNames = complete ? new() { ["#ready"] = "ready", ["#owner"] = "owner" } : null,
                ExpressionAttributeValues = complete ? new() { [":false"] = new() { BOOL = false }, [":owner"] = new() { S = record.OwnerId } } : null,
            }, token).ConfigureAwait(false);
            return true;
        }
        catch (ConditionalCheckFailedException) { return false; }
    }
}
