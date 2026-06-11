// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Transaction = Nethermind.BeaconChain.Types.Transaction;
using Withdrawal = Nethermind.BeaconChain.Types.Withdrawal;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Reports execution payload validity to the state transition — the spec's
/// <c>ExecutionEngine.verify_and_notify_new_payload</c>.
/// </summary>
public interface INewPayloadNotifier
{
    /// <summary>Returns whether the execution layer accepted the body's payload (with its versioned hashes and execution requests).</summary>
    bool NotifyNewPayload(BeaconBlockBody body);
}

/// <summary>
/// The Electra/Fulu <c>process_block</c> sub-transitions over <see cref="BeaconStateFulu"/>.
/// </summary>
/// <remarks>
/// Ported from consensus-specs v1.6.1 (<c>specs/electra/beacon-chain.md</c> block processing,
/// plus the Fulu <c>process_execution_payload</c> blob-limit change), cross-checked against
/// Lighthouse <c>consensus/state_processing/src/per_block_processing</c>. Spec asserts throw
/// <see cref="BeaconStateException"/>, so catching it means the block is invalid. Each
/// <c>Process*</c> method is independently callable, matching the per-operation spec test
/// fixtures. The <c>verifySignatures</c> flags exist for replaying already-verified blocks and
/// for spec tests with <c>bls_setting: 2</c>.
/// </remarks>
public static class BlockProcessing
{
    /// <summary>
    /// Spec <c>process_block</c>. The outer block (proposer) signature is not part of
    /// <c>process_block</c>; verify it separately with
    /// <see cref="SignatureSets.VerifyProposerSignature(BeaconStateFulu, SignedBeaconBlock, PubkeyCache)"/>.
    /// </summary>
    /// <param name="maxBlobsPerBlock">The blob limit for the state's epoch; see <see cref="ProcessExecutionPayload"/>.</param>
    public static void ProcessBlock(BeaconStateFulu state, BeaconBlock block, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, ulong maxBlobsPerBlock, bool verifySignatures = true)
    {
        BeaconBlockBody body = block.Body!;
        ProcessBlockHeader(state, block);
        ProcessWithdrawals(state, body.ExecutionPayload!);
        ProcessExecutionPayload(state, body, notifier, maxBlobsPerBlock);
        ProcessRandao(state, body, pubkeys, verifySignatures);
        ProcessEth1Data(state, body);
        ProcessOperations(state, body, cache, pubkeys, verifySignatures);
        ProcessSyncAggregate(state, body.SyncAggregate!, cache, verifySignatures);
    }

    /// <summary>Spec <c>process_block_header</c>: validates the block against the chain tip and caches it as the latest header.</summary>
    public static void ProcessBlockHeader(BeaconStateFulu state, BeaconBlock block)
    {
        if (block.Slot != state.Slot)
            throw new BeaconStateException($"Block slot {block.Slot} does not match state slot {state.Slot}");
        if (block.Slot <= state.LatestBlockHeader!.Slot)
            throw new BeaconStateException($"Block slot {block.Slot} is not newer than latest header slot {state.LatestBlockHeader.Slot}");
        if (block.ProposerIndex != state.GetBeaconProposerIndex())
            throw new BeaconStateException($"Block proposer {block.ProposerIndex} does not match expected proposer {state.GetBeaconProposerIndex()}");
        if (block.ParentRoot != SszRoots.HashTreeRoot(state.LatestBlockHeader))
            throw new BeaconStateException($"Block parent root {block.ParentRoot} does not match latest header root");

        state.LatestBlockHeader = new BeaconBlockHeader
        {
            Slot = block.Slot,
            ProposerIndex = block.ProposerIndex,
            ParentRoot = block.ParentRoot,
            StateRoot = Hash256.Zero, // Overwritten by the next process_slot.
            BodyRoot = SszRoots.HashTreeRoot(block.Body!),
        };

        if (state.Validators![(int)block.ProposerIndex].Slashed)
            throw new BeaconStateException($"Proposer {block.ProposerIndex} is slashed");
    }

    /// <summary>
    /// Spec <c>process_withdrawals</c> (Electra): asserts the payload withdrawals equal
    /// <c>get_expected_withdrawals</c> (pending partial sweep plus the bounded full/partial
    /// validator sweep), deducts the withdrawn balances, and advances the sweep cursors.
    /// </summary>
    public static void ProcessWithdrawals(BeaconStateFulu state, ExecutionPayload payload)
    {
        (List<Withdrawal> expected, int processedPartialWithdrawalsCount) = GetExpectedWithdrawals(state);

        Withdrawal[] actual = payload.Withdrawals ?? [];
        if (actual.Length != expected.Count)
            throw new BeaconStateException($"Payload has {actual.Length} withdrawals, expected {expected.Count}");
        for (int i = 0; i < actual.Length; i++)
        {
            if (!WithdrawalEquals(actual[i], expected[i]))
                throw new BeaconStateException($"Payload withdrawal {i} does not match the expected withdrawal");
        }

        foreach (Withdrawal withdrawal in expected)
        {
            state.DecreaseBalance((int)withdrawal.ValidatorIndex, withdrawal.Amount);
        }

        state.PendingPartialWithdrawals = state.PendingPartialWithdrawals![processedPartialWithdrawalsCount..];

        if (expected.Count != 0)
            state.NextWithdrawalIndex = expected[^1].Index + 1;

        ulong validatorCount = (ulong)state.Validators!.Length;
        state.NextWithdrawalValidatorIndex = expected.Count == Presets.MaxWithdrawalsPerPayload
            // Next sweep starts after the latest withdrawal's validator index.
            ? (expected[^1].ValidatorIndex + 1) % validatorCount
            // Advance the sweep by its max length when the withdrawal set was not full.
            : (state.NextWithdrawalValidatorIndex + (ulong)Presets.MaxValidatorsPerWithdrawalsSweep) % validatorCount;
    }

    /// <summary>Spec <c>get_expected_withdrawals</c> (Electra), also returning the number of consumed pending partial withdrawals.</summary>
    private static (List<Withdrawal> Withdrawals, int ProcessedPartialWithdrawalsCount) GetExpectedWithdrawals(BeaconStateFulu state)
    {
        ulong epoch = state.GetCurrentEpoch();
        ulong withdrawalIndex = state.NextWithdrawalIndex;
        ulong validatorIndex = state.NextWithdrawalValidatorIndex;
        List<Withdrawal> withdrawals = [];
        int processedPartialWithdrawalsCount = 0;

        // [New in Electra:EIP7251] Consume pending partial withdrawals.
        foreach (PendingPartialWithdrawal pending in state.PendingPartialWithdrawals!)
        {
            if (pending.WithdrawableEpoch > epoch || withdrawals.Count == Presets.MaxPendingPartialsPerWithdrawalsSweep)
                break;

            Validator validator = state.Validators![(int)pending.ValidatorIndex];
            bool hasSufficientEffectiveBalance = validator.EffectiveBalance >= Presets.MinActivationBalance;
            ulong balance = state.Balances![(int)pending.ValidatorIndex] - TotalWithdrawn(withdrawals, pending.ValidatorIndex);
            if (validator.ExitEpoch == Presets.FarFutureEpoch && hasSufficientEffectiveBalance && balance > Presets.MinActivationBalance)
            {
                withdrawals.Add(new Withdrawal
                {
                    Index = withdrawalIndex++,
                    ValidatorIndex = pending.ValidatorIndex,
                    Address = new Address(validator.WithdrawalCredentials!),
                    Amount = Math.Min(balance - Presets.MinActivationBalance, pending.Amount),
                });
            }
            processedPartialWithdrawalsCount++;
        }

        // Sweep for remaining full and partial withdrawals.
        int bound = Math.Min(state.Validators!.Length, Presets.MaxValidatorsPerWithdrawalsSweep);
        for (int i = 0; i < bound; i++)
        {
            Validator validator = state.Validators[(int)validatorIndex];
            ulong balance = state.Balances![(int)validatorIndex] - TotalWithdrawn(withdrawals, validatorIndex);
            if (validator.IsFullyWithdrawableValidator(balance, epoch))
            {
                withdrawals.Add(new Withdrawal
                {
                    Index = withdrawalIndex++,
                    ValidatorIndex = validatorIndex,
                    Address = new Address(validator.WithdrawalCredentials!),
                    Amount = balance,
                });
            }
            else if (validator.IsPartiallyWithdrawableValidator(balance))
            {
                withdrawals.Add(new Withdrawal
                {
                    Index = withdrawalIndex++,
                    ValidatorIndex = validatorIndex,
                    Address = new Address(validator.WithdrawalCredentials!),
                    Amount = balance - validator.GetMaxEffectiveBalance(),
                });
            }
            if (withdrawals.Count == Presets.MaxWithdrawalsPerPayload)
                break;
            validatorIndex = (validatorIndex + 1) % (ulong)state.Validators.Length;
        }

        return (withdrawals, processedPartialWithdrawalsCount);
    }

    private static ulong TotalWithdrawn(List<Withdrawal> withdrawals, ulong validatorIndex)
    {
        ulong total = 0;
        foreach (Withdrawal withdrawal in withdrawals)
        {
            if (withdrawal.ValidatorIndex == validatorIndex)
                total += withdrawal.Amount;
        }
        return total;
    }

    private static bool WithdrawalEquals(Withdrawal a, Withdrawal b) =>
        a.Index == b.Index && a.ValidatorIndex == b.ValidatorIndex && a.Address == b.Address && a.Amount == b.Amount;

    /// <summary>
    /// Spec <c>process_execution_payload</c> (Fulu): validates the payload against the state,
    /// queries the execution layer, and caches the payload header.
    /// </summary>
    /// <param name="maxBlobsPerBlock">
    /// Fulu <c>get_blob_parameters(get_current_epoch(state)).max_blobs_per_block</c>. The
    /// blob schedule lives in the node's <see cref="BeaconChainSpec"/> configuration (e.g.
    /// <see cref="BeaconChainSpec.GetBlobParameters"/>), which the state transition does not own,
    /// so the caller resolves the limit for the state's epoch.
    /// </param>
    public static void ProcessExecutionPayload(BeaconStateFulu state, BeaconBlockBody body, INewPayloadNotifier notifier, ulong maxBlobsPerBlock)
    {
        ExecutionPayload payload = body.ExecutionPayload!;
        if (payload.ParentHash != state.LatestExecutionPayloadHeader!.BlockHash)
            throw new BeaconStateException($"Payload parent hash {payload.ParentHash} does not match latest payload header block hash");
        if (payload.PrevRandao != state.GetRandaoMix(state.GetCurrentEpoch()))
            throw new BeaconStateException("Payload prev_randao does not match the current randao mix");
        if (payload.Timestamp != ComputeTimeAtSlot(state, state.Slot))
            throw new BeaconStateException($"Payload timestamp {payload.Timestamp} does not match slot {state.Slot}");
        if ((ulong)(body.BlobKzgCommitments?.Length ?? 0) > maxBlobsPerBlock)
            throw new BeaconStateException($"Blob commitment count {body.BlobKzgCommitments!.Length} exceeds limit {maxBlobsPerBlock}");

        if (!notifier.NotifyNewPayload(body))
            throw new BeaconStateException("Execution payload was rejected by the execution layer");

        Transaction.MerkleizeList(payload.Transactions ?? [], 1_048_576UL, out UInt256 transactionsRoot);
        Withdrawal.MerkleizeList(payload.Withdrawals ?? [], 16UL, out UInt256 withdrawalsRoot);
        state.LatestExecutionPayloadHeader = new ExecutionPayloadHeader
        {
            ParentHash = payload.ParentHash,
            FeeRecipient = payload.FeeRecipient,
            StateRoot = payload.StateRoot,
            ReceiptsRoot = payload.ReceiptsRoot,
            LogsBloom = payload.LogsBloom,
            PrevRandao = payload.PrevRandao,
            BlockNumber = payload.BlockNumber,
            GasLimit = payload.GasLimit,
            GasUsed = payload.GasUsed,
            Timestamp = payload.Timestamp,
            ExtraData = payload.ExtraData,
            BaseFeePerGas = payload.BaseFeePerGas,
            BlockHash = payload.BlockHash,
            TransactionsRoot = new Hash256(transactionsRoot.ToLittleEndian()),
            WithdrawalsRoot = new Hash256(withdrawalsRoot.ToLittleEndian()),
            BlobGasUsed = payload.BlobGasUsed,
            ExcessBlobGas = payload.ExcessBlobGas,
        };
    }

    private static ulong ComputeTimeAtSlot(BeaconStateFulu state, ulong slot) =>
        state.GenesisTime + (slot - Presets.GenesisSlot) * Presets.SecondsPerSlot;

    /// <summary>Spec <c>process_randao</c>: verifies the proposer's reveal and mixes it into the current randao mix.</summary>
    public static void ProcessRandao(BeaconStateFulu state, BeaconBlockBody body, PubkeyCache pubkeys, bool verifySignature = true)
    {
        ulong epoch = state.GetCurrentEpoch();
        if (verifySignature && !SignatureSets.VerifyRandaoReveal(state, (int)state.GetBeaconProposerIndex(), epoch, body.RandaoReveal, pubkeys))
            throw new BeaconStateException("Invalid RANDAO reveal");

        Span<byte> mix = stackalloc byte[32];
        SHA256.HashData(body.RandaoReveal.Bytes, mix);
        ReadOnlySpan<byte> currentMix = state.GetRandaoMix(epoch).Bytes;
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] ^= currentMix[i];
        }
        state.RandaoMixes![(int)(epoch % Presets.EpochsPerHistoricalVector)] = new Hash256(mix);
    }

    /// <summary>Spec <c>process_eth1_data</c>: records the vote and adopts it once it has a majority of the voting period.</summary>
    public static void ProcessEth1Data(BeaconStateFulu state, BeaconBlockBody body)
    {
        state.Eth1DataVotes = [.. state.Eth1DataVotes!, body.Eth1Data!];
        int votes = 0;
        foreach (Eth1Data vote in state.Eth1DataVotes)
        {
            if (Eth1DataEquals(vote, body.Eth1Data!))
                votes++;
        }
        if ((ulong)votes * 2 > Presets.EpochsPerEth1VotingPeriod * Presets.SlotsPerEpoch)
            state.Eth1Data = body.Eth1Data;
    }

    private static bool Eth1DataEquals(Eth1Data a, Eth1Data b) =>
        a.DepositRoot == b.DepositRoot && a.DepositCount == b.DepositCount && a.BlockHash == b.BlockHash;

    /// <summary>
    /// Spec <c>process_operations</c> (Electra): checks the expected Eth1 deposit count, then
    /// dispatches every operation list in spec order.
    /// </summary>
    public static void ProcessOperations(BeaconStateFulu state, BeaconBlockBody body, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures = true)
    {
        // [Modified in Electra:EIP6110] The former deposit mechanism is disabled once all
        // pre-request deposits are processed.
        ulong eth1DepositIndexLimit = Math.Min(state.Eth1Data!.DepositCount, state.DepositRequestsStartIndex);
        ulong expectedDeposits = state.Eth1DepositIndex < eth1DepositIndexLimit
            ? Math.Min(Presets.MaxDeposits, eth1DepositIndexLimit - state.Eth1DepositIndex)
            : 0;
        if ((ulong)(body.Deposits?.Length ?? 0) != expectedDeposits)
            throw new BeaconStateException($"Block has {body.Deposits?.Length ?? 0} deposits, expected {expectedDeposits}");

        foreach (ProposerSlashing slashing in body.ProposerSlashings ?? [])
        {
            ProcessProposerSlashing(state, slashing, cache, pubkeys, verifySignatures);
        }
        foreach (AttesterSlashing slashing in body.AttesterSlashings ?? [])
        {
            ProcessAttesterSlashing(state, slashing, cache, pubkeys, verifySignatures);
        }
        foreach (Attestation attestation in body.Attestations ?? [])
        {
            ProcessAttestation(state, attestation, cache, pubkeys, verifySignatures);
        }
        foreach (Deposit deposit in body.Deposits ?? [])
        {
            ProcessDeposit(state, deposit);
        }
        foreach (SignedVoluntaryExit exit in body.VoluntaryExits ?? [])
        {
            ProcessVoluntaryExit(state, exit, cache, pubkeys, verifySignatures);
        }
        foreach (SignedBlsToExecutionChange change in body.BlsToExecutionChanges ?? [])
        {
            ProcessBlsToExecutionChange(state, change, verifySignatures);
        }
        ExecutionRequests requests = body.ExecutionRequests!;
        foreach (DepositRequest request in requests.Deposits ?? [])
        {
            ProcessDepositRequest(state, request);
        }
        foreach (WithdrawalRequest request in requests.Withdrawals ?? [])
        {
            ProcessWithdrawalRequest(state, request, cache);
        }
        foreach (ConsolidationRequest request in requests.Consolidations ?? [])
        {
            ProcessConsolidationRequest(state, request, cache);
        }
    }

    /// <summary>Spec <c>process_proposer_slashing</c>.</summary>
    public static void ProcessProposerSlashing(BeaconStateFulu state, ProposerSlashing slashing, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures = true)
    {
        BeaconBlockHeader header1 = slashing.SignedHeader1!.Message!;
        BeaconBlockHeader header2 = slashing.SignedHeader2!.Message!;

        if (header1.Slot != header2.Slot)
            throw new BeaconStateException("Proposer slashing header slots do not match");
        if (header1.ProposerIndex != header2.ProposerIndex)
            throw new BeaconStateException("Proposer slashing proposer indices do not match");
        if (HeaderEquals(header1, header2))
            throw new BeaconStateException("Proposer slashing headers are identical");
        if (header1.ProposerIndex >= (ulong)state.Validators!.Length)
            throw new BeaconStateException($"Proposer slashing index {header1.ProposerIndex} is out of range");
        if (!state.Validators[(int)header1.ProposerIndex].IsSlashableValidator(state.GetCurrentEpoch()))
            throw new BeaconStateException($"Proposer {header1.ProposerIndex} is not slashable");

        if (verifySignatures)
        {
            if (!SignatureSets.VerifySignedBeaconBlockHeader(state, slashing.SignedHeader1, pubkeys))
                throw new BeaconStateException("Invalid proposer slashing signature 1");
            if (!SignatureSets.VerifySignedBeaconBlockHeader(state, slashing.SignedHeader2, pubkeys))
                throw new BeaconStateException("Invalid proposer slashing signature 2");
        }

        state.SlashValidator((int)header1.ProposerIndex, cache);
    }

    private static bool HeaderEquals(BeaconBlockHeader a, BeaconBlockHeader b) =>
        a.Slot == b.Slot
        && a.ProposerIndex == b.ProposerIndex
        && a.ParentRoot == b.ParentRoot
        && a.StateRoot == b.StateRoot
        && a.BodyRoot == b.BodyRoot;

    /// <summary>Spec <c>process_attester_slashing</c>: slashes every still-slashable validator attesting in both votes.</summary>
    public static void ProcessAttesterSlashing(BeaconStateFulu state, AttesterSlashing slashing, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures = true)
    {
        IndexedAttestation attestation1 = slashing.Attestation1!;
        IndexedAttestation attestation2 = slashing.Attestation2!;

        if (!BeaconStateAccessors.IsSlashableAttestationData(attestation1.Data!, attestation2.Data!))
            throw new BeaconStateException("Attester slashing votes are not slashable");
        if (!IsValidIndexedAttestation(state, attestation1, pubkeys, verifySignatures))
            throw new BeaconStateException("Attester slashing attestation 1 is invalid");
        if (!IsValidIndexedAttestation(state, attestation2, pubkeys, verifySignatures))
            throw new BeaconStateException("Attester slashing attestation 2 is invalid");

        ulong currentEpoch = state.GetCurrentEpoch();
        HashSet<ulong> indices2 = [.. attestation2.AttestingIndices!];
        bool slashedAny = false;
        // attestation_1's indices are validated ascending, so the intersection is visited in sorted order.
        foreach (ulong index in attestation1.AttestingIndices!)
        {
            if (indices2.Contains(index) && state.Validators![(int)index].IsSlashableValidator(currentEpoch))
            {
                state.SlashValidator((int)index, cache);
                slashedAny = true;
            }
        }
        if (!slashedAny)
            throw new BeaconStateException("Attester slashing slashed no validator");
    }

    /// <summary>
    /// Spec <c>is_valid_indexed_attestation</c>: indices must be non-empty, sorted, unique, and in
    /// range, and the aggregate signature must verify.
    /// </summary>
    private static bool IsValidIndexedAttestation(BeaconStateFulu state, IndexedAttestation attestation, PubkeyCache pubkeys, bool verifySignature)
    {
        ulong[] indices = attestation.AttestingIndices ?? [];
        if (indices.Length == 0)
            return false;
        for (int i = 0; i < indices.Length; i++)
        {
            if (i > 0 && indices[i - 1] >= indices[i])
                return false;
            if (indices[i] >= (ulong)state.Validators!.Length)
                return false;
        }
        return !verifySignature || SignatureSets.VerifyIndexedAttestation(state, attestation, pubkeys);
    }

    /// <summary>
    /// Spec <c>process_attestation</c> (Electra): validates the EIP-7549 aggregate, sets
    /// participation flags, and credits the proposer reward.
    /// </summary>
    public static void ProcessAttestation(BeaconStateFulu state, Attestation attestation, EpochCache cache, PubkeyCache pubkeys, bool verifySignature = true)
    {
        AttestationData data = attestation.Data!;
        ulong currentEpoch = state.GetCurrentEpoch();
        if (data.Target!.Epoch != state.GetPreviousEpoch() && data.Target.Epoch != currentEpoch)
            throw new BeaconStateException($"Attestation target epoch {data.Target.Epoch} is not the previous or current epoch");
        if (data.Target.Epoch != BeaconStateAccessors.ComputeEpochAtSlot(data.Slot))
            throw new BeaconStateException("Attestation target epoch does not match its slot");
        if (data.Slot + Presets.MinAttestationInclusionDelay > state.Slot)
            throw new BeaconStateException($"Attestation for slot {data.Slot} is included too early at slot {state.Slot}");
        // [Modified in Electra:EIP7549] The committee is selected by committee_bits.
        if (data.Index != 0)
            throw new BeaconStateException($"Attestation data index {data.Index} must be zero");

        // GetAttestingIndices performs the spec's committee/aggregation-bits structural asserts.
        CommitteeCache committees = cache.GetCommitteeCache(state, data.Target.Epoch);
        ulong[] attestingIndices = state.GetAttestingIndices(attestation, committees);

        byte participationFlags = GetAttestationParticipationFlagIndices(state, data, state.Slot - data.Slot);

        IndexedAttestation indexed = new()
        {
            AttestingIndices = attestingIndices,
            Data = data,
            Signature = attestation.Signature,
        };
        if (!IsValidIndexedAttestation(state, indexed, pubkeys, verifySignature))
            throw new BeaconStateException("Invalid indexed attestation");

        byte[] epochParticipation = data.Target.Epoch == currentEpoch
            ? state.CurrentEpochParticipation!
            : state.PreviousEpochParticipation!;

        ulong proposerRewardNumerator = 0;
        foreach (ulong index in attestingIndices)
        {
            for (int flagIndex = 0; flagIndex < Presets.ParticipationFlagWeights.Length; flagIndex++)
            {
                byte flag = (byte)(1 << flagIndex);
                if ((participationFlags & flag) != 0 && (epochParticipation[index] & flag) == 0)
                {
                    epochParticipation[index] |= flag;
                    proposerRewardNumerator += state.GetBaseReward((int)index, cache) * Presets.ParticipationFlagWeights[flagIndex];
                }
            }
        }

        ulong proposerRewardDenominator = (Presets.WeightDenominator - Presets.ProposerWeight) * Presets.WeightDenominator / Presets.ProposerWeight;
        state.IncreaseBalance((int)state.GetBeaconProposerIndex(), proposerRewardNumerator / proposerRewardDenominator);
    }

    /// <summary>
    /// Spec <c>get_attestation_participation_flag_indices</c> (Deneb/EIP-7045 timeliness rules),
    /// returned as a bitmask over the participation flag indices.
    /// </summary>
    /// <exception cref="BeaconStateException">The attestation source does not match the justified checkpoint.</exception>
    private static byte GetAttestationParticipationFlagIndices(BeaconStateFulu state, AttestationData data, ulong inclusionDelay)
    {
        Checkpoint justifiedCheckpoint = data.Target!.Epoch == state.GetCurrentEpoch()
            ? state.CurrentJustifiedCheckpoint!
            : state.PreviousJustifiedCheckpoint!;
        if (data.Source!.Epoch != justifiedCheckpoint.Epoch || data.Source.Root != justifiedCheckpoint.Root)
            throw new BeaconStateException("Attestation source does not match the justified checkpoint");

        bool isMatchingTarget = data.Target.Root == state.GetBlockRoot(data.Target.Epoch);
        bool isMatchingHead = isMatchingTarget && data.BeaconBlockRoot == state.GetBlockRootAtSlot(data.Slot);

        byte flags = 0;
        if (inclusionDelay <= BeaconStateAccessors.IntegerSquareRoot(Presets.SlotsPerEpoch))
            flags |= 1 << Presets.TimelySourceFlagIndex;
        if (isMatchingTarget)
            flags |= 1 << Presets.TimelyTargetFlagIndex;
        if (isMatchingHead && inclusionDelay == Presets.MinAttestationInclusionDelay)
            flags |= 1 << Presets.TimelyHeadFlagIndex;
        return flags;
    }

    /// <summary>Spec <c>process_deposit</c>: verifies the Eth1 deposit tree Merkle branch, then applies the deposit.</summary>
    public static void ProcessDeposit(BeaconStateFulu state, Deposit deposit)
    {
        DepositData data = deposit.Data!;
        // The +1 accounts for the SSZ list-length mix-in of the deposit tree.
        if (!IsValidMerkleBranch(SszRoots.HashTreeRoot(data), deposit.Proof!, Presets.DepositContractTreeDepth + 1, state.Eth1DepositIndex, state.Eth1Data!.DepositRoot!))
            throw new BeaconStateException($"Invalid Merkle proof for deposit {state.Eth1DepositIndex}");

        // Deposits must be processed in order.
        state.Eth1DepositIndex++;

        ApplyDeposit(state, data.Pubkey, data.WithdrawalCredentials!, data.Amount, data.Signature);
    }

    /// <summary>Spec <c>is_valid_merkle_branch</c>.</summary>
    private static bool IsValidMerkleBranch(Hash256 leaf, Hash256[] branch, int depth, ulong index, Hash256 root)
    {
        Span<byte> preimage = stackalloc byte[64];
        Span<byte> value = stackalloc byte[32];
        leaf.Bytes.CopyTo(value);
        for (int i = 0; i < depth; i++)
        {
            if ((index >> i & 1) == 1)
            {
                branch[i].Bytes.CopyTo(preimage);
                value.CopyTo(preimage[32..]);
            }
            else
            {
                value.CopyTo(preimage);
                branch[i].Bytes.CopyTo(preimage[32..]);
            }
            SHA256.HashData(preimage, value);
        }
        return value.SequenceEqual(root.Bytes);
    }

    /// <summary>
    /// Spec <c>apply_deposit</c> (Electra): registers a new validator when the proof of possession
    /// is valid (an invalid signature silently skips the deposit), and queues the amount as a
    /// pending deposit.
    /// </summary>
    private static void ApplyDeposit(BeaconStateFulu state, BlsPublicKey pubkey, Hash256 withdrawalCredentials, ulong amount, BlsSignature signature)
    {
        if (FindValidatorIndex(state, pubkey) is null)
        {
            if (!DepositSignatureVerifier.IsValid(pubkey, withdrawalCredentials, amount, signature))
                return;
            state.AddValidatorToRegistry(pubkey, withdrawalCredentials, 0);
        }

        state.PendingDeposits = [.. state.PendingDeposits!, new PendingDeposit
        {
            Pubkey = pubkey,
            WithdrawalCredentials = withdrawalCredentials,
            Amount = amount,
            Signature = signature,
            // GENESIS_SLOT distinguishes Eth1-path deposits from pending deposit requests.
            Slot = Presets.GenesisSlot,
        }];
    }

    /// <summary>Spec <c>process_voluntary_exit</c> (Electra).</summary>
    public static void ProcessVoluntaryExit(BeaconStateFulu state, SignedVoluntaryExit signedExit, EpochCache cache, PubkeyCache pubkeys, bool verifySignature = true)
    {
        VoluntaryExit exit = signedExit.Message!;
        if (exit.ValidatorIndex >= (ulong)state.Validators!.Length)
            throw new BeaconStateException($"Voluntary exit validator index {exit.ValidatorIndex} is out of range");

        Validator validator = state.Validators[(int)exit.ValidatorIndex];
        ulong currentEpoch = state.GetCurrentEpoch();
        if (!validator.IsActiveValidator(currentEpoch))
            throw new BeaconStateException($"Exiting validator {exit.ValidatorIndex} is not active");
        if (validator.ExitEpoch != Presets.FarFutureEpoch)
            throw new BeaconStateException($"Validator {exit.ValidatorIndex} already initiated an exit");
        if (currentEpoch < exit.Epoch)
            throw new BeaconStateException($"Voluntary exit is not valid before epoch {exit.Epoch}");
        if (currentEpoch < validator.ActivationEpoch + Presets.ShardCommitteePeriod)
            throw new BeaconStateException($"Validator {exit.ValidatorIndex} has not been active long enough");
        // [New in Electra:EIP7251] Only exit when no withdrawals are pending in the queue.
        if (state.GetPendingBalanceToWithdraw((int)exit.ValidatorIndex) != 0)
            throw new BeaconStateException($"Validator {exit.ValidatorIndex} has pending partial withdrawals");
        if (verifySignature && !SignatureSets.VerifyVoluntaryExit(state, signedExit, pubkeys))
            throw new BeaconStateException("Invalid voluntary exit signature");

        state.InitiateValidatorExit((int)exit.ValidatorIndex, cache);
    }

    /// <summary>Spec <c>process_bls_to_execution_change</c> (Capella).</summary>
    public static void ProcessBlsToExecutionChange(BeaconStateFulu state, SignedBlsToExecutionChange signedChange, bool verifySignature = true)
    {
        BlsToExecutionChange change = signedChange.Message!;
        if (change.ValidatorIndex >= (ulong)state.Validators!.Length)
            throw new BeaconStateException($"BLS change validator index {change.ValidatorIndex} is out of range");

        Validator validator = state.Validators[(int)change.ValidatorIndex];
        ReadOnlySpan<byte> credentials = validator.WithdrawalCredentials!.Bytes;
        if (credentials[0] != Presets.BlsWithdrawalPrefix)
            throw new BeaconStateException($"Validator {change.ValidatorIndex} does not have BLS withdrawal credentials");
        if (!credentials[1..].SequenceEqual(SHA256.HashData(change.FromBlsPubkey.Bytes).AsSpan(1)))
            throw new BeaconStateException("BLS change pubkey does not match the withdrawal credentials");
        if (verifySignature && !SignatureSets.VerifyBlsToExecutionChange(state, signedChange))
            throw new BeaconStateException("Invalid BLS to execution change signature");

        Span<byte> newCredentials = stackalloc byte[32];
        newCredentials[0] = Presets.EthWithdrawalPrefix;
        change.ToExecutionAddress!.Bytes.CopyTo(newCredentials[12..]);
        Validator updated = validator.Clone();
        updated.WithdrawalCredentials = new Hash256(newCredentials);
        state.Validators[(int)change.ValidatorIndex] = updated;
    }

    /// <summary>Spec <c>process_deposit_request</c> (EIP-6110).</summary>
    public static void ProcessDepositRequest(BeaconStateFulu state, DepositRequest request)
    {
        if (state.DepositRequestsStartIndex == Presets.UnsetDepositRequestsStartIndex)
            state.DepositRequestsStartIndex = request.Index;

        state.PendingDeposits = [.. state.PendingDeposits!, new PendingDeposit
        {
            Pubkey = request.Pubkey,
            WithdrawalCredentials = request.WithdrawalCredentials,
            Amount = request.Amount,
            Signature = request.Signature,
            Slot = state.Slot,
        }];
    }

    /// <summary>
    /// Spec <c>process_withdrawal_request</c> (EIP-7002/EIP-7251). Invalid requests are ignored,
    /// never invalidating the block.
    /// </summary>
    public static void ProcessWithdrawalRequest(BeaconStateFulu state, WithdrawalRequest request, EpochCache cache)
    {
        bool isFullExitRequest = request.Amount == Presets.FullExitRequestAmount;

        // When the partial withdrawal queue is full, only full exits are processed.
        if (state.PendingPartialWithdrawals!.Length == Presets.PendingPartialWithdrawalsLimit && !isFullExitRequest)
            return;

        if (FindValidatorIndex(state, request.ValidatorPubkey) is not int index)
            return;
        Validator validator = state.Validators![index];

        bool isCorrectSourceAddress = validator.WithdrawalCredentials!.Bytes[12..].SequenceEqual(request.SourceAddress!.Bytes);
        if (!validator.HasExecutionWithdrawalCredential() || !isCorrectSourceAddress)
            return;
        ulong currentEpoch = state.GetCurrentEpoch();
        if (!validator.IsActiveValidator(currentEpoch))
            return;
        if (validator.ExitEpoch != Presets.FarFutureEpoch)
            return;
        if (currentEpoch < validator.ActivationEpoch + Presets.ShardCommitteePeriod)
            return;

        ulong pendingBalanceToWithdraw = state.GetPendingBalanceToWithdraw(index);

        if (isFullExitRequest)
        {
            // Only exit the validator when it has no withdrawals pending in the queue.
            if (pendingBalanceToWithdraw == 0)
                state.InitiateValidatorExit(index, cache);
            return;
        }

        bool hasSufficientEffectiveBalance = validator.EffectiveBalance >= Presets.MinActivationBalance;
        bool hasExcessBalance = state.Balances![index] > Presets.MinActivationBalance + pendingBalanceToWithdraw;

        // Only compounding credentials allow partial withdrawals.
        if (validator.HasCompoundingWithdrawalCredential() && hasSufficientEffectiveBalance && hasExcessBalance)
        {
            ulong toWithdraw = Math.Min(state.Balances[index] - Presets.MinActivationBalance - pendingBalanceToWithdraw, request.Amount);
            ulong exitQueueEpoch = state.ComputeExitEpochAndUpdateChurn(toWithdraw, cache);
            state.PendingPartialWithdrawals = [.. state.PendingPartialWithdrawals, new PendingPartialWithdrawal
            {
                ValidatorIndex = (ulong)index,
                Amount = toWithdraw,
                WithdrawableEpoch = exitQueueEpoch + Presets.MinValidatorWithdrawabilityDelay,
            }];
        }
    }

    /// <summary>
    /// Spec <c>process_consolidation_request</c> (EIP-7251): a self-consolidation switches the
    /// validator to compounding credentials; otherwise the source's balance is consolidated into
    /// the target through the consolidation churn. Invalid requests are ignored.
    /// </summary>
    public static void ProcessConsolidationRequest(BeaconStateFulu state, ConsolidationRequest request, EpochCache cache)
    {
        if (IsValidSwitchToCompoundingRequest(state, request))
        {
            SwitchToCompoundingValidator(state, FindValidatorIndex(state, request.SourcePubkey)!.Value);
            return;
        }

        // A consolidation with source == target cannot be used as an exit.
        if (request.SourcePubkey == request.TargetPubkey)
            return;
        if (state.PendingConsolidations!.Length == Presets.PendingConsolidationsLimit)
            return;
        if (state.GetConsolidationChurnLimit(cache) <= Presets.MinActivationBalance)
            return;

        if (FindValidatorIndex(state, request.SourcePubkey) is not int sourceIndex)
            return;
        if (FindValidatorIndex(state, request.TargetPubkey) is not int targetIndex)
            return;
        Validator sourceValidator = state.Validators![sourceIndex];
        Validator targetValidator = state.Validators[targetIndex];

        bool isCorrectSourceAddress = sourceValidator.WithdrawalCredentials!.Bytes[12..].SequenceEqual(request.SourceAddress!.Bytes);
        if (!sourceValidator.HasExecutionWithdrawalCredential() || !isCorrectSourceAddress)
            return;
        if (!targetValidator.HasCompoundingWithdrawalCredential())
            return;
        ulong currentEpoch = state.GetCurrentEpoch();
        if (!sourceValidator.IsActiveValidator(currentEpoch) || !targetValidator.IsActiveValidator(currentEpoch))
            return;
        if (sourceValidator.ExitEpoch != Presets.FarFutureEpoch || targetValidator.ExitEpoch != Presets.FarFutureEpoch)
            return;
        if (currentEpoch < sourceValidator.ActivationEpoch + Presets.ShardCommitteePeriod)
            return;
        if (state.GetPendingBalanceToWithdraw(sourceIndex) > 0)
            return;

        Validator updatedSource = sourceValidator.Clone();
        updatedSource.ExitEpoch = state.ComputeConsolidationEpochAndUpdateChurn(sourceValidator.EffectiveBalance, cache);
        updatedSource.WithdrawableEpoch = updatedSource.ExitEpoch + Presets.MinValidatorWithdrawabilityDelay;
        state.Validators[sourceIndex] = updatedSource;

        state.PendingConsolidations = [.. state.PendingConsolidations, new PendingConsolidation
        {
            SourceIndex = (ulong)sourceIndex,
            TargetIndex = (ulong)targetIndex,
        }];
    }

    /// <summary>Spec <c>is_valid_switch_to_compounding_request</c>.</summary>
    private static bool IsValidSwitchToCompoundingRequest(BeaconStateFulu state, ConsolidationRequest request)
    {
        // Switching to compounding requires source and target to be the same validator.
        if (request.SourcePubkey != request.TargetPubkey)
            return false;
        if (FindValidatorIndex(state, request.SourcePubkey) is not int sourceIndex)
            return false;

        Validator sourceValidator = state.Validators![sourceIndex];
        if (!sourceValidator.WithdrawalCredentials!.Bytes[12..].SequenceEqual(request.SourceAddress!.Bytes))
            return false;
        if (!sourceValidator.HasEth1WithdrawalCredential())
            return false;
        if (!sourceValidator.IsActiveValidator(state.GetCurrentEpoch()))
            return false;
        if (sourceValidator.ExitEpoch != Presets.FarFutureEpoch)
            return false;

        return true;
    }

    /// <summary>Spec <c>switch_to_compounding_validator</c>.</summary>
    private static void SwitchToCompoundingValidator(BeaconStateFulu state, int index)
    {
        Span<byte> credentials = stackalloc byte[32];
        Validator validator = state.Validators![index].Clone();
        validator.WithdrawalCredentials!.Bytes.CopyTo(credentials);
        credentials[0] = Presets.CompoundingWithdrawalPrefix;
        validator.WithdrawalCredentials = new Hash256(credentials);
        state.Validators[index] = validator;
        QueueExcessActiveBalance(state, index);
    }

    /// <summary>Spec <c>queue_excess_active_balance</c>.</summary>
    private static void QueueExcessActiveBalance(BeaconStateFulu state, int index)
    {
        ulong balance = state.Balances![index];
        if (balance <= Presets.MinActivationBalance)
            return;

        state.Balances[index] = Presets.MinActivationBalance;
        Validator validator = state.Validators![index];
        state.PendingDeposits = [.. state.PendingDeposits!, new PendingDeposit
        {
            Pubkey = validator.Pubkey,
            WithdrawalCredentials = validator.WithdrawalCredentials,
            Amount = balance - Presets.MinActivationBalance,
            // The G2 point at infinity is the signature placeholder, and GENESIS_SLOT
            // distinguishes this from a pending deposit request.
            Signature = new BlsSignature(SignatureSets.G2PointAtInfinity),
            Slot = Presets.GenesisSlot,
        }];
    }

    /// <summary>Spec <c>process_sync_aggregate</c> (Altair): verifies the aggregate and applies participant, proposer, and non-participant balance changes.</summary>
    public static void ProcessSyncAggregate(BeaconStateFulu state, SyncAggregate syncAggregate, EpochCache cache, bool verifySignature = true)
    {
        if (verifySignature && !SignatureSets.VerifySyncAggregate(state, syncAggregate))
            throw new BeaconStateException("Invalid sync aggregate signature");

        ulong totalActiveIncrements = state.GetTotalActiveBalance(cache) / Presets.EffectiveBalanceIncrement;
        ulong totalBaseRewards = state.GetBaseRewardPerIncrement(cache) * totalActiveIncrements;
        ulong maxParticipantRewards = totalBaseRewards * Presets.SyncRewardWeight / Presets.WeightDenominator / Presets.SlotsPerEpoch;
        ulong participantReward = maxParticipantRewards / Presets.SyncCommitteeSize;
        ulong proposerReward = participantReward * Presets.ProposerWeight / (Presets.WeightDenominator - Presets.ProposerWeight);

        // Map the committee's (possibly repeated) pubkeys back to validator indices.
        Dictionary<BlsPublicKey, int> indexByPubkey = new(state.Validators!.Length);
        for (int i = 0; i < state.Validators.Length; i++)
        {
            indexByPubkey.TryAdd(state.Validators[i].Pubkey, i);
        }

        int proposerIndex = (int)state.GetBeaconProposerIndex();
        BlsPublicKey[] committee = state.CurrentSyncCommittee!.Pubkeys!;
        BitArray bits = syncAggregate.SyncCommitteeBits!;
        for (int i = 0; i < committee.Length; i++)
        {
            int participantIndex = indexByPubkey[committee[i]];
            if (bits[i])
            {
                state.IncreaseBalance(participantIndex, participantReward);
                state.IncreaseBalance(proposerIndex, proposerReward);
            }
            else
            {
                state.DecreaseBalance(participantIndex, participantReward);
            }
        }
    }

    /// <summary>Returns the index of the validator with <paramref name="pubkey"/>, or null when unregistered.</summary>
    private static int? FindValidatorIndex(BeaconStateFulu state, BlsPublicKey pubkey)
    {
        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            if (validators[i].Pubkey == pubkey)
                return i;
        }
        return null;
    }
}
