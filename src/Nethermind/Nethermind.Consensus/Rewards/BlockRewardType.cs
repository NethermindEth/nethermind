// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
            return blockRewardType switch
            {
                BlockRewardType.Block => "block",
                BlockRewardType.Uncle => "uncle",
                BlockRewardType.External => "external",
                BlockRewardType.EmptyStep => "emptystep",
                _ => throw new ArgumentOutOfRangeException(nameof(blockRewardType), blockRewardType, null),
            };
        }
    }
}
