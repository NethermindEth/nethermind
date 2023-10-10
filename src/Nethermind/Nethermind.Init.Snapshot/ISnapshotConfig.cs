// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Init.Snapshot;

public interface ISnapshotConfig : IConfig
{
    [ConfigItem(Description = "Defines whether the Snapshot plugin is enabled.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "URL to snapshot file. Ignored if not set.", DefaultValue = "null")]
    public string? DownloadUrl { get; set; }

    [ConfigItem(Description = "SHA256 checksum for the snapshot file", DefaultValue = "null")]
    public string? Checksum { get; set; }
}
