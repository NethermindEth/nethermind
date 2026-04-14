// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Services;

namespace Nethermind.Consensus.Clique
{
    internal class CliqueHealthHintService(ISnapshotManager snapshotManager, CliqueChainSpecEngineParameters chainSpec) : IHealthHintService
    {
        private readonly ISnapshotManager _snapshotManager = snapshotManager;
        private readonly CliqueChainSpecEngineParameters _chainSpec = chainSpec;

        public ulong? MaxSecondsIntervalForProcessingBlocksHint() => _chainSpec.Period * HealthHintConstants.ProcessingSafetyMultiplier;

        public ulong? MaxSecondsIntervalForProducingBlocksHint() => Math.Max(_snapshotManager.GetLastSignersCount(), 1) * _chainSpec.Period *
                HealthHintConstants.ProducingSafetyMultiplier;
    }
}
