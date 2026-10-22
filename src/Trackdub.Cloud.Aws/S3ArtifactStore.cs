using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Trackdub.Cloud.Jobs;
using Trackdub.Contracts.Jobs;

namespace Trackdub.Cloud.Aws;

public sealed class S3ArtifactStore(IAmazonS3 client, string bucket) : IArtifactStore
{
    private static void Validate(ObjectReference reference)
    {
        if (string.IsNullOrWhiteSpace(reference.Key) || string.IsNullOrWhiteSpace(reference.VersionId)
            || reference.VersionId == "null" || reference.SizeBytes <= 0 || reference.Sha256.Length != 64
            || !reference.Sha256.All(Uri.IsHexDigit))
            throw new JobException("INPUT_CHANGED", 409, "Object has no immutable verified identity.");
    }

    public async Task VerifyAsync(ObjectReference reference, CancellationToken token)
    {
        await using var target = Stream.Null;
        await CopyVerifiedAsync(reference, target, token).ConfigureAwait(false);
    }

    private async Task CopyVerifiedAsync(ObjectReference reference, Stream target, CancellationToken token)
    {
        Validate(reference);
        GetObjectResponse downloaded;
        try
        {
            downloaded = await client.GetObjectAsync(new GetObjectRequest
            { BucketName = bucket, Key = reference.Key, VersionId = reference.VersionId }, token).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { throw new JobException("INPUT_CHANGED", 409, "Immutable object version is unavailable."); }
        using var response = downloaded;
        if (response.VersionId != reference.VersionId || response.Headers.ContentLength != reference.SizeBytes)
            throw new JobException("INPUT_CHANGED", 409, "Object version or size changed.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = await response.ResponseStream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            copied += count;
            if (copied > reference.SizeBytes) throw new JobException("INPUT_CHANGED", 409, "Object exceeded expected size.");
            hash.AppendData(buffer, 0, count);
            await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        if (copied != reference.SizeBytes || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), reference.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new JobException("INPUT_CHANGED", 409, "Object checksum verification failed.");
    }

    public Task<string> SignDownloadAsync(ObjectReference reference, DateTimeOffset expiresAt, CancellationToken token)
    {
        Validate(reference);
        return client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = reference.Key,
            VersionId = reference.VersionId,
            Verb = HttpVerb.GET,
            Expires = expiresAt.UtcDateTime,
        });
    }

    public Task<string> SignUploadAsync(UploadRecord upload, CancellationToken token)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = upload.Key,
            Verb = HttpVerb.PUT,
            ContentType = upload.ContentType,
            Expires = upload.ExpiresAt.UtcDateTime,
        };
        request.Headers["x-amz-checksum-sha256"] = Convert.ToBase64String(Convert.FromHexString(upload.Sha256));
        request.Headers.ContentLength = upload.SizeBytes;
        return client.GetPreSignedURLAsync(request);
    }

    public async Task<ObjectReference> CompleteUploadAsync(UploadRecord upload, string versionId, CancellationToken token)
    {
        var reference = new ObjectReference(upload.Key, versionId, upload.SizeBytes, upload.Sha256);
        string temporary = Path.Join(Path.GetTempPath(), "trackdub-upload-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            await DownloadAsync(reference, temporary, token).ConfigureAwait(false);
            await MediaValidator.ValidateAsync(temporary, token).ConfigureAwait(false);
        }
        finally { File.Delete(temporary); }
        return reference;
    }

    public async Task DownloadAsync(ObjectReference reference, string destination, CancellationToken token)
    {
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await CopyVerifiedAsync(reference, target, token).ConfigureAwait(false);
    }

    public async Task<ObjectReference> UploadAsync(string key, string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        stream.Position = 0;
        var response = await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            AutoCloseStream = false,
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
            ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(hash)),
        }, token).ConfigureAwait(false);
        // S3 validates ChecksumSHA256 on PUT; re-downloading large outputs would spend the attempt deadline.
        var reference = new ObjectReference(key, response.VersionId, stream.Length, hash);
        Validate(reference);
        return reference;
    }

    public async Task<WorkerRunOutcome> ReadOutcomeAsync(ObjectReference reference, CancellationToken token)
    {
        if (reference.SizeBytes > 2 * 1024 * 1024) throw new JobException("RESULT_INVALID", 409, "Result exceeds the supported size.");
        using var stream = new MemoryStream();
        await CopyVerifiedAsync(reference, stream, token).ConfigureAwait(false);
        stream.Position = 0;
        return await JsonSerializer.DeserializeAsync<WorkerRunOutcome>(stream, WorkerRunOutcome.JsonOptions, token).ConfigureAwait(false)
            ?? throw new JobException("RESULT_MISSING", 409, "Result is missing.");
    }

    public async Task<AttemptOutcome?> RecoverCandidateAsync(JobRecord job, CancellationToken token)
    {
        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest
            { BucketName = bucket, Key = JobService.AttemptPrefix(job) + "candidate.json" }, token).ConfigureAwait(false);
            if (response.Headers.ContentLength is <= 0 or > 131072 || string.IsNullOrEmpty(response.VersionId) || response.VersionId == "null")
                throw new JobException("RESULT_INVALID", 409, "Invalid candidate manifest.");
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[8192]; int count;
            while ((count = await response.ResponseStream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + count > 131072) throw new JobException("RESULT_INVALID", 409, "Candidate manifest exceeded limit.");
                bytes.Write(buffer, 0, count);
            }
            return JsonSerializer.Deserialize<AttemptOutcome>(bytes.ToArray(), CloudJson.Options);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }
}
