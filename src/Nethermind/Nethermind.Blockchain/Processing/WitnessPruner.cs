// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Synchronization.Witness
{
    public static class WitnessCollectorExtensions
    {
        public static IWitnessRepository WithPruning(
            this IWitnessRepository repository,
            IBlockTree blockTree,
            ILogManager logManager,
            int followDistance = 16)
        {
            new WitnessPruner(blockTree, repository, logManager, followDistance).Start();
            return repository;
        }
    }

    public class WitnessPruner
    {
        private readonly IBlockTree _blockTree;
        private readonly IWitnessRepository _witnessRepository;
        private readonly int _followDistance;
        private readonly ILogger _logger;

        public WitnessPruner(IBlockTree blockTree, IWitnessRepository witnessRepository, ILogManager logManager, int followDistance = 16)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _witnessRepository = witnessRepository ?? throw new ArgumentNullException(nameof(witnessRepository));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _followDistance = followDistance;
        }

        public void Start()
        {
            _blockTree.NewHeadBlock += OnNewHeadBlock;
        }

        private void OnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            long toPrune = e.Block.Number - _followDistance;
            if (toPrune > 0)
            {
                var level = _blockTree.FindLevel(toPrune);
                if (level is not null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Pruning witness from blocks with number {toPrune}");

                    for (int i = 0; i < level.BlockInfos.Length; i++)
                    {
                        var blockInfo = level.BlockInfos[i];
                        if (blockInfo.BlockHash is not null)
                        {
                            _witnessRepository.Delete(blockInfo.BlockHash);
                        }
                    }
                }
            }
        }
    }
}
