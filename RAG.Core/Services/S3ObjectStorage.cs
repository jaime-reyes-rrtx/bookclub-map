using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using RAG.Core.Configuration;

namespace RAG.Core.Services;

public sealed class S3ObjectStorage : IObjectStorage
{
    private readonly StorageOptions _options;
    private readonly AmazonS3Client _client;

    public S3ObjectStorage(IOptions<RagOptions> options)
    {
        _options = options.Value.Storage;
        var config = new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = _options.Region
        };

        _client = new AmazonS3Client(
            new BasicAWSCredentials(_options.AccessKey, _options.SecretKey),
            config);
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        var buckets = await _client.ListBucketsAsync(cancellationToken);
        if (buckets.Buckets?.Any(bucket => bucket.BucketName == _options.Bucket) == true)
        {
            return;
        }

        await _client.PutBucketAsync(new PutBucketRequest
        {
            BucketName = _options.Bucket
        }, cancellationToken);
    }

    public async Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken);
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            InputStream = content,
            ContentType = contentType
        }, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        using var response = await _client.GetObjectAsync(_options.Bucket, objectKey, cancellationToken);
        var memory = new MemoryStream();
        await response.ResponseStream.CopyToAsync(memory, cancellationToken);
        memory.Position = 0;
        return memory;
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        await _client.DeleteObjectAsync(_options.Bucket, objectKey, cancellationToken);
    }
}
