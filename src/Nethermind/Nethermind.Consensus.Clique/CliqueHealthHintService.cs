// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Services;

namespace Nethermind.Consensus.Clique
{
    internal class CliqueHealthHintService : IHealthHintService
    {
        private readonly ISnapshotManager _snapshotManager;
        private readonly CliqueChainSpecEngineParameters _chainSpec;

        public CliqueHealthHintService(ISnapshotManager snapshotManager, CliqueChainSpecEngineParameters chainSpec)
        {
            _snapshotManager = snapshotManager;
            _chainSpec = chainSpec;
        }
        public ulong? MaxSecondsIntervalForProcessingBlocksHint()
        {
            return _chainSpec.Period * HealthHintConstants.ProcessingSafetyMultiplier;
        }

        public ulong? MaxSecondsIntervalForProducingBlocksHint()
        {
            return Math.Max(_snapshotManager.GetLastSignersCount(), 1) * _chainSpec.Period *
                HealthHintConstants.ProducingSafetyMultiplier;
        }
    }
}
