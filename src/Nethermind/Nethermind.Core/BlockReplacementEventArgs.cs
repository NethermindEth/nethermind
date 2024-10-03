// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public class BlockReplacementEventArgs(Block block, Block? previousBlock = null, bool isSyncing = false) : BlockEventArgs(block)
    {
        public Block? PreviousBlock { get; } = previousBlock;
        public bool IsSyncing { get; } = isSyncing;
    }
}
