// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.Rewards
{
    public class BlockReward(Address address, in UInt256 value, BlockRewardType rewardType = BlockRewardType.Block)
    {
        public Address Address { get; } = address;

        public UInt256 Value { get; } = value;

        public BlockRewardType RewardType { get; } = rewardType;
    }
}
