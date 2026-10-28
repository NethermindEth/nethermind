// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Posting;
using Nethermind.Eez.Proving;
using Nethermind.Eez.Rpc;
using Nethermind.Eez.Sequencer;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Eez;

public class EezPlugin(ChainSpec chainSpec, IEezConfig eezConfig, IInitConfig initConfig) : INethermindPlugin
{
    private EezChainRules? _rules;

    public string Name => "Eez";
    public string Description => "EEZ rollup L2 execution rules";
    public string Author => "Nethermind";
    public bool Enabled => eezConfig.Enabled;
    public bool MustInitialize => true;

    public void InitTxTypesAndRlpDecoders(INethermindApi api)
    {
        EnsureEezGenesis(chainSpec, Rules);
        EnsureRolesSupported(eezConfig, Rules);
        EnsureFollowerConfig(eezConfig);
        EnsureSequencerConfig(eezConfig, api.Config<IKeyStoreConfig>());
        if (Rules.UsesNativeContract)
        {
            api.RegisterTxType<EezNativeContractTransactionForRpc>(EezTxType.CreateDecoder(Rules), EezTxType.CreateValidator(api.SpecProvider!.ChainId, Rules));
            api.RegisterTxType<EezNativeHistoryTransactionForRpc>(new EezNativeHistoryTxDecoder(), EezTxType.CreateValidator(api.SpecProvider.ChainId, Rules));
        }
        else if (Rules.UsesNativeSystemTransactions)
            api.RegisterTxType<EezNativeSystemTransactionForRpc>(EezTxType.CreateDecoder(Rules), EezTxType.CreateValidator(api.SpecProvider!.ChainId, Rules));
        else
            api.RegisterTxType<EezSystemTransactionForRpc>(EezTxType.CreateDecoder(), EezTxType.CreateValidator(api.SpecProvider!.ChainId));
    }

    public IModule Module => new EezModule(eezConfig, Rules);

    /// <summary>The genesis decides the rules: a <c>rollupX</c> section in its configuration makes the chain a RollupX chain.</summary>
    private EezChainRules Rules => _rules ??= LoadRules(initConfig.ChainSpecPath.GetApplicationResourcePath(), eezConfig.SkipConsensusLayerCalls);

    internal static EezChainRules LoadRules(string genesisPath, bool skipConsensusLayerCalls = false)
    {
        try
        {
            EezChainRules rules = EezChainRules.FromGenesisFile(genesisPath);
            return skipConsensusLayerCalls ? rules.WithoutConsensusLayerCalls() : rules;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new InvalidConfigurationException($"{nameof(IEezConfig)}.{nameof(IEezConfig.Enabled)} needs a valid EEZ genesis at {genesisPath}: {e.Message}",
                ExitCodes.ConflictingConfigurations);
        }
    }

    /// <summary>
    /// A RollupX chain follows L1 from its blobs on the L1 its genesis is pinned to; its sequencer is not built yet.
    /// </summary>
    internal static void EnsureRolesSupported(IEezConfig config, EezChainRules rules)
    {
        if (rules.Schedule is not { } schedule)
        {
            return;
        }

        string? refused = config switch
        {
            { SequencerRpcUrl: { Length: > 0 } } when rules.UsesNativeContract =>
                $"{nameof(IEezConfig.SequencerRpcUrl)} is not supported for native RollupX until unsafe history preparation is available",
            { MaxFollowerBatchBlocks: <= 0 } when rules.UsesNativeContract =>
                $"{nameof(IEezConfig.MaxFollowerBatchBlocks)} must be positive for a native RollupX follower",
            { FollowerEnabled: false } when rules.UsesNativeContract =>
                $"{nameof(IEezConfig.FollowerEnabled)} is required for native RollupX execution",
            { FollowerEnabled: true, L1ChainConfigPath: null or "" } when rules.UsesNativeContract =>
                $"{nameof(IEezConfig.L1ChainConfigPath)} is required for a native RollupX follower",
            { SequencerEnabled: true } => $"{nameof(IEezConfig.SequencerEnabled)} is not supported on a RollupX chain yet",
            { FollowerEnabled: true } when !Uri.TryCreate(config.L1BeaconUrl, UriKind.Absolute, out _) =>
                $"{nameof(IEezConfig.FollowerEnabled)} on a RollupX chain requires a valid {nameof(IEezConfig.L1BeaconUrl)}, its batches being in blobs",
            { FollowerEnabled: true } when config.L1ChainId != schedule.L1Reference.ChainId =>
                $"{nameof(IEezConfig.L1ChainId)} must be {schedule.L1Reference.ChainId}, the L1 the genesis is pinned to",
            _ => null,
        };
        if (refused is not null)
        {
            throw new InvalidConfigurationException($"{nameof(IEezConfig)}.{refused}.", ExitCodes.ConflictingConfigurations);
        }
    }

    internal static void EnsureFollowerConfig(IEezConfig config)
    {
        if (!config.FollowerEnabled)
        {
            return;
        }

        string? missing = config switch
        {
            { L1RpcUrl: null or "" } => nameof(IEezConfig.L1RpcUrl),
            _ when !Address.TryParse(config.RegistryAddress, out _) => nameof(IEezConfig.RegistryAddress),
            { RegistryDeployBlock: 0 } => nameof(IEezConfig.RegistryDeployBlock),
            { RollupId: 0 } => nameof(IEezConfig.RollupId),
            { L1ChainId: 0 } => nameof(IEezConfig.L1ChainId),
            { L2BlockTimeSeconds: 0 } => nameof(IEezConfig.L2BlockTimeSeconds),
            { L1LogScanBlocks: 0 } => nameof(IEezConfig.L1LogScanBlocks),
            { L1PollingIntervalMs: <= 0 } => nameof(IEezConfig.L1PollingIntervalMs),
            _ => null,
        };
        if (missing is not null)
        {
            throw new InvalidConfigurationException(
                $"{nameof(IEezConfig)}.{nameof(IEezConfig.FollowerEnabled)} requires a valid {nameof(IEezConfig)}.{missing}.", ExitCodes.ConflictingConfigurations);
        }
    }

    internal static void EnsureSequencerConfig(IEezConfig config, IKeyStoreConfig keyStoreConfig)
    {
        if (!config.SequencerEnabled)
        {
            return;
        }

        RollupTiming timing = RollupTiming.From(config);
        string? violation = config switch
        {
            { FollowerEnabled: false } => $"requires {nameof(IEezConfig)}.{nameof(IEezConfig.FollowerEnabled)}, which confirms what L1 settled",
            { SequencerRpcUrl: { Length: > 0 } } => $"excludes {nameof(IEezConfig)}.{nameof(IEezConfig.SequencerRpcUrl)}: the node is the sequencer",
            { Provers.Length: 0 or > AttestationQuorum.MaxAttesters } => $"requires 1 to {AttestationQuorum.MaxAttesters} {nameof(IEezConfig)}.{nameof(IEezConfig.Provers)}",
            _ when !Address.TryParse(config.PosterAddress, out _) => $"requires a valid {nameof(IEezConfig)}.{nameof(IEezConfig.PosterAddress)}",
            { PosterPasswordFile: null or "" } => $"requires {nameof(IEezConfig)}.{nameof(IEezConfig.PosterPasswordFile)}",
            _ when keyStoreConfig.FindUnlockAccountIndex(new Address(config.PosterAddress!)) >= 0 =>
                $"requires {nameof(IEezConfig)}.{nameof(IEezConfig.PosterAddress)} to stay out of {nameof(IKeyStoreConfig)}.{nameof(IKeyStoreConfig.UnlockAccounts)}, " +
                "where the node's JSON-RPC signing methods would reach it",
            { SequencerFeeRecipient: { Length: > 0 } recipient } when !Address.TryParse(recipient, out _) =>
                $"requires {nameof(IEezConfig)}.{nameof(IEezConfig.SequencerFeeRecipient)} to be an address",
            _ => timing.FindViolation() is { } rule ? $"needs a timing where {rule}" : ProversViolation(config.Provers) ?? DaViolation(config, timing),
        };
        if (violation is not null)
        {
            throw new InvalidConfigurationException($"{nameof(IEezConfig)}.{nameof(IEezConfig.SequencerEnabled)} {violation}.", ExitCodes.ConflictingConfigurations);
        }
    }

    private static string? DaViolation(IEezConfig config, RollupTiming timing)
    {
        Address beneficiary = string.IsNullOrEmpty(config.SequencerFeeRecipient) ? Address.Zero : new Address(config.SequencerFeeRecipient);
        return PostBatchGas.DaBytesPerBlock(config.MaxPostBatchGas, config.RollupId, beneficiary, config.Provers.Length, timing.K) == 0
            ? $"needs a {nameof(IEezConfig)}.{nameof(IEezConfig.MaxPostBatchGas)} that leaves room for the transactions of a slot's blocks"
            : null;
    }

    private static string? ProversViolation(string[] provers)
    {
        HashSet<Address> proofSystems = [];
        foreach (string entry in provers)
        {
            if (ProverEndpoint.Parse(entry) is not { } endpoint)
            {
                return $"requires every {nameof(IEezConfig)}.{nameof(IEezConfig.Provers)} entry as url=attester=proofSystem, not '{entry}'";
            }

            if (!proofSystems.Add(endpoint.ProofSystem))
            {
                return $"names proof system {endpoint.ProofSystem} in more than one {nameof(IEezConfig)}.{nameof(IEezConfig.Provers)} entry";
            }
        }

        return null;
    }

    internal static void EnsureEezGenesis(ChainSpec chainSpec, EezChainRules rules)
    {
        ChainSpecAllocation? eezl2 = chainSpec.Allocations is { } allocations
            && allocations.TryGetValue(rules.Eezl2Address, out ChainSpecAllocation? allocation)
                ? allocation
                : null;
        if (eezl2?.Code is not { Length: > 0 })
        {
            throw new InvalidConfigurationException(
                $"{nameof(IEezConfig)}.{nameof(IEezConfig.Enabled)} requires an EEZ genesis with the EEZL2 predeploy at {rules.Eezl2Address}.",
                ExitCodes.ConflictingConfigurations);
        }
    }
}
