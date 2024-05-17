// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin
{
    public class AuRaMergeFinalizationManager : MergeFinalizationManager, IManualBlockFinalizationManager, IAuRaBlockFinalizationManager
    {
        private readonly IAuRaBlockFinalizationManager? _auRaBlockFinalizationManager;
        
        public AuRaMergeFinalizationManager(IManualBlockFinalizationManager manualBlockFinalizationManager, IBlockFinalizationManager? blockFinalizationManager, IPoSSwitcher poSSwitcher) : base(manualBlockFinalizationManager, blockFinalizationManager, poSSwitcher)
        {
            _auRaBlockFinalizationManager = blockFinalizationManager as IAuRaBlockFinalizationManager;
            _auRaBlockFinalizationManager!.BlocksFinalized += OnBlockFinalized;
        }

        public long GetLastLevelFinalizedBy(Hash256 blockHash)
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

        public void SetMainBlockProcessor(IBlockProcessor blockProcessor)
        {
            _auRaBlockFinalizationManager!.SetMainBlockProcessor(blockProcessor);
        }

        public override long LastFinalizedBlockLevel
        {
            get
            {
                if (IsPostMerge)
                {
                    return _manualBlockFinalizationManager.LastFinalizedBlockLevel;
                }
                return _auRaBlockFinalizationManager!.LastFinalizedBlockLevel;
            }
        }

        public override void Dispose()
        {
            if (IsPostMerge)
            {
                _auRaBlockFinalizationManager!.Dispose();
            }
            base.Dispose();
        }

    }
}
