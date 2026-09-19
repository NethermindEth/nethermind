// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// The one-time Fulu -&gt; Gloas state upgrade (spec <c>upgrade_to_gloas</c>,
/// <c>specs/gloas/fork.md</c> "Upgrading the state", fetched from ethereum/consensus-specs `master`
/// 2026-09-19) and the two post-construction steps the spec runs against the new state:
/// <c>onboard_builders_from_pending_deposits</c> and <c>initialize_ptc_window</c>.
/// </summary>
/// <remarks>
/// Runs once, at the boundary slot, and does not need a Gloas-shaped
/// <c>BeaconStateAccessors</c>/<c>CommitteeCache</c> stack: every accessor call below
/// (<see cref="BeaconStateAccessors.GetSeed"/>, <see cref="CommitteeCache"/>) reads <paramref
/// name="pre"/>, the Fulu state, rather than the Gloas state the spec pseudocode nominally calls
/// them against. That substitution is exact, not approximate: validators, balances and randao_mixes
/// are copied unchanged into the new state and the slot does not advance across the upgrade, so
/// every accessor pre and post would read would agree. This is what "let a second fork exist
/// without duplicating the whole state transition" means in practice for this one function - it
/// borrows the existing, already-tested Fulu accessors instead of porting them to a second concrete
/// type that would return identical answers.
/// </remarks>
public static class GloasForkTransition
{
    /// <summary>
    /// Spec <c>upgrade_to_gloas</c>. Runs when <c>pre.Slot % SLOTS_PER_EPOCH == 0 &amp;&amp;
    /// compute_epoch_at_slot(pre.Slot) == GLOAS_FORK_EPOCH</c> - the caller (see
    /// <see cref="ForkedStateTransition"/>) is responsible for advancing <paramref name="pre"/> to
    /// exactly that boundary slot before calling this.
    /// </summary>
    public static BeaconStateGloas UpgradeToGloas(BeaconStateFulu pre, BeaconChainSpec spec)
    {
        ulong epoch = pre.GetCurrentEpoch();
        ExecutionPayloadHeader preHeader = pre.LatestExecutionPayloadHeader!;
        BeaconBlockHeader preLatestBlockHeader = pre.LatestBlockHeader!;

        BeaconStateGloas post = new()
        {
            GenesisTime = pre.GenesisTime,
            GenesisValidatorsRoot = pre.GenesisValidatorsRoot,
            Slot = pre.Slot,
            Fork = new Fork
            {
                PreviousVersion = pre.Fork!.CurrentVersion,
                // [Modified in Gloas]
                CurrentVersion = spec.GloasForkVersion,
                Epoch = epoch,
            },
            LatestBlockHeader = preLatestBlockHeader,
            BlockRoots = pre.BlockRoots,
            StateRoots = pre.StateRoots,
            HistoricalRoots = pre.HistoricalRoots,
            Eth1Data = pre.Eth1Data,
            Eth1DataVotes = pre.Eth1DataVotes,
            Eth1DepositIndex = pre.Eth1DepositIndex,
            // [Modified in Gloas:EIP7688] (Validators/Balances/... retype to ProgressiveList; the
            // underlying CLR array is unaffected, so this is a plain copy either way)
            Validators = pre.Validators,
            Balances = pre.Balances,
            RandaoMixes = pre.RandaoMixes,
            Slashings = pre.Slashings,
            PreviousEpochParticipation = pre.PreviousEpochParticipation,
            CurrentEpochParticipation = pre.CurrentEpochParticipation,
            JustificationBits = pre.JustificationBits,
            PreviousJustifiedCheckpoint = pre.PreviousJustifiedCheckpoint,
            CurrentJustifiedCheckpoint = pre.CurrentJustifiedCheckpoint,
            FinalizedCheckpoint = pre.FinalizedCheckpoint,
            InactivityScores = pre.InactivityScores,
            CurrentSyncCommittee = pre.CurrentSyncCommittee,
            NextSyncCommittee = pre.NextSyncCommittee,
            // [Modified in Gloas:EIP7732] Removed `latest_execution_payload_header`.
            // [New in Gloas:EIP7732]
            LatestBlockHash = preHeader.BlockHash,
            NextWithdrawalIndex = pre.NextWithdrawalIndex,
            NextWithdrawalValidatorIndex = pre.NextWithdrawalValidatorIndex,
            HistoricalSummaries = pre.HistoricalSummaries,
            DepositRequestsStartIndex = pre.DepositRequestsStartIndex,
            DepositBalanceToConsume = pre.DepositBalanceToConsume,
            ExitBalanceToConsume = pre.ExitBalanceToConsume,
            EarliestExitEpoch = pre.EarliestExitEpoch,
            ConsolidationBalanceToConsume = pre.ConsolidationBalanceToConsume,
            EarliestConsolidationEpoch = pre.EarliestConsolidationEpoch,
            PendingDeposits = pre.PendingDeposits,
            PendingPartialWithdrawals = pre.PendingPartialWithdrawals,
            PendingConsolidations = pre.PendingConsolidations,
            ProposerLookahead = pre.ProposerLookahead,
            // [New in Gloas:EIP7732] Builders() - empty registry; builders only ever enter through
            // onboarding below or, from here on, BuilderDepositRequest.
            Builders = [],
            NextWithdrawalBuilderIndex = 0,
            // [New in Gloas:EIP7732] every historical slot starts marked "payload delivered" so
            // pre-fork history is never treated as having missed its payload.
            ExecutionPayloadAvailability = new BitArray((int)Presets.SlotsPerHistoricalRoot, true),
            BuilderPendingPayments = EmptyBuilderPendingPayments(),
            BuilderPendingWithdrawals = [],
            // [New in Gloas:EIP7732] a self-build placeholder bid standing in for the payload the
            // pre-fork header already committed to, so the first post-fork slot has a bid to extend.
            LatestExecutionPayloadBid = new ExecutionPayloadBid
            {
                ParentBlockHash = preHeader.ParentHash,
                ParentBlockRoot = preLatestBlockHeader.ParentRoot,
                BlockHash = preHeader.BlockHash,
                PrevRandao = preHeader.PrevRandao,
                FeeRecipient = Address.Zero,
                GasLimit = preHeader.GasLimit,
                BuilderIndex = Presets.BuilderIndexSelfBuild,
                Slot = preLatestBlockHeader.Slot,
                Value = 0,
                ExecutionPayment = 0,
                BlobKzgCommitments = [],
                // hash_tree_root(ExecutionRequests.empty()): the spec's bare `ExecutionRequests` name
                // here resolves in the gloas module's own namespace (every other bare container name
                // in this function - Validators, Builders, BlobKZGCommitments - is unambiguously the
                // Gloas type), so this is ExecutionRequestsGloas's zero-value root, not the pre-Gloas
                // ExecutionRequests' - see 'unresolved' for the case this reading could be wrong.
                ExecutionRequestsRoot = SszRoots.HashTreeRoot(new ExecutionRequestsGloas()),
            },
            PayloadExpectedWithdrawals = [],
            // Placeholder until InitializePtcWindow runs below, matching the spec's own
            // PayloadTimelinessCommitteeWindow() default-constructed value at this point in
            // upgrade_to_gloas, before its `post.ptc_window = initialize_ptc_window(post)` line.
            PtcWindow = EmptyPtcWindow((int)Presets.PtcWindowLength),
        };

        // [New in Gloas:EIP7732]
        OnboardBuildersFromPendingDeposits(post, epoch);

        // [New in Gloas:EIP7732]
        post.PtcWindow = InitializePtcWindow(pre, epoch);

        return post;
    }

    private static BuilderPendingPayment[] EmptyBuilderPendingPayments() =>
        [.. Enumerable.Range(0, (int)Presets.BuilderPendingPaymentsLength)
            .Select(static _ => new BuilderPendingPayment { Withdrawal = new BuilderPendingWithdrawal() })];

    private static PayloadTimelinessCommittee[] EmptyPtcWindow(int length) =>
        [.. Enumerable.Range(0, length).Select(static _ => new PayloadTimelinessCommittee())];

    /// <summary>Spec <c>is_builder_withdrawal_credential</c>.</summary>
    public static bool IsBuilderWithdrawalCredential(Hash256 withdrawalCredentials) =>
        withdrawalCredentials.Bytes[0] == Presets.BuilderWithdrawalPrefix;

    /// <summary>Spec <c>is_pending_validator</c>: is there a signature-valid pending deposit for <paramref name="pubkey"/>?</summary>
    public static bool IsPendingValidator(IReadOnlyList<PendingDeposit> pendingDeposits, BlsPublicKey pubkey)
    {
        foreach (PendingDeposit deposit in pendingDeposits)
        {
            if (deposit.Pubkey.Equals(pubkey) &&
                DepositSignatureVerifier.IsValid(deposit.Pubkey, deposit.WithdrawalCredentials!, deposit.Amount, deposit.Signature))
                return true;
        }
        return false;
    }

    /// <summary>Spec <c>get_index_for_new_builder</c>: reuse an exited, drained builder slot before appending.</summary>
    public static ulong GetIndexForNewBuilder(IReadOnlyList<Builder> builders, ulong currentEpoch)
    {
        for (int i = 0; i < builders.Count; i++)
        {
            if (builders[i].WithdrawableEpoch <= currentEpoch && builders[i].Balance == 0)
                return (ulong)i;
        }
        return (ulong)builders.Count;
    }

    /// <summary>Spec <c>add_builder_to_registry</c>.</summary>
    public static void AddBuilderToRegistry(BeaconStateGloas state, BlsPublicKey pubkey, byte version, Address executionAddress, ulong amount, ulong slot)
    {
        List<Builder> builders = [.. state.Builders!];
        ulong index = GetIndexForNewBuilder(builders, BeaconStateAccessors.ComputeEpochAtSlot(state.Slot));
        Builder builder = new()
        {
            Pubkey = pubkey,
            Version = version,
            ExecutionAddress = executionAddress,
            Balance = amount,
            DepositEpoch = BeaconStateAccessors.ComputeEpochAtSlot(slot),
            WithdrawableEpoch = Presets.FarFutureEpoch,
        };
        if (index < (ulong)builders.Count)
            builders[(int)index] = builder;
        else
            builders.Add(builder);
        state.Builders = [.. builders];
    }

    /// <summary>
    /// Spec <c>onboard_builders_from_pending_deposits</c>: the only path, from the fork onward, that
    /// creates a builder from a deposit rather than a <c>BuilderDepositRequest</c>.
    /// </summary>
    public static void OnboardBuildersFromPendingDeposits(BeaconStateGloas state, ulong currentEpoch)
    {
        HashSet<BlsPublicKey> validatorPubkeys = state.Validators!.Select(static v => v.Pubkey).ToHashSet();
        List<PendingDeposit> kept = [];

        foreach (PendingDeposit deposit in state.PendingDeposits ?? [])
        {
            // Deposits for existing validators stay in the pending queue (validator top-up, not a builder concern).
            if (validatorPubkeys.Contains(deposit.Pubkey))
            {
                kept.Add(deposit);
                continue;
            }

            // Recomputed every iteration: applying a deposit below can add a builder to the registry.
            HashSet<BlsPublicKey> builderPubkeys = state.Builders!.Select(static b => b.Pubkey).ToHashSet();

            if (!builderPubkeys.Contains(deposit.Pubkey))
            {
                if (!IsBuilderWithdrawalCredential(deposit.WithdrawalCredentials!))
                {
                    kept.Add(deposit);
                    continue;
                }
                if (IsPendingValidator(kept, deposit.Pubkey))
                {
                    kept.Add(deposit);
                    continue;
                }
                if (!DepositSignatureVerifier.IsValid(deposit.Pubkey, deposit.WithdrawalCredentials!, deposit.Amount, deposit.Signature))
                    continue;

                AddBuilderToRegistry(
                    state,
                    deposit.Pubkey,
                    Presets.PayloadBuilderVersion,
                    new Address(deposit.WithdrawalCredentials!.Bytes[12..]),
                    deposit.Amount,
                    deposit.Slot);
            }
            else
            {
                Builder[] builders = state.Builders!;
                int builderIndex = Array.FindIndex(builders, b => b.Pubkey.Equals(deposit.Pubkey));
                builders[builderIndex] = new Builder
                {
                    Pubkey = builders[builderIndex].Pubkey,
                    Version = builders[builderIndex].Version,
                    ExecutionAddress = builders[builderIndex].ExecutionAddress,
                    Balance = builders[builderIndex].Balance + deposit.Amount,
                    DepositEpoch = builders[builderIndex].DepositEpoch,
                    WithdrawableEpoch = builders[builderIndex].WithdrawableEpoch,
                };
            }
        }

        state.PendingDeposits = [.. kept];
    }

    /// <summary>
    /// Spec <c>initialize_ptc_window</c>: 32 empty (zero-index) committees standing in for "previous
    /// epoch" history that never had a PTC, followed by the real committees for the current epoch and
    /// the one epoch of lookahead the shuffling already determines.
    /// </summary>
    public static PayloadTimelinessCommittee[] InitializePtcWindow(BeaconStateFulu pre, ulong currentEpoch)
    {
        PayloadTimelinessCommittee[] emptyPreviousEpoch = EmptyPtcWindow((int)Presets.SlotsPerEpoch);

        List<PayloadTimelinessCommittee> ptcs = [];
        for (ulong e = 0; e < 1 + Presets.MinSeedLookahead; e++)
        {
            ulong epoch = currentEpoch + e;
            ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
            CommitteeCache committees = CommitteeCache.Build(pre, epoch);
            for (ulong i = 0; i < Presets.SlotsPerEpoch; i++)
                ptcs.Add(ComputePtc(pre, committees, startSlot + i));
        }

        return [.. emptyPreviousEpoch, .. ptcs];
    }

    /// <summary>Spec <c>compute_ptc</c>: the (possibly-duplicate) payload timeliness committee for one slot.</summary>
    private static PayloadTimelinessCommittee ComputePtc(BeaconStateFulu pre, CommitteeCache committees, ulong slot)
    {
        ulong epoch = BeaconStateAccessors.ComputeEpochAtSlot(slot);
        Hash256 domainSeed = pre.GetSeed(epoch, DomainType.PtcAttester);
        Span<byte> preimage = stackalloc byte[32 + 8];
        domainSeed.Bytes.CopyTo(preimage);
        BinaryPrimitives.WriteUInt64LittleEndian(preimage[32..], slot);
        byte[] seed = SHA256.HashData(preimage);

        List<int> indices = [];
        for (int i = 0; i < committees.CommitteesPerSlot; i++)
            indices.AddRange(committees.GetBeaconCommittee(slot, i).ToArray());

        return new PayloadTimelinessCommittee
        {
            Indices = [.. ComputeBalanceWeightedSelection(pre, indices, seed, (int)Presets.PtcSize, shuffleIndices: false).Select(static i => (ulong)i)],
        };
    }

    /// <summary>
    /// Spec <c>compute_balance_weighted_selection</c>: samples <paramref name="size"/> indices from
    /// <paramref name="indices"/> by effective balance, with replacement. Mirrors the existing
    /// <see cref="BeaconStateAccessors.ComputeProposerIndex"/> rejection-sampling loop, generalized to
    /// keep sampling until <paramref name="size"/> selections are made instead of stopping at the first.
    /// </summary>
    private static int[] ComputeBalanceWeightedSelection(BeaconStateFulu state, IReadOnlyList<int> indices, ReadOnlySpan<byte> seed, int size, bool shuffleIndices)
    {
        if (indices.Count == 0)
            throw new BeaconStateException("Cannot select a balance-weighted committee from an empty candidate list");

        Span<byte> preimage = stackalloc byte[32 + 8];
        seed[..32].CopyTo(preimage);

        int[] selected = new int[size];
        int selectedCount = 0;
        // Re-hashed only every 16th draw and reused for the 16 draws in between, matching the spec's
        // own random-byte batching (each sha256 output yields eight 2-byte random values).
        Span<byte> randomBytes = stackalloc byte[32];
        ulong i = 0;
        while (selectedCount < size)
        {
            ulong offset = i % 16 * 2;
            if (offset == 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(preimage[32..], i / 16);
                SHA256.HashData(preimage, randomBytes);
            }

            int nextIndex = (int)(i % (ulong)indices.Count);
            if (shuffleIndices)
                nextIndex = SwapOrNotShuffle.ComputeShuffledIndex(nextIndex, indices.Count, seed);

            ulong effectiveBalance = state.Validators![indices[nextIndex]].EffectiveBalance;
            ulong randomValue = BinaryPrimitives.ReadUInt16LittleEndian(randomBytes[(int)offset..]);
            ulong weight = effectiveBalance * BeaconStateAccessors.MaxRandomValue;
            ulong threshold = Presets.MaxEffectiveBalanceElectra * randomValue;
            if (weight >= threshold)
                selected[selectedCount++] = indices[nextIndex];
            i++;
        }

        return selected;
    }
}
