// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Services;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Clique
{
    internal class CliqueHealthHintService : IHealthHintService
    {
        private readonly ISnapshotManager _snapshotManager;
        private readonly ChainSpec _chainSpec;

        public CliqueHealthHintService(ISnapshotManager snapshotManager, ChainSpec chainSpec)
        {
            _snapshotManager = snapshotManager;
            _chainSpec = chainSpec;
        }
        public ulong? MaxSecondsIntervalForProcessingBlocksHint()
        {
            return _chainSpec.Clique.Period * HealthHintConstants.ProcessingSafetyMultiplier;
        }

        public ulong? MaxSecondsIntervalForProducingBlocksHint()
        {
            return Math.Max(_snapshotManager.GetLastSignersCount(), 1) * _chainSpec.Clique.Period *
                HealthHintConstants.ProducingSafetyMultiplier;
        }
    }
}
