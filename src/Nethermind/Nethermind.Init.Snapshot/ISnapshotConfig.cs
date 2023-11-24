// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Init.Snapshot;

public interface ISnapshotConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the Snapshot plugin.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The URL of the snapshot file.", DefaultValue = "null")]
    public string? DownloadUrl { get; set; }

    [ConfigItem(Description = "The SHA-256 checksum of the snapshot file.", DefaultValue = "null")]
    public string? Checksum { get; set; }

    [ConfigItem(Description = "Directory where snapshot will be stored.", DefaultValue = "snapshot")]
    public string SnapshotDirectory { get; set; }

    [ConfigItem(Description = "Snapshot file name.", DefaultValue = "snapshot.zip")]
    public string SnapshotFileName { get; set; }
}
