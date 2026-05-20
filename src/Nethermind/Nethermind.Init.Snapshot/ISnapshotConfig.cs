// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Init.Snapshot;

public interface ISnapshotConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the Snapshot plugin.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The URL of the snapshot file.", DefaultValue = "null")]
    string? DownloadUrl { get; set; }

    [ConfigItem(Description = "The SHA-256 checksum of the snapshot file.", DefaultValue = "null")]
    string? Checksum { get; set; }

    [ConfigItem(Description = "The path to the directory to store the snapshot file.", DefaultValue = "snapshot")]
    string SnapshotDirectory { get; set; }

    [ConfigItem(Description = "The name of the snapshot file.", DefaultValue = "snapshot.zip")]
    string SnapshotFileName { get; set; }

    [ConfigItem(Description = "Number of leading path components to strip when extracting a tar archive (passed as --strip-components to tar). Must be non-negative. Set this to match the depth of the snapshot path embedded in the archive.", DefaultValue = "1")]
    int StripComponents { get; set; }
}
