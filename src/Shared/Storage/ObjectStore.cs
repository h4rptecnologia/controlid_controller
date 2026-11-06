using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Platform.Shared.Storage;

public interface IObjectStore
{
    Task PutAsync(string key, Stream content, CancellationToken cancellationToken);

    Task GetAsync(string key, Stream destination, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public sealed class S3ObjectStore(IAmazonS3 client, string bucket) : IObjectStore
{
    public async Task PutAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        await client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = content,
                AutoCloseStream = false,
                ContentType = "application/octet-stream",
                CannedACL = S3CannedACL.Private,
            },
            cancellationToken);
    }

    public async Task GetAsync(string key, Stream destination, CancellationToken cancellationToken)
    {
        using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, cancellationToken);
        await response.ResponseStream.CopyToAsync(destination, cancellationToken);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, cancellationToken);

    public async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Ja existe.
        }
    }

    public static IAmazonS3 CreateClient(string serviceUrl, string accessKey, string secretKey) =>
        new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config
            {
                ServiceURL = serviceUrl,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
}
