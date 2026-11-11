// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Nethermind.Config;
using Nethermind.Core.Exceptions;

namespace Nethermind.InvalidChainDump;

internal sealed class S3DumpArchiveStore : IDumpArchiveStore, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucketName;
    private readonly string _keyPrefix;

    public S3DumpArchiveStore(IInvalidChainDumpConfig config)
    {
        if (!Uri.TryCreate(config.ServiceUrl, UriKind.Absolute, out Uri? serviceUri) || (serviceUri.Scheme != Uri.UriSchemeHttp && serviceUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidConfigurationException($"{nameof(IInvalidChainDumpConfig)}.{nameof(config.ServiceUrl)} must be an absolute HTTP or HTTPS URL.", ExitCodes.ForbiddenOptionValue);
        }

        if (string.IsNullOrWhiteSpace(config.BucketName) || string.IsNullOrWhiteSpace(config.AccessKey) || string.IsNullOrWhiteSpace(config.SecretKey))
        {
            throw new InvalidConfigurationException(
                $"{nameof(IInvalidChainDumpConfig)}.{nameof(config.BucketName)}, {nameof(config.AccessKey)} and {nameof(config.SecretKey)} must be set when {nameof(config.ServiceUrl)} is.",
                ExitCodes.ForbiddenOptionValue);
        }

        _client = new AmazonS3Client(
            new BasicAWSCredentials(config.AccessKey, config.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = serviceUri.ToString(),
                AuthenticationRegion = config.Region,
                ForcePathStyle = true,
                // S3-compatible stores do not all accept the flexible checksums or the streaming (aws-chunked) signing the SDK uses by default.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
        _bucketName = config.BucketName;
        string prefix = config.ObjectKeyPrefix.Trim('/');
        _keyPrefix = prefix.Length == 0 ? string.Empty : $"{prefix}/";
    }

    public async Task<string> UploadAsync(string archivePath, CancellationToken cancellationToken)
    {
        string key = _keyPrefix + Path.GetFileName(archivePath);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            FilePath = archivePath,
            ContentType = "application/zip",
            UseChunkEncoding = false,
        }, cancellationToken);
        return key;
    }

    public void Dispose() => _client.Dispose();
}
