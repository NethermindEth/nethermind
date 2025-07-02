// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

public class NoCategoryConfig : INoCategoryConfig
{
    public string Config { get; set; } = null;
    public string MonitoringJob { get; set; }
    public string MonitoringGroup { get; set; }
    public string CliSwitchLocal { get; set; }
}
