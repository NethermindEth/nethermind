// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.Rewards
{
    public class BlockReward
    {
        public BlockReward(Address address, in UInt256 value, BlockRewardType rewardType = BlockRewardType.Block)
        {
            Address = address;
            Value = value;
            RewardType = rewardType;
        }

        public Address Address { get; }

        public UInt256 Value { get; }

        public BlockRewardType RewardType { get; }
    }
}
