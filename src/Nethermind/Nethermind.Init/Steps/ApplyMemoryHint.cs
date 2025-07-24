// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.TxPool;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(MigrateConfigs))]
    public sealed class ApplyMemoryHint(
        IInitConfig initConfig,
        IDbConfig dbConfig,
        INetworkConfig networkConfig,
        ISyncConfig syncConfig,
        ITxPoolConfig txPoolConfig,
        ILogManager logManager)
        : IStep
    {
        public Task Execute(CancellationToken _)
        {
            MemoryHintMan memoryHintMan = new(logManager);
            uint cpuCount = (uint)Environment.ProcessorCount;
            if (initConfig.MemoryHint.HasValue)
            {
                memoryHintMan.SetMemoryAllowances(
                    dbConfig,
                    initConfig,
                    networkConfig,
                    syncConfig,
                    txPoolConfig,
                    cpuCount);
            }

            return Task.CompletedTask;
        }
    }
}
