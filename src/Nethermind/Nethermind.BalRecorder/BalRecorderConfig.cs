// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BalRecorder;

public class BalRecorderConfig : IBalRecorderConfig
{
    public bool ReplayEnabled { get; set; } = false;
    public bool RecordingEnabled { get; set; } = false;
    public string Path { get; set; } = "recordedBal";
}
