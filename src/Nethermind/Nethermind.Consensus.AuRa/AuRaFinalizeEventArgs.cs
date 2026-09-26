// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaFinalizeEventArgs(BlockHeader finalizingBlock, IReadOnlyList<BlockHeader> finalizedBlocks) : EventArgs
    {
        public AuRaFinalizeEventArgs(BlockHeader finalizingBlock, params BlockHeader[] finalizedBlocks)
            : this(finalizingBlock, (IReadOnlyList<BlockHeader>)finalizedBlocks) { }

        public BlockHeader FinalizingBlock { get; } = finalizingBlock;
        public IReadOnlyList<BlockHeader> FinalizedBlocks { get; } = finalizedBlocks;
    }
}
