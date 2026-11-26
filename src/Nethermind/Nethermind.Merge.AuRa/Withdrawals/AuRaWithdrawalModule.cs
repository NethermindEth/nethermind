// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Merge.AuRa.Contracts;

namespace Nethermind.Merge.AuRa.Withdrawals;

/// <summary>Registers AuRa withdrawal processing for block and proof-backed execution scopes.</summary>
public sealed class AuRaWithdrawalModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<IWithdrawalContractFactory, WithdrawalContractFactory>()
        .AddScoped<IWithdrawalContract, IWithdrawalContractFactory, ITransactionProcessor>((factory, txProcessor) => factory.Create(txProcessor))
        .AddScoped<IWithdrawalProcessor, AuraWithdrawalProcessor>()
        .AddSingleton<IWithdrawalProcessorFactory, AuraWithdrawalProcessorFactory>();
}
