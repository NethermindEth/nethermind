// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.BalRecorder;

public interface IBalRecorderConfig : IConfig
{
    [ConfigItem(Description = "Whether to replay recorded block access lists during block processing.", DefaultValue = "false")]
    bool ReplayEnabled { get; set; }

    [ConfigItem(Description = "Whether to record block access lists to disk after block processing.", DefaultValue = "false")]
    bool RecordingEnabled { get; set; }

    [ConfigItem(Description = "Directory (relative to BaseDbPath) used to store recorded block access list era files.", DefaultValue = "recordedBal")]
    string Path { get; set; }
}
