// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Config;

public class NoCategoryConfig : INoCategoryConfig
{
    public string? Config { get; set; } = null;
    [Obsolete("Has no effect. Set Metrics.MonitoringJob.")]
    public string? MonitoringJob { get; set; }
    [Obsolete("Has no effect. Set Metrics.MonitoringGroup.")]
    public string? MonitoringGroup { get; set; }
    [Obsolete("Has no effect.")]
    public string? CliSwitchLocal { get; set; }
}
