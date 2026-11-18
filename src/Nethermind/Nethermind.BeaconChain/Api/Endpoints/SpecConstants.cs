// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>The constants of the "Constants" tables of the consensus-specs v1.7.0-beta.2 files of the forks this node runs (phase0 to gloas), as <c>getSpec</c> serves them.</summary>
/// <remarks>
/// Numbers are decimal strings, <c>Bytes1</c>, <c>DomainType</c> and hash values are <c>0x</c> hex and
/// <c>PARTICIPATION_FLAG_WEIGHTS</c> is an array. A value comes from the constant the state transition reads wherever
/// the code has one; the others are listed once here.
/// </remarks>
internal static class SpecConstants
{
    public static readonly IReadOnlyDictionary<string, object> All = new Dictionary<string, object>
    {
        // phase0/beacon-chain.md Constants/Misc
        ["UINT64_MAX"] = Number(ulong.MaxValue),
        ["UINT64_MAX_SQRT"] = Number(uint.MaxValue),
        ["GENESIS_SLOT"] = Number(Presets.GenesisSlot),
        ["GENESIS_EPOCH"] = Number(Presets.GenesisEpoch),
        ["FAR_FUTURE_EPOCH"] = Number(Presets.FarFutureEpoch),
        ["BASE_REWARDS_PER_EPOCH"] = "4",
        ["JUSTIFICATION_BITS_LENGTH"] = Number((ulong)Presets.JustificationBitsLength),
        ["ENDIANNESS"] = "little",
        // Withdrawal prefixes: phase0, electra, gloas beacon-chain.md
        ["BLS_WITHDRAWAL_PREFIX"] = Hex(Presets.BlsWithdrawalPrefix),
        ["ETH1_ADDRESS_WITHDRAWAL_PREFIX"] = Hex(Presets.EthWithdrawalPrefix),
        ["COMPOUNDING_WITHDRAWAL_PREFIX"] = Hex(Presets.CompoundingWithdrawalPrefix),
        ["BUILDER_WITHDRAWAL_PREFIX"] = Hex(Presets.BuilderWithdrawalPrefix),
        // Domains: phase0, altair, capella, gloas beacon-chain.md
        ["DOMAIN_BEACON_PROPOSER"] = Hex(DomainType.BeaconProposer),
        ["DOMAIN_BEACON_ATTESTER"] = Hex(DomainType.BeaconAttester),
        ["DOMAIN_RANDAO"] = Hex(DomainType.Randao),
        ["DOMAIN_DEPOSIT"] = Hex(DomainType.Deposit),
        ["DOMAIN_VOLUNTARY_EXIT"] = Hex(DomainType.VoluntaryExit),
        ["DOMAIN_SELECTION_PROOF"] = Hex(DomainType.SelectionProof),
        ["DOMAIN_AGGREGATE_AND_PROOF"] = Hex(DomainType.AggregateAndProof),
        ["DOMAIN_APPLICATION_MASK"] = "0x00000001",
        ["DOMAIN_SYNC_COMMITTEE"] = Hex(DomainType.SyncCommittee),
        ["DOMAIN_SYNC_COMMITTEE_SELECTION_PROOF"] = Hex(DomainType.SyncCommitteeSelectionProof),
        ["DOMAIN_CONTRIBUTION_AND_PROOF"] = Hex(DomainType.ContributionAndProof),
        ["DOMAIN_BLS_TO_EXECUTION_CHANGE"] = Hex(DomainType.BlsToExecutionChange),
        ["DOMAIN_BEACON_BUILDER"] = Hex(DomainType.BeaconBuilder),
        ["DOMAIN_PTC_ATTESTER"] = Hex(DomainType.PtcAttester),
        ["DOMAIN_PROPOSER_PREFERENCES"] = Hex(DomainType.ProposerPreferences),
        ["DOMAIN_BUILDER_DEPOSIT"] = Hex(DomainType.BuilderDeposit),
        // phase0/{deposit-contract,validator,fork-choice,p2p-interface,weak-subjectivity,fast-confirmation}.md
        ["DEPOSIT_CONTRACT_TREE_DEPTH"] = Number((ulong)Presets.DepositContractTreeDepth),
        ["TARGET_AGGREGATORS_PER_COMMITTEE"] = "16",
        ["BASIS_POINTS"] = Number(Presets.BasisPoints),
        ["NODE_ID_BITS"] = "256",
        ["ETH_TO_GWEI"] = "1000000000",
        ["COMMITTEE_WEIGHT_ESTIMATION_ADJUSTMENT_FACTOR"] = "5",
        // altair/{beacon-chain,validator,bls}.md
        ["TIMELY_SOURCE_FLAG_INDEX"] = Number((ulong)Presets.TimelySourceFlagIndex),
        ["TIMELY_TARGET_FLAG_INDEX"] = Number((ulong)Presets.TimelyTargetFlagIndex),
        ["TIMELY_HEAD_FLAG_INDEX"] = Number((ulong)Presets.TimelyHeadFlagIndex),
        ["TIMELY_SOURCE_WEIGHT"] = Number(Presets.TimelySourceWeight),
        ["TIMELY_TARGET_WEIGHT"] = Number(Presets.TimelyTargetWeight),
        ["TIMELY_HEAD_WEIGHT"] = Number(Presets.TimelyHeadWeight),
        ["SYNC_REWARD_WEIGHT"] = Number(Presets.SyncRewardWeight),
        ["PROPOSER_WEIGHT"] = Number(Presets.ProposerWeight),
        ["WEIGHT_DENOMINATOR"] = Number(Presets.WeightDenominator),
        ["PARTICIPATION_FLAG_WEIGHTS"] = Presets.ParticipationFlagWeights.Select(Number).ToArray(),
        ["TARGET_AGGREGATORS_PER_SYNC_SUBCOMMITTEE"] = "16",
        ["SYNC_COMMITTEE_SUBNET_COUNT"] = "4",
        ["G2_POINT_AT_INFINITY"] = Hex(SignatureSets.G2PointAtInfinity),
        // altair, capella, electra, gloas light-client/sync-protocol.md
        ["FINALIZED_ROOT_GINDEX"] = "105",
        ["CURRENT_SYNC_COMMITTEE_GINDEX"] = "54",
        ["NEXT_SYNC_COMMITTEE_GINDEX"] = "55",
        ["EXECUTION_PAYLOAD_GINDEX"] = "25",
        ["FINALIZED_ROOT_GINDEX_ELECTRA"] = "169",
        ["CURRENT_SYNC_COMMITTEE_GINDEX_ELECTRA"] = "86",
        ["NEXT_SYNC_COMMITTEE_GINDEX_ELECTRA"] = "87",
        ["FINALIZED_ROOT_GINDEX_GLOAS"] = "735",
        ["CURRENT_SYNC_COMMITTEE_GINDEX_GLOAS"] = "2945",
        ["NEXT_SYNC_COMMITTEE_GINDEX_GLOAS"] = "2946",
        ["EXECUTION_BLOCK_HASH_GINDEX"] = "412",
        ["EXECUTION_BLOCK_HASH_GINDEX_DENEB"] = "812",
        ["EXECUTION_BLOCK_HASH_GINDEX_GLOAS"] = "2856",
        // bellatrix/{beacon-chain,optimistic-sync,p2p-interface}.md
        ["EMPTY_BLOCK_HASH"] = Hex(Keccak.Zero.Bytes),
        ["SAFE_SLOTS_TO_IMPORT_OPTIMISTICALLY"] = "128",
        ["PAYLOAD_STATUS_VALID"] = "0",
        ["PAYLOAD_STATUS_INVALIDATED"] = "1",
        ["PAYLOAD_STATUS_NOT_VALIDATED"] = "2",
        // deneb/beacon-chain.md
        ["VERSIONED_HASH_VERSION_KZG"] = Hex(KzgPolynomialCommitments.KzgBlobHashVersionV1),
        ["BYTES_PER_FIELD_ELEMENT"] = "32",
        // electra/beacon-chain.md
        ["UNSET_DEPOSIT_REQUESTS_START_INDEX"] = Number(Presets.UnsetDepositRequestsStartIndex),
        ["FULL_EXIT_REQUEST_AMOUNT"] = Number(Presets.FullExitRequestAmount),
        ["DEPOSIT_REQUEST_TYPE"] = Hex((byte)ExecutionRequestType.Deposit),
        ["WITHDRAWAL_REQUEST_TYPE"] = Hex((byte)ExecutionRequestType.WithdrawalRequest),
        ["CONSOLIDATION_REQUEST_TYPE"] = Hex((byte)ExecutionRequestType.ConsolidationRequest),
        // fulu/das-core.md
        ["UINT256_MAX"] = UInt256.MaxValue.ToString(),
        // gloas/beacon-chain.md
        ["BUILDER_INDEX_FLAG"] = Number(Presets.BuilderIndexFlag),
        ["BUILDER_INDEX_SELF_BUILD"] = Number(Presets.BuilderIndexSelfBuild),
        ["BUILDER_PAYMENT_THRESHOLD_NUMERATOR"] = Number(Presets.BuilderPaymentThresholdNumerator),
        ["BUILDER_PAYMENT_THRESHOLD_DENOMINATOR"] = Number(Presets.BuilderPaymentThresholdDenominator),
        ["PAYLOAD_BUILDER_VERSION"] = Number(Presets.PayloadBuilderVersion),
        ["BUILDER_DEPOSIT_REQUEST_TYPE"] = Hex((byte)ExecutionRequestType.BuilderDepositRequest),
        ["BUILDER_EXIT_REQUEST_TYPE"] = Hex((byte)ExecutionRequestType.BuilderExitRequest),
        // gloas/fork-choice.md
        ["PAYLOAD_TIMELY_THRESHOLD"] = Number(Presets.PtcSize / 2),
        ["DATA_AVAILABILITY_TIMELY_THRESHOLD"] = Number(Presets.PtcSize / 2),
        ["PAYLOAD_STATUS_EMPTY"] = Number((ulong)ForkChoicePayloadStatus.Empty),
        ["PAYLOAD_STATUS_FULL"] = Number((ulong)ForkChoicePayloadStatus.Full),
        ["PAYLOAD_STATUS_PENDING"] = Number((ulong)ForkChoicePayloadStatus.Pending),
        ["ATTESTATION_TIMELINESS_INDEX"] = "0",
        ["PTC_TIMELINESS_INDEX"] = "1",
        ["NUM_BLOCK_TIMELINESS_DEADLINES"] = "2",
    };

    private static string Number(ulong value) => value.ToString();
    private static string Hex(byte value) => "0x" + Convert.ToHexStringLower([value]);
    private static string Hex(ReadOnlySpan<byte> value) => "0x" + Convert.ToHexStringLower(value);
}
