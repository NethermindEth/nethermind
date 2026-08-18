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
            // size exists to avoid) and an out-of-range value would surface later as an opaque
            // TypeInitializationException at the first EVM execution, so reject both here.
            // The ceiling keeps the cache's eagerly-allocated entry table small (tens of MB) so
            // this validation, not a later type-initializer failure, is what an operator sees.
            const int maxInstructionStreamCacheSize = 1 << 20;
            if (initConfig.InstructionStreamCacheSize is <= 0 or > maxInstructionStreamCacheSize)
            {
                throw new InvalidDataException(
                    $"Init.{nameof(IInitConfig.InstructionStreamCacheSize)} must be between 1 and {maxInstructionStreamCacheSize}, got {initConfig.InstructionStreamCacheSize}.");
            }

            // Before any EVM execution: the cache captures this value when its static state initializes.
            Evm.MemoryAllowance.InstructionStreamCacheSize = initConfig.InstructionStreamCacheSize;
            ILogger logger = logManager.GetClassLogger<ApplyMemoryHint>();
            if (logger.IsInfo) logger.Info($"Instruction stream cache size: {initConfig.InstructionStreamCacheSize} entries");

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
