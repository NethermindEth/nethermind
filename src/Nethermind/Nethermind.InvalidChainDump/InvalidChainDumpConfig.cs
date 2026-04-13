// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.InvalidChainDump;

public class InvalidChainDumpConfig : IInvalidChainDumpConfig
{
    public string ServiceUrl { get; set; } = string.Empty;
    public string BucketName { get; set; } = "invalid-blocks";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string ObjectKeyPrefix { get; set; } = "dumps";

    public int UploadTimeoutMilliseconds { get; set; } = 300000;
}
