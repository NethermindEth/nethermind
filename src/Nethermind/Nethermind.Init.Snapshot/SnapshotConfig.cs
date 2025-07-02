// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Init.Snapshot;

public class SnapshotConfig : ISnapshotConfig
{
    public bool Enabled { get; set; }

    public string? DownloadUrl { get; set; }

    public string? Checksum { get; set; }

    public string SnapshotDirectory { get; set; } = "snapshot";

    public string SnapshotFileName { get; set; } = "snapshot.zip";
}
