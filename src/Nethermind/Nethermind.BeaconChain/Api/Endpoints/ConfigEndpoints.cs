// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary><c>/eth/v1/config/*</c>: spec constants, fork schedule, deposit contract.</summary>
internal static class ConfigEndpoints
{
    public static void Map(WebApplication app, BeaconApiContext ctx)
    {
        app.MapGet("/eth/v1/config/spec", c => Spec(c, ctx));
        app.MapGet("/eth/v1/config/fork_schedule", c => ForkSchedule(c, ctx));
        app.MapGet("/eth/v1/config/deposit_contract", c => DepositContract(c, ctx));
    }

    /// <summary>The network config's <c>DEPOSIT_CONTRACT_ADDRESS</c>, or <c>null</c> for a chain this driver has no network config for.</summary>
    /// <remarks>
    /// From consensus-specs v1.7.0-beta.2 <c>configs/mainnet.yaml</c> and the eth-clients <c>hoodi</c> and
    /// <c>sepolia</c> <c>metadata/config.yaml</c>, whose <c>DEPOSIT_CHAIN_ID</c> equals the chain id each is keyed by.
    /// </remarks>
    private static string? DepositContractAddress(ulong chainId) => chainId switch
    {
        BlockchainIds.Mainnet => "0x00000000219ab540356cBB839Cbe05303d7705Fa",
        BlockchainIds.Hoodi => "0x00000000219ab540356cBB839Cbe05303d7705Fa",
        BlockchainIds.Sepolia => "0x7f02C3E3c98b133055B8B348B2Ac625669Ed295D",
        _ => null,
    };

    private static Task DepositContract(HttpContext c, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (DepositContractAddress(ctx.Spec.ChainId) is not { } address)
        {
            return ApiErrors.Write(c, StatusCodes.Status500InternalServerError,
                $"No deposit contract is configured for chain {ctx.Spec.ChainId}.", c.RequestAborted);
        }

        return BeaconApiJson.WriteDataAsync(c, new DepositContractDto(ctx.Spec.ChainId.ToString(), address), c.RequestAborted);
    }

    /// <remarks>
    /// This is a deliberate subset of the full mainnet <c>config.yaml</c>: only fields this driver
    /// actually holds a value for (in <see cref="BeaconChainSpec"/> or <see cref="Presets"/>) are
    /// included. A caller cannot use an absent field's absence to infer anything about it beyond
    /// "not reported here" - nothing here claims completeness.
    /// </remarks>
    private static Task Spec(HttpContext c, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        BeaconChainSpec spec = ctx.Spec;
        Dictionary<string, string> data = new()
        {
            ["SECONDS_PER_SLOT"] = spec.SecondsPerSlot.ToString(),
            ["SLOTS_PER_EPOCH"] = spec.SlotsPerEpoch.ToString(),
            ["GENESIS_FORK_VERSION"] = spec.Forks[0].Version.ToHexString(withZeroX: true),
            ["ELECTRA_FORK_EPOCH"] = spec.ElectraForkEpoch.ToString(),
            ["ELECTRA_FORK_VERSION"] = spec.VersionForEpoch(spec.ElectraForkEpoch).ToHexString(withZeroX: true),
            ["FULU_FORK_EPOCH"] = spec.FuluForkEpoch.ToString(),
            ["FULU_FORK_VERSION"] = spec.VersionForEpoch(spec.FuluForkEpoch).ToHexString(withZeroX: true),
            ["MAX_BLOBS_PER_BLOCK_ELECTRA"] = spec.MaxBlobsPerBlockElectra.ToString(),
            ["DEPOSIT_CONTRACT_TREE_DEPTH"] = Presets.DepositContractTreeDepth.ToString(),
            ["MAX_COMMITTEES_PER_SLOT"] = Presets.MaxCommitteesPerSlot.ToString(),
            ["TARGET_COMMITTEE_SIZE"] = Presets.TargetCommitteeSize.ToString(),
            ["MAX_VALIDATORS_PER_COMMITTEE"] = Presets.MaxValidatorsPerCommittee.ToString(),
            ["MIN_SEED_LOOKAHEAD"] = Presets.MinSeedLookahead.ToString(),
            ["MAX_SEED_LOOKAHEAD"] = Presets.MaxSeedLookahead.ToString(),
            ["SHARD_COMMITTEE_PERIOD"] = Presets.ShardCommitteePeriod.ToString(),
            ["MIN_PER_EPOCH_CHURN_LIMIT"] = Presets.MinPerEpochChurnLimit.ToString(),
            ["CHURN_LIMIT_QUOTIENT"] = Presets.ChurnLimitQuotient.ToString(),
            ["EJECTION_BALANCE"] = Presets.EjectionBalance.ToString(),
            ["MIN_ACTIVATION_BALANCE"] = Presets.MinActivationBalance.ToString(),
            ["MAX_EFFECTIVE_BALANCE_ELECTRA"] = Presets.MaxEffectiveBalanceElectra.ToString(),
            ["SHUFFLE_ROUND_COUNT"] = Presets.ShuffleRoundCount.ToString(),
            ["HYSTERESIS_QUOTIENT"] = Presets.HysteresisQuotient.ToString(),
            ["HYSTERESIS_DOWNWARD_MULTIPLIER"] = Presets.HysteresisDownwardMultiplier.ToString(),
            ["HYSTERESIS_UPWARD_MULTIPLIER"] = Presets.HysteresisUpwardMultiplier.ToString(),
            ["MIN_DEPOSIT_AMOUNT"] = Presets.MinDepositAmount.ToString(),
            ["MAX_EFFECTIVE_BALANCE"] = Presets.MaxEffectiveBalance.ToString(),
            ["EFFECTIVE_BALANCE_INCREMENT"] = Presets.EffectiveBalanceIncrement.ToString(),
            ["MAX_DEPOSITS"] = Presets.MaxDeposits.ToString(),
            ["MIN_ATTESTATION_INCLUSION_DELAY"] = Presets.MinAttestationInclusionDelay.ToString(),
            ["EPOCHS_PER_ETH1_VOTING_PERIOD"] = Presets.EpochsPerEth1VotingPeriod.ToString(),
            ["SLOTS_PER_HISTORICAL_ROOT"] = Presets.SlotsPerHistoricalRoot.ToString(),
            ["MIN_EPOCHS_TO_INACTIVITY_PENALTY"] = Presets.MinEpochsToInactivityPenalty.ToString(),
            ["EPOCHS_PER_HISTORICAL_VECTOR"] = Presets.EpochsPerHistoricalVector.ToString(),
            ["EPOCHS_PER_SLASHINGS_VECTOR"] = Presets.EpochsPerSlashingsVector.ToString(),
            ["BASE_REWARD_FACTOR"] = Presets.BaseRewardFactor.ToString(),
            ["SYNC_COMMITTEE_SIZE"] = Presets.SyncCommitteeSize.ToString(),
            ["EPOCHS_PER_SYNC_COMMITTEE_PERIOD"] = Presets.EpochsPerSyncCommitteePeriod.ToString(),
            ["INACTIVITY_PENALTY_QUOTIENT_BELLATRIX"] = Presets.InactivityPenaltyQuotientBellatrix.ToString(),
            ["PROPORTIONAL_SLASHING_MULTIPLIER_BELLATRIX"] = Presets.ProportionalSlashingMultiplierBellatrix.ToString(),
            ["MAX_WITHDRAWALS_PER_PAYLOAD"] = Presets.MaxWithdrawalsPerPayload.ToString(),
            ["MAX_VALIDATORS_PER_WITHDRAWALS_SWEEP"] = Presets.MaxValidatorsPerWithdrawalsSweep.ToString(),
            ["MIN_SLASHING_PENALTY_QUOTIENT_ELECTRA"] = Presets.MinSlashingPenaltyQuotientElectra.ToString(),
            ["WHISTLEBLOWER_REWARD_QUOTIENT_ELECTRA"] = Presets.WhistleblowerRewardQuotientElectra.ToString(),
            ["PENDING_PARTIAL_WITHDRAWALS_LIMIT"] = Presets.PendingPartialWithdrawalsLimit.ToString(),
            ["PENDING_CONSOLIDATIONS_LIMIT"] = Presets.PendingConsolidationsLimit.ToString(),
            ["MAX_PENDING_PARTIALS_PER_WITHDRAWALS_SWEEP"] = Presets.MaxPendingPartialsPerWithdrawalsSweep.ToString(),
            ["MAX_PENDING_DEPOSITS_PER_EPOCH"] = Presets.MaxPendingDepositsPerEpoch.ToString(),
            ["MAX_PROPOSER_SLASHINGS"] = Presets.MaxProposerSlashings.ToString(),
            ["MAX_ATTESTER_SLASHINGS_ELECTRA"] = Presets.MaxAttesterSlashingsElectra.ToString(),
            ["MAX_ATTESTATIONS_ELECTRA"] = Presets.MaxAttestationsElectra.ToString(),
            ["MAX_VOLUNTARY_EXITS"] = Presets.MaxVoluntaryExits.ToString(),
            ["MAX_BLS_TO_EXECUTION_CHANGES"] = Presets.MaxBlsToExecutionChanges.ToString(),
            ["MAX_DEPOSIT_REQUESTS_PER_PAYLOAD"] = Presets.MaxDepositRequestsPerPayload.ToString(),
            ["MAX_WITHDRAWAL_REQUESTS_PER_PAYLOAD"] = Presets.MaxWithdrawalRequestsPerPayload.ToString(),
            ["MAX_CONSOLIDATION_REQUESTS_PER_PAYLOAD"] = Presets.MaxConsolidationRequestsPerPayload.ToString(),
            ["MAX_PER_EPOCH_ACTIVATION_CHURN_LIMIT"] = Presets.MaxPerEpochActivationChurnLimit.ToString(),
            ["MIN_PER_EPOCH_CHURN_LIMIT_ELECTRA"] = Presets.MinPerEpochChurnLimitElectra.ToString(),
        };

        if (spec.GloasForkEpoch != Presets.FarFutureEpoch)
        {
            data["GLOAS_FORK_EPOCH"] = spec.GloasForkEpoch.ToString();
            data["GLOAS_FORK_VERSION"] = spec.GloasForkVersion.ToHexString(withZeroX: true);
        }

        return BeaconApiJson.WriteDataAsync(c, data, c.RequestAborted);
    }

    private static Task ForkSchedule(HttpContext c, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        ForkScheduleEntry[] forks = ctx.Spec.Forks;
        ForkDto[] data = new ForkDto[forks.Length];
        for (int i = 0; i < forks.Length; i++)
        {
            byte[] previousVersion = i == 0 ? forks[0].Version : forks[i - 1].Version;
            data[i] = new ForkDto(
                previousVersion.ToHexString(withZeroX: true),
                forks[i].Version.ToHexString(withZeroX: true),
                forks[i].Epoch.ToString());
        }

        return BeaconApiJson.WriteDataAsync(c, data, c.RequestAborted);
    }

    private sealed record DepositContractDto(
        [property: JsonPropertyName("chain_id")] string ChainId,
        [property: JsonPropertyName("address")] string Address);

    private sealed record ForkDto(
        [property: JsonPropertyName("previous_version")] string PreviousVersion,
        [property: JsonPropertyName("current_version")] string CurrentVersion,
        [property: JsonPropertyName("epoch")] string Epoch);
}
