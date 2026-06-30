// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.StateDiffArchive;

/// <summary>
/// Configuration for the state-diff archive plugin: record committed per-block state diffs, replay them
/// to fast-forward state without the EVM, or merge several recordings into one archive directory.
/// </summary>
[ConfigCategory(Description = "Records committed per-block state diffs and replays them without the EVM. Development/operations use only — must not run on a validating node.")]
public interface IStateDiffArchiveConfig : IConfig
{
    [ConfigItem(Description = "Whether to record the committed per-block state diff to the archive directory.", DefaultValue = "false")]
    bool RecordingEnabled { get; set; }

    [ConfigItem(Description = "Whether to replay recorded state diffs during block processing instead of executing transactions.", DefaultValue = "false")]
    bool ReplayEnabled { get; set; }

    [ConfigItem(Description = "Directory (relative to BaseDbPath) holding the state-diff era files.", DefaultValue = "stateDiffArchive")]
    string ArchivePath { get; set; }

    [ConfigItem(Description = "Semicolon-separated source archive directories to merge into ArchivePath at startup. When set, the node merges and then exits without processing.", DefaultValue = null)]
    string? MergeSources { get; set; }
}
