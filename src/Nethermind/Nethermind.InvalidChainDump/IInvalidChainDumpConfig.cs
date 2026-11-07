// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.InvalidChainDump;

[ConfigCategory(Description = "Configuration of uploads of invalid-block diagnostics to S3-compatible storage.")]
public interface IInvalidChainDumpConfig : IConfig
{
    [ConfigItem(
        Description = "The URL of the S3-compatible service to upload invalid-block diagnostics to, for example `http://127.0.0.1:9000` for MinIO. When set, uploads are enabled and `Init.AutoDump` is extended to `All`.",
        DefaultValue = "")]
    string ServiceUrl { get; set; }

    [ConfigItem(Description = "The bucket to upload invalid-block diagnostics to.", DefaultValue = "invalid-blocks")]
    string BucketName { get; set; }

    [ConfigItem(Description = "The access key for uploads of invalid-block diagnostics.", DefaultValue = "", IsSensitive = true)]
    string AccessKey { get; set; }

    [ConfigItem(Description = "The secret key for uploads of invalid-block diagnostics.", DefaultValue = "", IsSensitive = true)]
    string SecretKey { get; set; }

    [ConfigItem(Description = "The region used to sign uploads of invalid-block diagnostics.", DefaultValue = "us-east-1")]
    string Region { get; set; }

    [ConfigItem(Description = "The object key prefix of uploaded invalid-block diagnostics.", DefaultValue = "dumps")]
    string ObjectKeyPrefix { get; set; }

    [ConfigItem(Description = "The timeout, in milliseconds, of an upload of invalid-block diagnostics.", DefaultValue = "300000")]
    int UploadTimeoutMs { get; set; }
}
