// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>
/// Default <see cref="IBalProcessingEnvFactory"/> producing <see cref="ParallelBalEnv"/> or
/// <see cref="SequentialBalEnv"/> workers.
/// </summary>
public sealed class BalProcessingEnvFactory(
    IBlockhashProvider blockHashProvider,
    ISpecProvider specProvider,
    IWorldState stateProvider,
    ILogManager logManager,
    ITransactionProcessorFactory txProcessorFactory,
    CodeInfoRepositoryFactory codeInfoRepositoryFactory) : IBalProcessingEnvFactory
{
    public IBalProcessingEnv Create(bool parallel)
        => parallel
            ? new ParallelBalEnv(blockHashProvider, specProvider, stateProvider, logManager, txProcessorFactory, codeInfoRepositoryFactory)
            : new SequentialBalEnv(blockHashProvider, specProvider, stateProvider, logManager, txProcessorFactory, codeInfoRepositoryFactory);
}
