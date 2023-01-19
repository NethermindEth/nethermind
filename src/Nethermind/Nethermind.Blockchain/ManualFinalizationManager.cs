// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public class ManualBlockFinalizationManager : IManualBlockFinalizationManager
    {
        // We could save in DB, but its not really needed yet
        public long LastFinalizedBlockLevel { get; private set; } = 0;

        // We could save in DB, but its not really needed yet
        public Keccak LastFinalizedHash { get; private set; } = Keccak.Zero;

        public event EventHandler<FinalizeEventArgs>? BlocksFinalized;

        public void MarkFinalized(BlockHeader finalizingBlock, BlockHeader finalizedBlock)
        {
            LastFinalizedHash = finalizedBlock.Hash!;
            LastFinalizedBlockLevel = Math.Max(LastFinalizedBlockLevel, finalizedBlock.Number);
            BlocksFinalized?.Invoke(this, new FinalizeEventArgs(finalizingBlock, finalizedBlock));
        }

        public void Dispose() { }
    }

    public interface IManualBlockFinalizationManager : IBlockFinalizationManager
    {
        Keccak LastFinalizedHash { get; }
        void MarkFinalized(BlockHeader finalizingBlock, BlockHeader finalizedBlock);
    }
}
