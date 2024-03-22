// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Services;
using Nethermind.Clique.Test;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Clique
{
    internal class CliqueHealthHintService : IHealthHintService
    {
        private readonly ISnapshotManager _snapshotManager;
        private readonly CliqueChainSpecEngineParameters _parameters;

        public CliqueHealthHintService(ISnapshotManager snapshotManager, CliqueChainSpecEngineParameters parameters)
        {
            _snapshotManager = snapshotManager;
            _parameters = parameters;
        }
        public ulong? MaxSecondsIntervalForProcessingBlocksHint()
        {
            return _parameters.Period * HealthHintConstants.ProcessingSafetyMultiplier;
        }

        public ulong? MaxSecondsIntervalForProducingBlocksHint()
        {
            return Math.Max(_snapshotManager.GetLastSignersCount(), 1) * _parameters.Period *
                HealthHintConstants.ProducingSafetyMultiplier;
        }
    }
}
