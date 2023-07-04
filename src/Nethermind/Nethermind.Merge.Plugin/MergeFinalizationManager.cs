// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin
{
    public class MergeFinalizationManager : IManualBlockFinalizationManager, IAuRaBlockFinalizationManager
    {
        private readonly IManualBlockFinalizationManager _manualBlockFinalizationManager;
        private readonly IAuRaBlockFinalizationManager? _auRaBlockFinalizationManager;
        private bool HasAuRaFinalizationManager => _auRaBlockFinalizationManager is not null;
        private bool IsPostMerge { get; set; }

        public event EventHandler<FinalizeEventArgs>? BlocksFinalized;

        public MergeFinalizationManager(IManualBlockFinalizationManager manualBlockFinalizationManager,
            IBlockFinalizationManager? blockFinalizationManager, IPoSSwitcher poSSwitcher)
        {
            _manualBlockFinalizationManager = manualBlockFinalizationManager;
            _auRaBlockFinalizationManager = blockFinalizationManager as IAuRaBlockFinalizationManager;

            poSSwitcher.TerminalBlockReached += OnSwitchHappened;
            if (poSSwitcher.HasEverReachedTerminalBlock())
            {
                IsPostMerge = true;
            }

            _manualBlockFinalizationManager.BlocksFinalized += OnBlockFinalized;
            if (HasAuRaFinalizationManager)
                _auRaBlockFinalizationManager!.BlocksFinalized += OnBlockFinalized;
        }

        private void OnSwitchHappened(object? sender, EventArgs e)
        {
            IsPostMerge = true;
        }

        private void OnBlockFinalized(object? sender, FinalizeEventArgs e)
        {
            BlocksFinalized?.Invoke(this, e);
        }

        public void MarkFinalized(BlockHeader finalizingBlock, BlockHeader finalizedBlock)
        {
            _manualBlockFinalizationManager.MarkFinalized(finalizingBlock, finalizedBlock);
        }

        public long GetLastLevelFinalizedBy(Keccak blockHash)
        {
            if (_auRaBlockFinalizationManager is not null)
            {
                return _auRaBlockFinalizationManager.GetLastLevelFinalizedBy(blockHash);
            }

            throw new InvalidOperationException(
                $"{nameof(GetLastLevelFinalizedBy)} called when empty {nameof(_auRaBlockFinalizationManager)} is null.");
        }

        public long? GetFinalizationLevel(long level)
        {
            if (_auRaBlockFinalizationManager is not null)
            {
                return _auRaBlockFinalizationManager.GetFinalizationLevel(level);
            }

            throw new InvalidOperationException(
                $"{nameof(GetFinalizationLevel)} called when empty {nameof(_auRaBlockFinalizationManager)} is null.");
        }

        public void Dispose()
        {
            if (IsPostMerge && HasAuRaFinalizationManager)
            {
                _auRaBlockFinalizationManager!.Dispose();
            }
        }

        public Keccak LastFinalizedHash { get => _manualBlockFinalizationManager.LastFinalizedHash; }

        public long LastFinalizedBlockLevel
        {
            get
            {
                if (IsPostMerge)
                {
                    return _manualBlockFinalizationManager.LastFinalizedBlockLevel;
                }

                if (HasAuRaFinalizationManager)
                {
                    return _auRaBlockFinalizationManager!.LastFinalizedBlockLevel;
                }

                return 0;
            }
        }
    }
}
