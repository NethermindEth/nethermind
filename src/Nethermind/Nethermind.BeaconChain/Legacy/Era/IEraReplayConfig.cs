// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.BeaconChain.Legacy.Era;

public interface IEraReplayConfig : IConfig
{
    [ConfigItem(Description = "The directory of consensus-layer .era files that the `replay-beacon-era` command replays from genesis. It must hold every era from 0 without a gap.", DefaultValue = "null")]
    string? Directory { get; set; }

    [ConfigItem(Description = "The last era to replay, so that a run can stop at a fork and be continued later. When unset, replays to the newest file.", DefaultValue = "null")]
    ulong? EndEra { get; set; }

    [ConfigItem(Description = "The number of eras to replay at once. Each takes up to about 1.5 GB of memory for recent eras. Zero uses half of the processors.", DefaultValue = "0")]
    int Lanes { get; set; }
}

public class EraReplayConfig : IEraReplayConfig
{
    public string? Directory { get; set; }
    public ulong? EndEra { get; set; }
    public int Lanes { get; set; }
}
