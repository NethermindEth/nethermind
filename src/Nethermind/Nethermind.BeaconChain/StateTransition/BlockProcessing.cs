// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
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
    /// <summary>Returns the execution layer's verdict on the body's payload (with its versioned hashes and execution requests).</summary>
    /// <remarks>
    /// <see cref="ExecutionStatus.Optimistic"/> means the payload was not rejected and was not
    /// validated either; the caller must carry that distinction into fork choice rather than
    /// collapsing it into acceptance.
    /// </remarks>
    ExecutionStatus NotifyNewPayload(BeaconBlockBody body);

    /// <summary>
    /// Gloas <c>ExecutionEngine.verify_and_notify_new_payload</c> for an execution payload envelope
    /// (spec <c>verify_execution_payload_envelope</c>, EIP-7732). The plugin's engine adapter
    /// implements it for real; the default exists only so pre-Gloas test doubles keep compiling,
    /// and <see cref="RequireEnvelopeSupport"/> turns a production notifier still relying on it into
    /// a startup failure instead of a throw at the first Gloas envelope.
    /// </summary>
    ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests) =>
        throw new NotSupportedException($"{GetType().Name} does not implement execution-layer notification for Gloas execution payload envelopes");

    /// <summary>
    /// Refuses a notifier whose runtime type still uses the interface's throwing default for the
    /// envelope overload. Run once at startup against the production notifier: a node that passes
    /// this can not discover at Gloas activation that it never had an engine path for envelopes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The notifier does not implement the envelope overload.</exception>
    static void RequireEnvelopeSupport(INewPayloadNotifier notifier)
    {
        Type type = notifier.GetType();
        InterfaceMapping map = type.GetInterfaceMap(typeof(INewPayloadNotifier));
        for (int i = 0; i < map.InterfaceMethods.Length; i++)
        {
            MethodInfo declared = map.InterfaceMethods[i];
            if (declared.Name != nameof(NotifyNewPayload) || declared.GetParameters()[0].ParameterType != typeof(ExecutionPayloadGloas))
                continue;
            if (map.TargetMethods[i].DeclaringType == typeof(INewPayloadNotifier))
                throw new InvalidOperationException(
                    $"{type.FullName} does not implement execution-layer notification for Gloas execution payload envelopes; " +
                    "a Gloas node cannot follow the chain without one");
            return;
        }
        throw new InvalidOperationException($"{nameof(INewPayloadNotifier)} envelope overload not found in the interface map of {type.FullName}");
    }
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
public static partial class BlockProcessing
{
    /// <summary>
    /// Spec <c>process_block</c>. The outer block (proposer) signature is not part of
    /// <c>process_block</c>; verify it separately with
    /// <see cref="SignatureSets.VerifyProposerSignature(BeaconStateFulu, SignedBeaconBlock, PubkeyCache)"/>.
    /// </summary>
    /// <param name="maxBlobsPerBlock">The blob limit for the state's epoch; see <see cref="ProcessExecutionPayload"/>.</param>
    /// <remarks>The operation signatures are verified as one <see cref="BlockSignatureBatch"/>, with the serial verdicts and messages.</remarks>
    public static void ProcessBlock(BeaconStateFulu state, BeaconBlock block, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, ulong maxBlobsPerBlock, bool verifySignatures = true)
    {
        if (verifySignatures)
            BlockSignatureBatch.Run(batch => ProcessBlock(state, block, cache, pubkeys, notifier, maxBlobsPerBlock, verifySignatures: true, batch));
        else
            ProcessBlock(state, block, cache, pubkeys, notifier, maxBlobsPerBlock, verifySignatures: false, batch: null);
    }

    /// <summary>Spec <c>process_block</c>, deferring the signatures to <paramref name="batch"/> or, when it is <c>null</c>, verifying each at its own step.</summary>
    internal static void ProcessBlock(BeaconStateFulu state, BeaconBlock block, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, ulong maxBlobsPerBlock, bool verifySignatures, BlockSignatureBatch? batch)
    {
        BeaconBlockBody body = block.Body!;
        ProcessBlockHeader(state, block);
        ProcessWithdrawals(state, body.ExecutionPayload!);
        ProcessExecutionPayload(state, body, notifier, maxBlobsPerBlock);
        ProcessRandao(state, body, pubkeys, verifySignatures, batch);
        ProcessEth1Data(state, body);
        ProcessOperations(state, body, cache, pubkeys, verifySignatures, batch);
        ProcessSyncAggregate(state, body.SyncAggregate!, cache, pubkeys, verifySignatures, batch);
    }

    /// <summary>Spec <c>process_block_header</c>: validates the block against the chain tip and caches it as the latest header.</summary>
    public static partial void ProcessBlockHeader(BeaconStateFulu state, BeaconBlock block);

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

    private static partial ulong TotalWithdrawn(List<Withdrawal> withdrawals, ulong validatorIndex);

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

        // Irrelevant is default(ExecutionStatus): a notifier that returns no verdict must not admit the block.
        ExecutionStatus executionStatus = notifier.NotifyNewPayload(body);
        if (executionStatus is ExecutionStatus.Invalid or ExecutionStatus.Irrelevant)
            throw new BeaconStateException($"Execution payload was rejected by the execution layer ({executionStatus})");

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

    public static partial void ProcessRandao(BeaconStateFulu state, BeaconBlockBody body, PubkeyCache pubkeys, bool verifySignature = true, BlockSignatureBatch? batch = null);

    /// <summary>Spec <c>process_eth1_data</c>: records the vote and adopts it once it has a majority of the voting period.</summary>
    public static partial void ProcessEth1Data(BeaconStateFulu state, BeaconBlockBody body);

    private static bool Eth1DataEquals(Eth1Data a, Eth1Data b) =>
        a.DepositRoot == b.DepositRoot && a.DepositCount == b.DepositCount && a.BlockHash == b.BlockHash;

    /// <summary>
    /// Spec <c>process_operations</c> (Electra): checks the expected Eth1 deposit count, then
    /// dispatches every operation list in spec order.
    /// </summary>
    public static void ProcessOperations(BeaconStateFulu state, BeaconBlockBody body, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures = true, BlockSignatureBatch? batch = null)
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
            ProcessProposerSlashing(state, slashing, cache, pubkeys, verifySignatures, batch);
        }
        foreach (AttesterSlashing slashing in body.AttesterSlashings ?? [])
        {
            ProcessAttesterSlashing(state, slashing, cache, pubkeys, verifySignatures, batch);
        }
        foreach (Attestation attestation in body.Attestations ?? [])
        {
            ProcessAttestation(state, attestation, cache, pubkeys, verifySignatures, batch);
        }
        foreach (Deposit deposit in body.Deposits ?? [])
        {
            ProcessDeposit(state, deposit);
        }
        foreach (SignedVoluntaryExit exit in body.VoluntaryExits ?? [])
        {
            ProcessVoluntaryExit(state, exit, cache, pubkeys, verifySignatures, batch);
        }
        foreach (SignedBlsToExecutionChange change in body.BlsToExecutionChanges ?? [])
        {
            ProcessBlsToExecutionChange(state, change, verifySignatures, batch);
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

    public static partial void ProcessProposerSlashing(BeaconStateFulu state, ProposerSlashing slashing, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures = true, BlockSignatureBatch? batch = null);

    private static partial bool HeaderEquals(BeaconBlockHeader a, BeaconBlockHeader b);

    public static partial void ProcessAttesterSlashing(BeaconStateFulu state, AttesterSlashing slashing, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures = true, BlockSignatureBatch? batch = null);

    public static partial bool IsValidIndexedAttestation(BeaconStateFulu state, IndexedAttestation attestation, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch.Deferral? deferral = null);

    /// <summary>
    /// Spec <c>process_attestation</c> (Electra): validates the EIP-7549 aggregate, sets
    /// participation flags, and credits the proposer reward.
    /// </summary>
    public static partial void ProcessAttestation(BeaconStateFulu state, Attestation attestation, EpochCache cache, PubkeyCache pubkeys, bool verifySignature = true, BlockSignatureBatch? batch = null);

    /// <summary>
    /// Spec <c>get_attestation_participation_flag_indices</c> (Deneb/EIP-7045 timeliness rules),
    /// returned as a bitmask over the participation flag indices.
    /// </summary>
    /// <exception cref="BeaconStateException">The attestation source does not match the justified checkpoint.</exception>
    private static partial byte GetAttestationParticipationFlagIndices(BeaconStateFulu state, AttestationData data, ulong inclusionDelay);

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
            if (!DepositSignatureVerifier.IsValid(state.GenesisValidatorsRoot!, pubkey, withdrawalCredentials, amount, signature))
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

    public static partial void ProcessVoluntaryExit(BeaconStateFulu state, SignedVoluntaryExit signedExit, EpochCache cache, PubkeyCache pubkeys, bool verifySignature = true, BlockSignatureBatch? batch = null);

    public static partial void ProcessBlsToExecutionChange(BeaconStateFulu state, SignedBlsToExecutionChange signedChange, bool verifySignature = true, BlockSignatureBatch? batch = null);

    public static partial void ProcessDepositRequest(BeaconStateFulu state, DepositRequest request);

    public static partial void ProcessWithdrawalRequest(BeaconStateFulu state, WithdrawalRequest request, EpochCache cache);

    public static partial void ProcessConsolidationRequest(BeaconStateFulu state, ConsolidationRequest request, EpochCache cache);

    private static partial bool IsValidSwitchToCompoundingRequest(BeaconStateFulu state, ConsolidationRequest request);

    /// <summary>Spec <c>switch_to_compounding_validator</c>.</summary>
    private static partial void SwitchToCompoundingValidator(BeaconStateFulu state, int index);

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

    public static partial void ProcessSyncAggregate(BeaconStateFulu state, SyncAggregate syncAggregate, EpochCache cache, PubkeyCache pubkeys, bool verifySignature = true, BlockSignatureBatch? batch = null);

    /// <summary>Refuses sync committee bits that are not one bit per member, which the SSZ <c>Bitvector[SYNC_COMMITTEE_SIZE]</c> type guarantees on the wire.</summary>
    /// <exception cref="BeaconStateException">The bit count is not <c>SYNC_COMMITTEE_SIZE</c>.</exception>
    internal static void EnsureSyncCommitteeWidth(SyncAggregate syncAggregate)
    {
        int width = syncAggregate.SyncCommitteeBits!.Length;
        if (width != Presets.SyncCommitteeSize)
            throw new BeaconStateException($"Sync committee bits have {width} entries, expected {Presets.SyncCommitteeSize}");
    }

    private static partial int? FindValidatorIndex(BeaconStateFulu state, BlsPublicKey pubkey);
}
