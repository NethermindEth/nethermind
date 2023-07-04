// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Rewards
{
    public enum BlockRewardType
    {
        Block = 0,
        Uncle = 1,
        EmptyStep = 2,
        External = 3
    }

    public static class BlockRewardTypeExtension
    {
        public static string ToLowerString(this BlockRewardType blockRewardType)
        {
            switch (blockRewardType)
            {
                case BlockRewardType.Block:
                    return "block";
                case BlockRewardType.Uncle:
                    return "uncle";
                case BlockRewardType.External:
                    return "external";
                case BlockRewardType.EmptyStep:
                    return "emptystep";
                default:
                    throw new ArgumentOutOfRangeException(nameof(blockRewardType), blockRewardType, null);
            }
        }
    }
}
