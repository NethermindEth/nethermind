// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin
{
    public class MergeFinalizationManager : IManualBlockFinalizationManager
    {
        protected readonly IManualBlockFinalizationManager _manualBlockFinalizationManager;
        protected bool IsPostMerge { get; set; }

        public event EventHandler<FinalizeEventArgs>? BlocksFinalized;

        public MergeFinalizationManager(IManualBlockFinalizationManager manualBlockFinalizationManager,
            IBlockFinalizationManager? blockFinalizationManager, IPoSSwitcher poSSwitcher)
        {
            _manualBlockFinalizationManager = manualBlockFinalizationManager;

            poSSwitcher.TerminalBlockReached += OnSwitchHappened;
            if (poSSwitcher.HasEverReachedTerminalBlock())
            {
                IsPostMerge = true;
            }

            _manualBlockFinalizationManager.BlocksFinalized += OnBlockFinalized;
        }

        private void OnSwitchHappened(object? sender, EventArgs e)
        {
            IsPostMerge = true;
        }

        protected void OnBlockFinalized(object? sender, FinalizeEventArgs e)
        {
            BlocksFinalized?.Invoke(this, e);
        }

        public void MarkFinalized(BlockHeader finalizingBlock, BlockHeader finalizedBlock)
        {
            _manualBlockFinalizationManager.MarkFinalized(finalizingBlock, finalizedBlock);
        }

        public Hash256 LastFinalizedHash { get => _manualBlockFinalizationManager.LastFinalizedHash; }

        public virtual long LastFinalizedBlockLevel
        {
            get
            {
                if (IsPostMerge)
                {
                    return _manualBlockFinalizationManager.LastFinalizedBlockLevel;
                }

                return 0;
            }
        }

        public virtual void Dispose() { }
    }
}
