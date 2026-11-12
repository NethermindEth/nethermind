// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core.ServiceStopper;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// These classes share the same IWorldState as the main block validation pipeline.
/// This is very important to distinguish as there are multiple instance of these classes for different
/// rpc modules or block building or block validations or prewarming threads
/// Using these concurrently with block processing may result in random invalid block.
/// Therefore only use these if you are sure that you definitely need the main block processing one.
/// </summary>
public interface IMainProcessingContext : IStoppableService
{
    ITransactionProcessor TransactionProcessor { get; }
    IBranchProcessor BranchProcessor { get; }
    IBlockProcessor BlockProcessor { get; }
    IBlockchainProcessor BlockchainProcessor { get; }
    IBlockProcessingQueue BlockProcessingQueue { get; }
    IWorldState WorldState { get; }
    IGenesisLoader GenesisLoader { get; }

    /// <summary>The scope the pipeline's components live in, which a graph that shares its registrations is built from.</summary>
    ILifetimeScope LifetimeScope { get; }

    /// <summary>
    /// Fired after a transaction has been processed (even if inside the block).
    /// </summary>
    event EventHandler<TxProcessedEventArgs> TransactionProcessed;

    Task IStoppableService.StopAsync() => BlockProcessingQueue.StopAsync();
    string IStoppableService.Description => "blockchain processor";
}
