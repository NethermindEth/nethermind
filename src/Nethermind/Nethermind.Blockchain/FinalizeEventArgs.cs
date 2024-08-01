// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain
{
    public class FinalizeEventArgs : EventArgs
    {
        public FinalizeEventArgs(BlockHeader finalizingBlock, params BlockHeader[] finalizedBlocks)
            : this(finalizingBlock, (IReadOnlyList<BlockHeader>)finalizedBlocks) { }

        public FinalizeEventArgs(BlockHeader finalizingBlock, IReadOnlyList<BlockHeader> finalizedBlocks)
        {
            FinalizingBlock = finalizingBlock;
            FinalizedBlocks = finalizedBlocks;
        }

        public BlockHeader FinalizingBlock { get; }
        public IReadOnlyList<BlockHeader> FinalizedBlocks { get; }

    }
}
