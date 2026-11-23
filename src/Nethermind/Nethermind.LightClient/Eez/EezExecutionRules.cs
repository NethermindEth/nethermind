// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Rpc;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Rlp;

namespace Nethermind.LightClient.Eez;

internal sealed class EezExecutionRules(EezChainRules rules, bool decorateSpecs) : Module
{
    private static readonly Lock _decoderRegistration = new();
    private static EezChainRules? _registeredRules;

    internal EezChainRules Rules => rules;

    internal EezExecutionRules ForCalls() => new(rules, decorateSpecs: false);

    internal static EezExecutionRules FromGenesis(string genesisFile, ulong settlementChainId)
    {
        EezChainRules rules = EezChainRules.FromGenesisFile(genesisFile);
        if (rules.Schedule?.L1Reference.ChainId != settlementChainId)
            throw new ArgumentException("The EEZ genesis must be a RollupX genesis for the selected settlement network.");
        RegisterTransactionTypes(rules);
        return new(rules, decorateSpecs: true);
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        builder.AddSingleton(rules);
        if (decorateSpecs)
            builder.AddDecorator<ISpecProvider, EezSpecProvider>();
        builder.AddScoped<ITransactionProcessor, EezTransactionProcessor>();
    }

    private static void RegisterTransactionTypes(EezChainRules rules)
    {
        lock (_decoderRegistration)
        {
            if (_registeredRules is not null)
            {
                if (_registeredRules.SystemTransactionType != rules.SystemTransactionType || _registeredRules.NativeFormat != rules.NativeFormat)
                    throw new InvalidOperationException("Only one EEZ transaction format can be registered per process.");
                return;
            }
            TxDecoder.Instance.RegisterDecoder(EezTxType.CreateDecoder(rules));
            if (rules.UsesNativeContract)
                TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder(rules.NativeFormat));
            RegisterRpcTransactionTypes(rules);
            _registeredRules = rules;
        }
    }

    internal static void RegisterRpcTransactionTypes(EezChainRules rules)
    {
        if (rules.UsesNativeContract)
        {
            TransactionForRpc.RegisterTransactionType<EezNativeContractTransactionForRpc>();
            TransactionForRpc.RegisterTransactionType<EezNativeHistoryTransactionForRpc>();
        }
        else if (rules.UsesNativeSystemTransactions)
            TransactionForRpc.RegisterTransactionType<EezNativeSystemTransactionForRpc>();
        else
            TransactionForRpc.RegisterTransactionType<EezSystemTransactionForRpc>();
    }
}
