using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Trackdub.Cloud.Aws;
using Trackdub.Cloud.Jobs;

namespace Trackdub.Cloud.Aws.Tests;

public sealed class S3SigningTests
{
    [Fact]
    public async Task PresignedPutBindsExactLengthAndChecksumWithoutAwsCalls()
    {
        using var client = new AmazonS3Client(new BasicAWSCredentials("local", "local"), RegionEndpoint.USWest2);
        var upload = new UploadRecord(Guid.NewGuid(), "owner", null, "uploads/input.mp4", 123,
            new string('a', 64), "video/mp4", DateTimeOffset.UtcNow.AddMinutes(5));
        string url = Uri.UnescapeDataString(await new S3ArtifactStore(client, "private-test-bucket", TimeProvider.System)
            .SignUploadAsync(upload, TestContext.Current.CancellationToken));
        string signedHeaders = url.Split('&').Single(part => part.StartsWith("X-Amz-SignedHeaders=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("content-length", signedHeaders);
        Assert.Contains("x-amz-checksum-sha256", signedHeaders);
    }
}
