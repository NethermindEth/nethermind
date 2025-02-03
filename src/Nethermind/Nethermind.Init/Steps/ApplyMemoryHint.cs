// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db.Rocks.Config;
using Nethermind.Network.Config;
using Nethermind.TxPool;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(MigrateConfigs))]
    public sealed class ApplyMemoryHint : IStep
    {
        private readonly INethermindApi _api;
        private readonly IInitConfig _initConfig;
        private readonly IDbConfig _dbConfig;
        private readonly INetworkConfig _networkConfig;
        private readonly ISyncConfig _syncConfig;
        private readonly ITxPoolConfig _txPoolConfig;

        public ApplyMemoryHint(INethermindApi api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _initConfig = api.Config<IInitConfig>();
            _dbConfig = api.Config<IDbConfig>();
            _networkConfig = api.Config<INetworkConfig>();
            _syncConfig = api.Config<ISyncConfig>();
            _txPoolConfig = api.Config<ITxPoolConfig>();
        }

        public Task Execute(CancellationToken _)
        {
            MemoryHintMan memoryHintMan = new(_api.LogManager);
            uint cpuCount = (uint)Environment.ProcessorCount;
            if (_initConfig.MemoryHint.HasValue)
            {
                memoryHintMan.SetMemoryAllowances(
                    _dbConfig,
                    _initConfig,
                    _networkConfig,
                    _syncConfig,
                    _txPoolConfig,
                    cpuCount);
            }

            return Task.CompletedTask;
        }
    }
}
