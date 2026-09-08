// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Logging;

namespace Nethermind.Init.Snapshot;

/// <summary>
/// Persists the current stage of a snapshot initialization to disk so that progress
/// survives process restarts. A single file per snapshot tracks the last completed
/// <see cref="SnapshotStage"/> as a plain string, which is cheap to write and easy to inspect.
/// </summary>
internal sealed class SnapshotCheckpoint(ISnapshotConfig config, ILogManager logManager)
{
    private const int WriteBufferSize = 4096;

    private readonly string _path = Path.Combine(config.SnapshotDirectory, $"checkpoint_{config.SnapshotFileName}");
    private readonly ILogger _logger = logManager.GetClassLogger<SnapshotCheckpoint>();

    public SnapshotStage Read()
    {
        if (!File.Exists(_path))
            return SnapshotStage.Started;

        string? content = File.ReadLines(_path).FirstOrDefault()?.Trim();
        if (Enum.TryParse(content, out SnapshotStage stage))
            return stage;

        if (_logger.IsWarn)
            _logger.Warn($"Checkpoint file '{_path}' contains unrecognized value '{content}'. Restarting from the beginning.");
        return SnapshotStage.Started;
    }

    /// <summary>
    /// Atomically writes <paramref name="stage"/> to the checkpoint file by writing to a
    /// temp file, flushing to disk, then renaming — so a crash mid-write cannot corrupt
    /// the checkpoint.
    /// </summary>
    public void Advance(SnapshotStage stage)
    {
        string tempPath = $"{_path}.tmp";
        using (FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, WriteBufferSize, FileOptions.WriteThrough))
        {
            byte[] data = Encoding.UTF8.GetBytes(stage.ToString());
            fs.Write(data);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tempPath, _path, overwrite: true);
    }
}

internal enum SnapshotStage
{
    Started,
    Downloaded,
    Verified,
    Extracted,
    Completed
}
