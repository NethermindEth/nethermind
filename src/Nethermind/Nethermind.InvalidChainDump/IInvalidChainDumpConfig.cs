// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.InvalidChainDump;

/// <summary>
/// Configuration for uploading invalid-block diagnostic bundles.
/// </summary>
[ConfigCategory(Description = "Configuration of invalid-block diagnostic bundle uploads.")]
public interface IInvalidChainDumpConfig : IConfig
{
    [ConfigItem(
        Description = "The S3-compatible service URL used for direct uploads, for example `http://127.0.0.1:9000` for MinIO.",
        DefaultValue = "")]
    string ServiceUrl { get; set; }

    [ConfigItem(
        Description = "The bucket name used for direct S3-compatible invalid-block diagnostic uploads.",
        DefaultValue = "")]
    string BucketName { get; set; }

    [ConfigItem(
        Description = "The access key used for direct S3-compatible invalid-block diagnostic uploads.",
        DefaultValue = "")]
    string AccessKey { get; set; }

    [ConfigItem(
        Description = "The secret key used for direct S3-compatible invalid-block diagnostic uploads.",
        DefaultValue = "")]
    string SecretKey { get; set; }

    [ConfigItem(
        Description = "The AWS region used for direct S3-compatible invalid-block diagnostic uploads.",
        DefaultValue = "us-east-1")]
    string Region { get; set; }

    [ConfigItem(
        Description = "An optional object-key prefix used for direct S3-compatible invalid-block diagnostic uploads.",
        DefaultValue = "dumps")]
    string ObjectKeyPrefix { get; set; }

    [ConfigItem(
        Description = "The timeout, in milliseconds, for uploading an invalid-block diagnostic archive.",
        DefaultValue = "300000")]
    int UploadTimeoutMilliseconds { get; set; }
}
