// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// AuRa-flavoured <see cref="EthereumTransactionProcessorBase"/> — overrides system-tx creation so
/// every TransactionProcessor instance built for the AuRa chain (DI singleton, BAL workers,
/// tests) routes system transactions through <see cref="AuRaSystemTransactionProcessor{TGasPolicy}"/>.
/// </summary>
public sealed class AuRaEthereumTransactionProcessor(
    ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
    ISpecProvider? specProvider,
    IWorldState? worldState,
    IVirtualMachine? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager,
    bool parallel = false)
    : EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel)
{
    protected override SystemTransactionProcessor<EthereumGasPolicy> CreateSystemTransactionProcessor() =>
        new AuRaSystemTransactionProcessor<EthereumGasPolicy>(
            _blobBaseFeeCalculator, SpecProvider, WorldState, VirtualMachine, _codeInfoRepository, _logManager);
}

/// <summary>Builds AuRa-aware worker tx processors for the BAL pool.</summary>
public sealed class AuRaTransactionProcessorFactory : ITransactionProcessorFactory
{
    public ITransactionProcessor Create(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider specProvider,
        IWorldState worldState,
        IVirtualMachine virtualMachine,
        ICodeInfoRepository codeInfoRepository,
        ILogManager logManager,
        bool parallel)
        => new AuRaEthereumTransactionProcessor(
            blobBaseFeeCalculator, specProvider, worldState, virtualMachine,
            codeInfoRepository, logManager, parallel);
}
