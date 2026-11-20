// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Arkiv.Execution;
using Nethermind.Arkiv.Rpc;
using Nethermind.Arkiv.State;
using Nethermind.Arkiv.Sync;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Exceptions;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Synchronization.FastSync;

namespace Nethermind.Arkiv;

/// <summary>
/// Runs Nethermind as an execution node of an Arkiv database chain (Rabdi and the other GolemDB networks):
/// a post-merge Ethereum chain whose execution is Arkiv's no-EVM entity engine over GolemDB, and whose header
/// <c>stateRoot</c> is GolemDB's commitment.
/// </summary>
/// <remarks>
/// Everything Ethereum about the chain — blocks, transactions, receipts, the Engine API, devp2p, the base-fee
/// floor (the chain spec's <c>Eip1559BaseFeeMinValue</c>) — is Nethermind's own. What this plugin replaces:
/// <list type="bullet">
/// <item>user transaction execution, by Arkiv's executor (<see cref="ArkivBlockProcessor"/>, <see cref="ArkivTransactionProcessor"/>);</item>
/// <item>the header state root, by GolemDB's (<see cref="ArkivGenesisBuilder"/>, <see cref="ArkivWorldStateManager"/>);</item>
/// <item>the <c>arkiv_*</c> RPC namespace (<see cref="ArkivRpcModule"/>);</item>
/// <item>state sync: a peer's GolemDB store over <c>arkiv/1</c> instead of snap (<see cref="ArkivStateSyncRunner"/>).</item>
/// </list>
/// </remarks>
public sealed class ArkivPlugin(IArkivConfig config, ChainSpec chainSpec) : INethermindPlugin
{
    public string Name => "Arkiv";
    public string Description => "Arkiv database-chain (GolemDB) execution";
    public string Author => "Nethermind";
    public bool Enabled => config.Enabled;
    public IModule Module => new ArkivModule(chainSpec);
}

public sealed class ArkivModule(ChainSpec chainSpec) : Module
{
    public const string DbName = "arkiv";

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        ApplyBaseFeeFloor(chainSpec);

        builder
            .AddDatabase(DbName)
            .AddSingleton<ArkivChain>(static ctx =>
            {
                IFlatDbConfig flat = ctx.Resolve<IFlatDbConfig>();
                if (flat.Enabled)
                {
                    throw new InvalidConfigurationException("Arkiv needs the patricia state backend: set FlatDb.Enabled=false.", ExitCodes.ConflictingConfigurations);
                }
                return new ArkivChain(
                    ctx.Resolve<IArkivConfig>(),
                    ctx.Resolve<IInitConfig>(),
                    ctx.ResolveKeyed<IDb>(DbName),
                    new Lazy<IBlockTree>(ctx.Resolve<IComponentContext>().Resolve<IBlockTree>),
                    ctx.Resolve<IEthereumEcdsa>(),
                    ctx.Resolve<ILogManager>());
            })
            .AddSingleton<ArkivRoots>()

            // The header's state root is GolemDB's; state is opened by the local account trie's root instead.
            .AddDecorator<IWorldStateManager, ArkivWorldStateManager>()
            .AddDecorator<IStateHeaderProvider, ArkivStateHeaderProvider>()
            .AddDecorator<IGenesisBuilder, ArkivGenesisBuilder>()

            // Execution: the block runs natively, transactions take its outcomes, system calls stay on the EVM.
            .AddScoped<ArkivBlockContext>()
            .AddScoped<IBlockProcessor, ArkivBlockProcessor>()
            .AddDecorator<ITransactionProcessor, ArkivTransactionProcessor>()
            // Building: the protocol purge first, candidates taken or refused by Arkiv's executor, not by the trie.
            .AddScoped<BlockProcessor.IBlockProductionTransactionPicker, ArkivTransactionPicker>()

            // State sync: the state is GolemDB, so a peer's store is fetched (arkiv/1) instead of the account trie (snap).
            .AddMessageSerializer<GetStoreManifestMessage, GetStoreManifestMessageSerializer>()
            .AddMessageSerializer<StoreManifestMessage, StoreManifestMessageSerializer>()
            .AddMessageSerializer<GetStoreChunkMessage, GetStoreChunkMessageSerializer>()
            .AddMessageSerializer<StoreChunkMessage, StoreChunkMessageSerializer>()
            .AddProtocolHandler<ArkivProtocolHandler>()
            .AddLast<IP2PCapabilityResolver, ArkivCapabilityResolver>()
            .AddSingleton<IStateSyncRunner, ArkivStateSyncRunner>()

            .RegisterSingletonJsonRpcModule<IArkivRpcModule, ArkivRpcModule>();
    }

    /// <summary>
    /// Arkiv's one consensus rule on top of Ethereum's: <c>next base fee = max(EIP-1559(parent), genesis base fee)</c>
    /// from block 0 (<c>arkiv-reth-chainspec</c>), which is Nethermind's <c>Eip1559BaseFeeMinValue</c>.
    /// </summary>
    /// <remarks>
    /// A network's published genesis file serves as its chain spec, and carries no such parameter, so it is taken from
    /// the genesis header unless the chain spec sets it.
    /// </remarks>
    internal static void ApplyBaseFeeFloor(ChainSpec chainSpec)
    {
        if (chainSpec.Parameters.Eip1559BaseFeeMinValue is not null || chainSpec.Genesis is not { } genesis) return;
        chainSpec.Parameters.Eip1559BaseFeeMinValue = genesis.BaseFeePerGas;
        chainSpec.Parameters.Eip1559BaseFeeMinValueTransition = 0;
    }
}
