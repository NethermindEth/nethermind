// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core.ServiceStopper;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Api;

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
    IBlockProcessor BlockProcessor { get; }
    IBlockchainProcessor BlockchainProcessor { get; }
    IWorldState WorldState { get; }

    Task IStoppableService.StopAsync() => BlockchainProcessor.StopAsync();
    string IStoppableService.Description => "blockchain processor";
}
