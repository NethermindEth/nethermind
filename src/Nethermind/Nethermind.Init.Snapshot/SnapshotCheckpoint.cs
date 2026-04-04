// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Init.Snapshot;

/// <summary>
/// Persists the current stage of a snapshot initialization to disk so that progress
/// survives process restarts.
/// </summary>
internal sealed class SnapshotCheckpoint(ISnapshotConfig config)
{
    private readonly string _path = Path.Combine(config.SnapshotDirectory, $"checkpoint_{config.SnapshotFileName}");

    public SnapshotStage Read()
    {
        if (!File.Exists(_path))
            return SnapshotStage.Started;

        string content = File.ReadAllText(_path).Trim();
        return Enum.TryParse(content, out SnapshotStage stage)
            ? stage
            : SnapshotStage.Started;
    }

    public void Advance(SnapshotStage stage) => File.WriteAllText(_path, stage.ToString());
}

internal enum SnapshotStage
{
    Started,
    Downloaded,
    Verified,
    Extracted,
    Completed
}
