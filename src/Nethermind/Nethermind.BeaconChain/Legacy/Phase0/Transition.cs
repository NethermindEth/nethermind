// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz.Merkleization;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;
using Body = Nethermind.BeaconChain.Legacy.Deneb.BeaconBlockBody;

namespace Nethermind.BeaconChain.Legacy.Phase0;

/// <summary>The Phase0 state transition over a <see cref="BeaconStateFulu"/> working state.</summary>
/// <remarks>
/// Follows consensus-specs v1.7.0-beta.4. Each later fork derives from the fork before it and overrides what its
/// specification modifies; the hierarchy is closed because these forks no longer change. A
/// <see cref="BeaconStateException"/> leaves the state partially mutated, and it must be discarded.
/// Use one instance per state lineage.
/// </remarks>
internal class Transition(BeaconChainSpec spec)
{
    /// <summary>Marks a lookahead slot without a proposer, so that only reading it fails.</summary>
    private const ulong NoProposer = ulong.MaxValue;

    private BeaconStateFulu? _exitQueueState;
    private ulong _lastExitEpoch;
    private ulong _lastExitEpochCount;

    protected BeaconChainSpec Spec => spec;

    public virtual LegacyFork Fork => LegacyFork.Phase0;
    protected virtual ulong MinSlashingPenaltyQuotient => LegacyPresets.MinSlashingPenaltyQuotient;
    protected virtual ulong ProportionalSlashingMultiplier => LegacyPresets.ProportionalSlashingMultiplier;

    public virtual LegacyBlock DecodeBlock(ReadOnlySpan<byte> ssz)
    {
        SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock block);
        return new LegacyBlock(block, SszRoots.HashTreeRoot(block.Message!.Body!));
    }

    /// <summary>Decodes a state of this fork into a working state.</summary>
    public BeaconStateFulu DecodeState(ReadOnlySpan<byte> ssz)
    {
        BeaconStateFulu state = DecodeStateContainer(ssz);
        state.LatestExecutionPayloadHeader ??= new ExecutionPayloadHeader { ExtraData = [] };
        RefillProposers(state);
        return state;
    }

    protected virtual BeaconStateFulu DecodeStateContainer(ReadOnlySpan<byte> ssz)
    {
        BeaconState.Decode(ssz, out BeaconState state);
        return state;
    }

    /// <summary>Upgrades a state of the previous fork in place, at the first slot of this fork's activation epoch.</summary>
    public void Upgrade(BeaconStateFulu state, EpochCache cache)
    {
        state.Fork = new Fork
        {
            PreviousVersion = state.Fork!.CurrentVersion,
            CurrentVersion = spec.Forks[(int)Fork].Version,
            Epoch = state.GetCurrentEpoch(),
        };
        UpgradeFields(state, cache);
        RefillProposers(state);
    }

    protected virtual void UpgradeFields(BeaconStateFulu state, EpochCache cache) =>
        throw new NotSupportedException("Phase0 is the genesis fork");

    /// <summary>Spec <c>process_slots</c> within this fork.</summary>
    public void ProcessSlots(BeaconStateFulu state, ulong targetSlot, EpochCache cache)
    {
        if (state.Slot >= targetSlot)
            throw new BeaconStateException($"Cannot advance state at slot {state.Slot} to non-future slot {targetSlot}");

        while (state.Slot < targetSlot)
        {
            SlotProcessing.ProcessSlot(state, cache.Hasher);
            bool endsEpoch = (state.Slot + 1) % Presets.SlotsPerEpoch == 0;
            if (endsEpoch)
                ProcessEpoch(state, cache);
            state.Slot++;
            if (endsEpoch)
                RefillProposers(state);
        }
    }

    /// <summary>Applies a block to a state already advanced to its slot, and checks the block's state root.</summary>
    /// <exception cref="BeaconStateException">The block is invalid.</exception>
    public void Apply(BeaconStateFulu state, LegacyBlock block, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, bool verifySignatures)
    {
        cache.SigningSpec = spec;
        if (verifySignatures)
        {
            if (state.ReadOnlyValidators.Length > pubkeys.Count)
                pubkeys.Extend(state.ValidatorBuffer!.Items, pubkeys.Count);
            BlockSignatureBatch.Run(batch =>
            {
                const string invalidSignature = "Invalid proposer signature";
                if (!SignatureSets.VerifySignedBeaconBlockHeader(state, block.ToHeader(), pubkeys, batch.Defer(invalidSignature)))
                    throw new BeaconStateException(invalidSignature);
                ProcessBlock(state, block, cache, pubkeys, notifier, verifySignatures: true, batch);
            });
        }
        else
        {
            ProcessBlock(state, block, cache, pubkeys, notifier, verifySignatures: false, batch: null);
        }

        Hash256 root = cache.Hasher.HashTreeRoot(state);
        if (block.Message.StateRoot != root)
            throw new BeaconStateException($"Block state root {block.Message.StateRoot} does not match the post-state root {root} at slot {state.Slot}");
        (cache.Hasher as LegacyStateHasher)?.RecordPostBlockRoot(root);
    }

    public virtual void ProcessBlock(BeaconStateFulu state, LegacyBlock block, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, bool verifySignatures, BlockSignatureBatch? batch)
    {
        Body body = (Body)block.Message.Body!;
        ProcessBlockHeader(state, block);
        ProcessPayload(state, body, notifier);
        BlockProcessing.ProcessRandao(state, body, pubkeys, verifySignatures, batch);
        BlockProcessing.ProcessEth1Data(state, body);
        ProcessOperations(state, body, cache, pubkeys, verifySignatures, batch);
    }

    /// <summary>Spec <c>process_block_header</c>.</summary>
    public void ProcessBlockHeader(BeaconStateFulu state, LegacyBlock legacyBlock)
    {
        Types.BeaconBlock block = legacyBlock.Message;
        if (block.Slot != state.Slot)
            throw new BeaconStateException($"Block slot {block.Slot} does not match state slot {state.Slot}");
        if (block.Slot <= state.LatestBlockHeader!.Slot)
            throw new BeaconStateException($"Block slot {block.Slot} is not newer than latest header slot {state.LatestBlockHeader.Slot}");
        if (block.ProposerIndex != state.GetBeaconProposerIndex() || block.ProposerIndex == NoProposer)
            throw new BeaconStateException($"Block proposer {block.ProposerIndex} does not match expected proposer {state.GetBeaconProposerIndex()}");
        if (block.ParentRoot != SszRoots.HashTreeRoot(state.LatestBlockHeader))
            throw new BeaconStateException($"Block parent root {block.ParentRoot} does not match latest header root");

        BeaconBlockHeader header = legacyBlock.ToHeader().Message!;
        header.StateRoot = Hash256.Zero;
        state.LatestBlockHeader = header;

        if (state.ReadOnlyValidators[(int)block.ProposerIndex].Slashed)
            throw new BeaconStateException($"Proposer {block.ProposerIndex} is slashed");
    }

    /// <summary>The execution payload steps of <c>process_block</c>, which Bellatrix adds.</summary>
    public virtual void ProcessPayload(BeaconStateFulu state, Body body, INewPayloadNotifier notifier) { }

    /// <summary>Spec <c>process_operations</c>.</summary>
    public virtual void ProcessOperations(BeaconStateFulu state, Body body, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures, BlockSignatureBatch? batch)
    {
        Deposit[] deposits = body.Deposits ?? [];
        if (state.Eth1Data!.DepositCount < state.Eth1DepositIndex)
            throw new BeaconStateException($"Eth1 deposit count {state.Eth1Data.DepositCount} is below the deposit index {state.Eth1DepositIndex}");
        if ((ulong)deposits.Length != Math.Min(Presets.MaxDeposits, state.Eth1Data.DepositCount - state.Eth1DepositIndex))
            throw new BeaconStateException($"Block has {deposits.Length} deposits, not the outstanding ones");

        foreach (ProposerSlashing slashing in body.ProposerSlashings ?? [])
            ProcessProposerSlashing(state, slashing, cache, pubkeys, verifySignatures, batch);
        foreach (AttesterSlashing slashing in body.AttesterSlashings ?? [])
            ProcessAttesterSlashing(state, slashing, cache, pubkeys, verifySignatures, batch);
        foreach (Attestation attestation in body.Attestations ?? [])
            ProcessAttestation(state, attestation, cache, pubkeys, verifySignatures, batch);
        ProcessDeposits(state, deposits, cache);
        foreach (SignedVoluntaryExit exit in body.VoluntaryExits ?? [])
            ProcessVoluntaryExit(state, exit, cache, pubkeys, verifySignatures, batch);
    }

    /// <summary>Spec <c>process_proposer_slashing</c>.</summary>
    public void ProcessProposerSlashing(BeaconStateFulu state, ProposerSlashing slashing, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures, BlockSignatureBatch? batch)
    {
        BeaconBlockHeader header1 = slashing.SignedHeader1!.Message!;
        BeaconBlockHeader header2 = slashing.SignedHeader2!.Message!;
        if (header1.Slot != header2.Slot || header1.ProposerIndex != header2.ProposerIndex)
            throw new BeaconStateException("Proposer slashing headers are not of one proposer and slot");
        if (SszRoots.HashTreeRoot(header1) == SszRoots.HashTreeRoot(header2))
            throw new BeaconStateException("Proposer slashing headers are identical");
        if (header1.ProposerIndex >= (ulong)state.ReadOnlyValidators.Length)
            throw new BeaconStateException($"Proposer slashing index {header1.ProposerIndex} is out of range");
        if (!state.ReadOnlyValidators[(int)header1.ProposerIndex].IsSlashableValidator(state.GetCurrentEpoch()))
            throw new BeaconStateException($"Proposer {header1.ProposerIndex} is not slashable");

        const string invalidSignature = "Invalid proposer slashing signature";
        if (verifySignatures
            && !(SignatureSets.VerifySignedBeaconBlockHeader(state, slashing.SignedHeader1, pubkeys, batch?.Defer(invalidSignature))
                && SignatureSets.VerifySignedBeaconBlockHeader(state, slashing.SignedHeader2!, pubkeys, batch?.Defer(invalidSignature))))
            throw new BeaconStateException(invalidSignature);

        SlashValidator(state, (int)header1.ProposerIndex, cache);
    }

    /// <summary>Spec <c>process_attester_slashing</c>.</summary>
    public void ProcessAttesterSlashing(BeaconStateFulu state, AttesterSlashing slashing, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures, BlockSignatureBatch? batch)
    {
        IndexedAttestation attestation1 = slashing.Attestation1!;
        IndexedAttestation attestation2 = slashing.Attestation2!;
        if (!BeaconStateAccessors.IsSlashableAttestationData(attestation1.Data!, attestation2.Data!))
            throw new BeaconStateException("Attester slashing votes are not slashable");

        const string invalidAttestation = "Attester slashing attestation is invalid";
        if (!BlockProcessing.IsValidIndexedAttestation(state, ToCurrent(attestation1), pubkeys, verifySignatures, batch?.Defer(invalidAttestation))
            || !BlockProcessing.IsValidIndexedAttestation(state, ToCurrent(attestation2), pubkeys, verifySignatures, batch?.Defer(invalidAttestation)))
            throw new BeaconStateException(invalidAttestation);

        ulong currentEpoch = state.GetCurrentEpoch();
        HashSet<ulong> secondIndices = [.. attestation2.AttestingIndices!];
        bool slashedAny = false;
        foreach (ulong index in attestation1.AttestingIndices!)
        {
            if (secondIndices.Contains(index) && state.ReadOnlyValidators[(int)index].IsSlashableValidator(currentEpoch))
            {
                SlashValidator(state, (int)index, cache);
                slashedAny = true;
            }
        }
        if (!slashedAny)
            throw new BeaconStateException("Attester slashing slashed no validator");
    }

    /// <summary>Spec <c>process_attestation</c>.</summary>
    public virtual void ProcessAttestation(BeaconStateFulu state, Attestation attestation, EpochCache cache, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch? batch)
    {
        BeaconState phase0 = (BeaconState)state;
        AttestationData data = attestation.Data!;
        ValidateAttestation(state, attestation, cache, pubkeys, verifySignature, batch);

        PendingAttestation pending = new()
        {
            AggregationBits = attestation.AggregationBits,
            Data = data,
            InclusionDelay = state.Slot - data.Slot,
            ProposerIndex = state.GetBeaconProposerIndex(),
        };
        bool current = data.Target!.Epoch == state.GetCurrentEpoch();
        Checkpoint justified = current ? state.CurrentJustifiedCheckpoint! : state.PreviousJustifiedCheckpoint!;
        if (data.Source!.Epoch != justified.Epoch || data.Source.Root != justified.Root)
            throw new BeaconStateException("Attestation source does not match the justified checkpoint");
        if (current)
            phase0.CurrentEpochParticipation = [.. phase0.CurrentEpochParticipation ?? [], pending];
        else
            phase0.PreviousEpochParticipation = [.. phase0.PreviousEpochParticipation ?? [], pending];
    }

    /// <summary>The last state slot an attestation for <paramref name="slot"/> may be included at.</summary>
    protected virtual ulong GetLastInclusionSlot(ulong slot) => slot + Presets.SlotsPerEpoch;

    /// <summary>The checks of <c>process_attestation</c> that do not depend on how participation is recorded.</summary>
    /// <returns>The attesting validators, in committee order.</returns>
    protected ulong[] ValidateAttestation(BeaconStateFulu state, Attestation attestation, EpochCache cache, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch? batch)
    {
        AttestationData data = attestation.Data!;
        if (data.Target!.Epoch != state.GetPreviousEpoch() && data.Target.Epoch != state.GetCurrentEpoch())
            throw new BeaconStateException($"Attestation target epoch {data.Target.Epoch} is not the previous or current epoch");
        if (data.Target.Epoch != BeaconStateAccessors.ComputeEpochAtSlot(data.Slot))
            throw new BeaconStateException("Attestation target epoch does not match its slot");
        if (data.Slot + Presets.MinAttestationInclusionDelay > state.Slot || state.Slot > GetLastInclusionSlot(data.Slot))
            throw new BeaconStateException($"Attestation for slot {data.Slot} cannot be included at slot {state.Slot}");

        ulong[] attestingIndices = GetAttestingIndices(state, data, attestation.AggregationBits!, cache);
        const string invalidAttestation = "Invalid indexed attestation";
        if (attestingIndices.Length == 0)
            throw new BeaconStateException(invalidAttestation);
        // Committee members are unique and in range, so only the signature is left of is_valid_indexed_attestation.
        if (verifySignature && !SignatureSets.VerifyIndexedAttestation(state, new Types.IndexedAttestation
        {
            AttestingIndices = attestingIndices,
            Data = data,
            Signature = attestation.Signature,
        }, pubkeys, batch?.Defer(invalidAttestation)))
            throw new BeaconStateException(invalidAttestation);
        return attestingIndices;
    }

    /// <summary>Spec <c>get_attesting_indices</c>, in committee order.</summary>
    protected static ulong[] GetAttestingIndices(BeaconStateFulu state, AttestationData data, BitArray aggregationBits, EpochCache cache)
    {
        CommitteeCache committees = cache.GetCommitteeCache(state, data.Target!.Epoch);
        if (data.Index >= (ulong)committees.CommitteesPerSlot)
            throw new BeaconStateException($"Committee index {data.Index} out of range ({committees.CommitteesPerSlot} committees per slot)");
        ReadOnlySpan<int> committee = committees.GetBeaconCommittee(data.Slot, (int)data.Index);
        if (aggregationBits.Length != committee.Length)
            throw new BeaconStateException($"Aggregation bits length {aggregationBits.Length} does not match committee size {committee.Length}");

        List<ulong> indices = [with(committee.Length)];
        for (int i = 0; i < committee.Length; i++)
        {
            if (aggregationBits[i])
                indices.Add((ulong)committee[i]);
        }
        return [.. indices];
    }

    private static Types.IndexedAttestation ToCurrent(IndexedAttestation attestation) => new()
    {
        AttestingIndices = attestation.AttestingIndices,
        Data = attestation.Data,
        Signature = attestation.Signature,
    };

    /// <summary>Spec <c>process_deposit</c> over the deposits of a block, appending the new validators once.</summary>
    public void ProcessDeposits(BeaconStateFulu state, Deposit[] deposits, EpochCache cache)
    {
        if (deposits.Length == 0)
            return;

        Validator[] validators = state.ValidatorBuffer!.Items;
        cache.IndexDepositPubkeys(validators, deposits);
        List<Validator> added = [];
        List<ulong> addedBalances = [];
        foreach (Deposit deposit in deposits)
        {
            DepositData data = deposit.Data!;
            // The +1 accounts for the SSZ list-length mix-in of the deposit tree.
            if (!BlockProcessing.IsValidMerkleBranch(SszRoots.HashTreeRoot(data), deposit.Proof!, Presets.DepositContractTreeDepth + 1, state.Eth1DepositIndex, state.Eth1Data!.DepositRoot!))
                throw new BeaconStateException($"Invalid Merkle proof for deposit {state.Eth1DepositIndex}");
            state.Eth1DepositIndex++;

            if (cache.FindRequestValidator(validators, data.Pubkey) is { } index)
            {
                state.IncreaseBalance(index, data.Amount);
                continue;
            }
            int addedIndex = added.FindIndex(validator => validator.Pubkey == data.Pubkey);
            if (addedIndex >= 0)
            {
                addedBalances[addedIndex] += data.Amount;
            }
            else if (DepositSignatureVerifier.IsValid(spec, data.Pubkey, data.WithdrawalCredentials!, data.Amount, data.Signature))
            {
                added.Add(GetValidatorFromDeposit(data));
                addedBalances.Add(data.Amount);
            }
        }

        if (added.Count > 0)
            AddValidators(state, added, addedBalances);
    }

    private static Validator GetValidatorFromDeposit(DepositData data) => new()
    {
        Pubkey = data.Pubkey,
        WithdrawalCredentials = data.WithdrawalCredentials,
        EffectiveBalance = Math.Min(data.Amount - data.Amount % Presets.EffectiveBalanceIncrement, Presets.MaxEffectiveBalance),
        ActivationEligibilityEpoch = Presets.FarFutureEpoch,
        ActivationEpoch = Presets.FarFutureEpoch,
        ExitEpoch = Presets.FarFutureEpoch,
        WithdrawableEpoch = Presets.FarFutureEpoch,
    };

    protected virtual void AddValidators(BeaconStateFulu state, List<Validator> validators, List<ulong> balances)
    {
        state.Validators = [.. state.Validators!, .. validators];
        state.Balances = [.. state.Balances!, .. balances];
    }

    /// <summary>Spec <c>process_voluntary_exit</c>.</summary>
    public void ProcessVoluntaryExit(BeaconStateFulu state, SignedVoluntaryExit signedExit, EpochCache cache, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch? batch)
    {
        VoluntaryExit exit = signedExit.Message!;
        if (exit.ValidatorIndex >= (ulong)state.ReadOnlyValidators.Length)
            throw new BeaconStateException($"Voluntary exit validator index {exit.ValidatorIndex} is out of range");
        Validator validator = state.ReadOnlyValidators[(int)exit.ValidatorIndex];
        ulong currentEpoch = state.GetCurrentEpoch();
        if (!validator.IsActiveValidator(currentEpoch) || validator.ExitEpoch != Presets.FarFutureEpoch)
            throw new BeaconStateException($"Exiting validator {exit.ValidatorIndex} is not active, or already exiting");
        if (currentEpoch < exit.Epoch)
            throw new BeaconStateException($"Voluntary exit is not valid before epoch {exit.Epoch}");
        if (currentEpoch < validator.ActivationEpoch + Presets.ShardCommitteePeriod)
            throw new BeaconStateException($"Validator {exit.ValidatorIndex} has not been active long enough");

        const string invalidSignature = "Invalid voluntary exit signature";
        if (verifySignature && !VerifyVoluntaryExit(state, signedExit, pubkeys, batch?.Defer(invalidSignature)))
            throw new BeaconStateException(invalidSignature);

        InitiateValidatorExit(state, (int)exit.ValidatorIndex, cache);
    }

    protected virtual bool VerifyVoluntaryExit(BeaconStateFulu state, SignedVoluntaryExit signedExit, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral)
    {
        VoluntaryExit exit = signedExit.Message!;
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(exit), state.GetDomain(DomainType.VoluntaryExit, exit.Epoch));
        return SignatureSets.TryGetValidatorKey(pubkeys, state.ReadOnlyValidators, exit.ValidatorIndex, out G1Affine key)
            && BlockSignatureBatch.Verify(key, signedExit.Signature, signingRoot, deferral);
    }

    /// <summary>Spec <c>initiate_validator_exit</c>, whose churn counts validators.</summary>
    /// <remarks>The end of the exit queue is remembered, because the specification scans the registry for it on every exit.</remarks>
    protected void InitiateValidatorExit(BeaconStateFulu state, int index, EpochCache cache)
    {
        Validator validator = state.ReadOnlyValidators[index];
        if (validator.ExitEpoch != Presets.FarFutureEpoch)
            return;

        if (!ReferenceEquals(_exitQueueState, state))
        {
            _exitQueueState = state;
            _lastExitEpoch = _lastExitEpochCount = 0;
            foreach (Validator other in state.ReadOnlyValidators)
            {
                if (other.ExitEpoch == Presets.FarFutureEpoch || other.ExitEpoch < _lastExitEpoch)
                    continue;
                _lastExitEpochCount = other.ExitEpoch == _lastExitEpoch ? _lastExitEpochCount + 1 : 1;
                _lastExitEpoch = other.ExitEpoch;
            }
        }

        ulong exitEpoch = Math.Max(_lastExitEpoch, BeaconStateAccessors.ComputeActivationExitEpoch(state.GetCurrentEpoch()));
        ulong count = exitEpoch == _lastExitEpoch ? _lastExitEpochCount : 0;
        if (count >= state.GetValidatorChurnLimit(cache))
        {
            exitEpoch++;
            count = 0;
        }

        Validator updated = validator.Clone();
        updated.ExitEpoch = exitEpoch;
        updated.WithdrawableEpoch = BeaconStateMutators.CheckedEpochSum(exitEpoch, Presets.MinValidatorWithdrawabilityDelay);
        state.Validators![index] = updated;
        _lastExitEpoch = exitEpoch;
        _lastExitEpochCount = count + 1;
    }

    /// <summary>Spec <c>slash_validator</c>, with the proposer as the whistleblower.</summary>
    protected void SlashValidator(BeaconStateFulu state, int slashedIndex, EpochCache cache)
    {
        ulong epoch = state.GetCurrentEpoch();
        InitiateValidatorExit(state, slashedIndex, cache);

        Validator validator = state.ReadOnlyValidators[slashedIndex].Clone();
        validator.Slashed = true;
        validator.WithdrawableEpoch = Math.Max(validator.WithdrawableEpoch, BeaconStateMutators.CheckedEpochSum(epoch, Presets.EpochsPerSlashingsVector));
        state.Validators![slashedIndex] = validator;

        state.Slashings![(int)(epoch % Presets.EpochsPerSlashingsVector)] += validator.EffectiveBalance;
        state.DecreaseBalance(slashedIndex, validator.EffectiveBalance / MinSlashingPenaltyQuotient);
        // The proposer is also the whistleblower, so it receives both shares of the reward.
        state.IncreaseBalance((int)state.GetBeaconProposerIndex(), validator.EffectiveBalance / LegacyPresets.WhistleblowerRewardQuotient);
    }

    /// <summary>Fills this epoch's proposers, which the working state keeps where Fulu keeps its lookahead.</summary>
    protected virtual void RefillProposers(BeaconStateFulu state)
    {
        ulong epoch = state.GetCurrentEpoch();
        int[] active = state.GetActiveValidatorIndices(epoch);
        ulong[] proposers = new ulong[Presets.ProposerLookaheadSlots];
        Array.Fill(proposers, NoProposer);
        if (active.Length > 0)
        {
            Span<byte> preimage = stackalloc byte[32 + 8];
            state.GetSeed(epoch, DomainType.BeaconProposer).Bytes.CopyTo(preimage);
            ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
            for (ulong i = 0; i < Presets.SlotsPerEpoch; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(preimage[32..], startSlot + i);
                proposers[i] = (ulong)SampleByEffectiveBalance(state, active, ThreadSha256.HashData(preimage), count: 1)[0];
            }
        }
        state.ProposerLookahead = proposers;
    }

    /// <summary>Spec <c>compute_proposer_index</c> and <c>get_next_sync_committee_indices</c> before Electra: samples validators in proportion to their effective balance.</summary>
    protected static int[] SampleByEffectiveBalance(BeaconStateFulu state, ReadOnlySpan<int> indices, ReadOnlySpan<byte> seed, int count)
    {
        if (indices.IsEmpty)
            throw new BeaconStateException("Cannot sample from an empty validator set");

        Span<byte> preimage = stackalloc byte[32 + 8];
        Span<byte> randomBytes = stackalloc byte[32];
        seed[..32].CopyTo(preimage);
        int[] sampled = new int[count];
        int found = 0;
        for (ulong i = 0; found < count; i++)
        {
            int candidate = indices[SwapOrNotShuffle.ComputeShuffledIndex((int)(i % (ulong)indices.Length), indices.Length, seed)];
            if (i % 32 == 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(preimage[32..], i / 32);
                ThreadSha256.HashData(preimage, randomBytes);
            }
            if (state.ReadOnlyValidators[candidate].EffectiveBalance * LegacyPresets.MaxRandomByte >= Presets.MaxEffectiveBalance * randomBytes[(int)(i % 32)])
                sampled[found++] = candidate;
        }
        return sampled;
    }

    /// <summary>Spec <c>process_epoch</c>.</summary>
    public virtual void ProcessEpoch(BeaconStateFulu state, EpochCache cache)
    {
        ProcessJustificationAndFinalization(state, cache);
        ProcessRewardsAndPenalties(state, cache);
        ProcessRegistryUpdates(state, cache);
        ProcessSlashings(state, cache);
        EpochProcessing.ProcessEth1DataReset(state);
        ProcessEffectiveBalanceUpdates(state, cache);
        EpochProcessing.ProcessSlashingsReset(state);
        EpochProcessing.ProcessRandaoMixesReset(state);
        ProcessHistoricalUpdate(state);
        ProcessParticipationUpdates(state);
    }

    /// <summary>Spec <c>process_justification_and_finalization</c>.</summary>
    public virtual void ProcessJustificationAndFinalization(BeaconStateFulu state, EpochCache cache)
    {
        if (state.GetCurrentEpoch() <= Presets.GenesisEpoch + 1)
            return;

        BeaconState phase0 = (BeaconState)state;
        Participation[] previous = GetParticipation(state, phase0.PreviousEpochParticipation ?? [], cache, withHead: false);
        Participation[] current = GetParticipation(state, phase0.CurrentEpochParticipation ?? [], cache, withHead: false);
        JustificationAndFinalizationState result = new(state);
        EpochProcessing.WeighJustificationAndFinalization(result, state.GetTotalActiveBalance(cache),
            GetAttestingBalance(state, previous, Vote.Target), GetAttestingBalance(state, current, Vote.Target), state.Slot, state.BlockRoots!);
        result.ApplyTo(state);
    }

    [Flags]
    private enum Vote : byte
    {
        Source = 1,
        Target = 2,
        Head = 4,
    }

    /// <summary>What one validator's pending attestations of an epoch add up to.</summary>
    /// <param name="InclusionDelay">The lowest delay of its attestations, of which <paramref name="Proposer"/> included the first.</param>
    private readonly record struct Participation(Vote Votes, ulong InclusionDelay, ulong Proposer);

    /// <summary>The matching votes of every unslashed validator in <paramref name="attestations"/>, which share one target epoch.</summary>
    /// <param name="withHead">Whether to match head votes, which justification does not need and cannot for the state's own slot.</param>
    private static Participation[] GetParticipation(BeaconStateFulu state, PendingAttestation[] attestations, EpochCache cache, bool withHead)
    {
        Participation[] participation = new Participation[state.ReadOnlyValidators.Length];
        foreach (PendingAttestation attestation in attestations)
        {
            AttestationData data = attestation.Data!;
            Vote votes = Vote.Source;
            if (data.Target!.Root == state.GetBlockRoot(data.Target.Epoch))
            {
                votes |= Vote.Target;
                if (withHead && data.BeaconBlockRoot == state.GetBlockRootAtSlot(data.Slot))
                    votes |= Vote.Head;
            }

            foreach (ulong index in GetAttestingIndices(state, data, attestation.AggregationBits!, cache))
            {
                if (state.ReadOnlyValidators[(int)index].Slashed)
                    continue;
                Participation known = participation[index];
                participation[index] = known.Votes == 0 || attestation.InclusionDelay < known.InclusionDelay
                    ? new Participation(known.Votes | votes, attestation.InclusionDelay, attestation.ProposerIndex)
                    : known with { Votes = known.Votes | votes };
            }
        }
        return participation;
    }

    private static ulong GetAttestingBalance(BeaconStateFulu state, Participation[] participation, Vote vote)
    {
        ulong balance = 0;
        for (int i = 0; i < participation.Length; i++)
        {
            if ((participation[i].Votes & vote) != 0)
                balance += state.ReadOnlyValidators[i].EffectiveBalance;
        }
        return Math.Max(Presets.EffectiveBalanceIncrement, balance);
    }

    /// <summary>Spec <c>process_rewards_and_penalties</c>.</summary>
    public void ProcessRewardsAndPenalties(BeaconStateFulu state, EpochCache cache)
    {
        if (state.GetCurrentEpoch() == Presets.GenesisEpoch)
            return;

        ApplyRewardDeltas(state, cache);
    }

    public virtual void ApplyRewardDeltas(BeaconStateFulu state, EpochCache cache)
    {
        (ulong[] rewards, ulong[] penalties) = GetAttestationDeltas(state, cache);
        for (int i = 0; i < rewards.Length; i++)
        {
            state.IncreaseBalance(i, rewards[i]);
            state.DecreaseBalance(i, penalties[i]);
        }
    }

    /// <summary>Spec <c>get_attestation_deltas</c>.</summary>
    private static (ulong[] Rewards, ulong[] Penalties) GetAttestationDeltas(BeaconStateFulu state, EpochCache cache)
    {
        ReadOnlySpan<Validator> validators = state.ReadOnlyValidators;
        ulong[] rewards = new ulong[validators.Length];
        ulong[] penalties = new ulong[validators.Length];
        ulong previousEpoch = state.GetPreviousEpoch();
        ulong totalBalance = state.GetTotalActiveBalance(cache);
        ulong totalBalanceRoot = BeaconStateAccessors.IntegerSquareRoot(totalBalance);
        ulong finalityDelay = previousEpoch - state.FinalizedCheckpoint!.Epoch;
        bool isLeaking = finalityDelay > Presets.MinEpochsToInactivityPenalty;
        Participation[] participation = GetParticipation(state, ((BeaconState)state).PreviousEpochParticipation ?? [], cache, withHead: true);

        Vote[] components = [Vote.Source, Vote.Target, Vote.Head];
        ulong[] attestingIncrements = Array.ConvertAll(components, vote => GetAttestingBalance(state, participation, vote) / Presets.EffectiveBalanceIncrement);
        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            Participation known = participation[i];
            ulong baseReward = validator.EffectiveBalance * Presets.BaseRewardFactor / totalBalanceRoot / LegacyPresets.BaseRewardsPerEpoch;
            ulong proposerReward = baseReward / LegacyPresets.ProposerRewardQuotient;

            if (validator.IsActiveValidator(previousEpoch) || (validator.Slashed && previousEpoch + 1 < validator.WithdrawableEpoch))
            {
                for (int component = 0; component < components.Length; component++)
                {
                    if ((known.Votes & components[component]) == 0)
                        penalties[i] += baseReward;
                    else
                        // A leak cancels the full base reward through the inactivity penalty, so full participation is compensated here.
                        rewards[i] += isLeaking ? baseReward : baseReward * attestingIncrements[component] / (totalBalance / Presets.EffectiveBalanceIncrement);
                }

                if (isLeaking)
                {
                    penalties[i] += LegacyPresets.BaseRewardsPerEpoch * baseReward - proposerReward;
                    if ((known.Votes & Vote.Target) == 0)
                        penalties[i] += validator.EffectiveBalance * finalityDelay / LegacyPresets.InactivityPenaltyQuotient;
                }
            }

            if (known.Votes != 0)
            {
                rewards[known.Proposer] += proposerReward;
                rewards[i] += (baseReward - proposerReward) / known.InclusionDelay;
            }
        }
        return (rewards, penalties);
    }

    protected virtual ulong GetActivationChurnLimit(BeaconStateFulu state, EpochCache cache) => state.GetValidatorChurnLimit(cache);

    /// <summary>Spec <c>process_registry_updates</c>.</summary>
    public virtual void ProcessRegistryUpdates(BeaconStateFulu state, EpochCache cache)
    {
        ulong currentEpoch = state.GetCurrentEpoch();
        Validator[] validators = state.Validators!;
        List<(ulong EligibilityEpoch, int Index)> activationQueue = [];
        for (int i = 0; i < validators.Length; i++)
        {
            if (validators[i].IsEligibleForActivationQueue())
            {
                Validator eligible = validators[i].Clone();
                eligible.ActivationEligibilityEpoch = currentEpoch + 1;
                validators[i] = eligible;
            }
            if (validators[i].IsActiveValidator(currentEpoch) && validators[i].EffectiveBalance <= Presets.EjectionBalance)
                InitiateValidatorExit(state, i, cache);
            if (state.IsEligibleForActivation(validators[i]))
                activationQueue.Add((validators[i].ActivationEligibilityEpoch, i));
        }

        activationQueue.Sort();
        ulong churnLimit = GetActivationChurnLimit(state, cache);
        for (int i = 0; i < activationQueue.Count && (ulong)i < churnLimit; i++)
        {
            int index = activationQueue[i].Index;
            Validator activated = validators[index].Clone();
            activated.ActivationEpoch = BeaconStateAccessors.ComputeActivationExitEpoch(currentEpoch);
            validators[index] = activated;
        }
    }

    /// <summary>Spec <c>process_slashings</c>.</summary>
    public virtual void ProcessSlashings(BeaconStateFulu state, EpochCache cache)
    {
        ulong totalBalance = state.GetTotalActiveBalance(cache);
        ulong totalSlashings = 0;
        foreach (ulong slashing in state.Slashings!)
            totalSlashings += slashing;
        ulong adjustedTotalSlashings = Math.Min(totalSlashings * ProportionalSlashingMultiplier, totalBalance);
        ulong withdrawableEpoch = state.GetCurrentEpoch() + Presets.EpochsPerSlashingsVector / 2;

        ReadOnlySpan<Validator> validators = state.ReadOnlyValidators;
        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            if (validator.Slashed && validator.WithdrawableEpoch == withdrawableEpoch)
                state.DecreaseBalance(i, validator.EffectiveBalance / Presets.EffectiveBalanceIncrement * adjustedTotalSlashings / totalBalance * Presets.EffectiveBalanceIncrement);
        }
    }

    /// <summary>Spec <c>process_effective_balance_updates</c>.</summary>
    public virtual void ProcessEffectiveBalanceUpdates(BeaconStateFulu state, EpochCache cache)
    {
        const ulong hysteresisIncrement = Presets.EffectiveBalanceIncrement / Presets.HysteresisQuotient;
        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            ulong balance = state.Balances![i];
            ulong effectiveBalance = validators[i].EffectiveBalance;
            if (balance + hysteresisIncrement * Presets.HysteresisDownwardMultiplier < effectiveBalance
                || effectiveBalance + hysteresisIncrement * Presets.HysteresisUpwardMultiplier < balance)
            {
                Validator updated = validators[i].Clone();
                updated.EffectiveBalance = Math.Min(balance - balance % Presets.EffectiveBalanceIncrement, Presets.MaxEffectiveBalance);
                validators[i] = updated;
            }
        }
        cache.InvalidateTotalActiveBalance();
    }

    /// <summary>Spec <c>process_historical_roots_update</c>.</summary>
    /// <remarks>A <c>HistoricalBatch</c> and the <c>HistoricalSummary</c> Capella replaced it with have the same root.</remarks>
    public virtual void ProcessHistoricalUpdate(BeaconStateFulu state)
    {
        EpochProcessing.ProcessHistoricalSummariesUpdate(state);
        if (state.HistoricalSummaries is not [HistoricalSummary batch])
            return;
        state.HistoricalRoots = [.. state.HistoricalRoots ?? [], SszRoots.HashTreeRoot(batch)];
        state.HistoricalSummaries = [];
    }

    /// <summary>Spec <c>process_participation_record_updates</c>.</summary>
    public virtual void ProcessParticipationUpdates(BeaconStateFulu state)
    {
        BeaconState phase0 = (BeaconState)state;
        phase0.PreviousEpochParticipation = phase0.CurrentEpochParticipation;
        phase0.CurrentEpochParticipation = [];
    }
}
