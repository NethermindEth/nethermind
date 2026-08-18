// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
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
            // Zero would silently disable the cache (every lookup misses — the exact pathology the
            // size exists to avoid) and a negative value would surface later as an opaque
            // TypeInitializationException at the first EVM execution, so reject both here.
            if (initConfig.InstructionStreamCacheSize <= 0)
            {
                throw new InvalidDataException(
                    $"{nameof(IInitConfig)}.{nameof(IInitConfig.InstructionStreamCacheSize)} must be positive, got {initConfig.InstructionStreamCacheSize}.");
            }

            // Before any EVM execution: the cache captures this value when its static state initializes.
            Evm.MemoryAllowance.InstructionStreamCacheSize = initConfig.InstructionStreamCacheSize;
            ILogger logger = logManager.GetClassLogger<ApplyMemoryHint>();
            if (logger.IsDebug) logger.Debug($"Instruction stream cache size: {initConfig.InstructionStreamCacheSize} entries");

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
