// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Clique
{
    internal static class BlockHeaderExtensions
    {
        public static bool IsInTurn(this BlockHeader header)
        {
            return header.Difficulty == Clique.DifficultyInTurn;
        }
    }

    internal static class BlockExtensions
    {
        public static bool IsInTurn(this Block block)
        {
            return block.Difficulty == Clique.DifficultyInTurn;
        }
    }
}
