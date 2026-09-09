// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Consensus.Rewards;
using Nethermind.Int256;

namespace Nethermind.Xdc;

public sealed class XdcRewardLog
{
    [JsonPropertyName("sign")]
    public ulong Sign { get; set; }

    [JsonPropertyName("reward")]
    public string Reward { get; set; } = UInt256.Zero.ToString();
}

public sealed class XdcEpochRewards
{
    [JsonPropertyName("signers")]
    public Dictionary<string, XdcRewardLog> Signers { get; set; } = [];

    [JsonPropertyName("rewards")]
    public Dictionary<string, Dictionary<string, string>> Rewards { get; set; } = [];

    [JsonPropertyName("signersProtector")]
    public Dictionary<string, XdcRewardLog> SignersProtector { get; set; } = [];

    [JsonPropertyName("rewardsProtector")]
    public Dictionary<string, Dictionary<string, string>> RewardsProtector { get; set; } = [];

    [JsonPropertyName("signersObserver")]
    public Dictionary<string, XdcRewardLog> SignersObserver { get; set; } = [];

    [JsonPropertyName("rewardsObserver")]
    public Dictionary<string, Dictionary<string, string>> RewardsObserver { get; set; } = [];

    public static XdcEpochRewards Empty => new();
}

internal sealed record XdcProcessedRewards(BlockReward[] BlockRewards, XdcEpochRewards EpochRewards)
{
    public static XdcProcessedRewards Empty => new([], XdcEpochRewards.Empty);
}
