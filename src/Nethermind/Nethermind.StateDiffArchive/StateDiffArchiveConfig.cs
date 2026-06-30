// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateDiffArchive;

public class StateDiffArchiveConfig : IStateDiffArchiveConfig
{
    public bool RecordingEnabled { get; set; }
    public bool ReplayEnabled { get; set; }
    public string ArchivePath { get; set; } = "stateDiffArchive";
    public string? MergeSources { get; set; }
}
