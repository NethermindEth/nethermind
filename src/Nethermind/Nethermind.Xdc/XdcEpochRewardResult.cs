// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Rewards;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

internal readonly record struct XdcEpochRewardResult(
    BlockReward[] Rewards,
    IXdcReleaseSpec? Spec,
    UInt256 TotalMintedInEpoch,
    UInt256 BurnedInOneEpoch);
