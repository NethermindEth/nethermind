// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.LightClient.Consensus;

internal sealed class IrrelevantLightClientUpdateException() : IOException("Update is not relevant to the current sync period.");

/// <summary>Authenticates finalized and optimistic headers from a trusted beacon checkpoint.</summary>
/// <remarks>
/// Implements the consensus-specs light client sync protocol using the Electra and Gloas proof indices.
/// Forced progress never changes the header exposed as finalized to RPC.
/// One synchronization owner calls <see cref="Process(LightClientUpdate, ulong)"/>;
/// RPC readers receive copies of atomically published snapshots.
/// https://ethereum.github.io/consensus-specs/specs/altair/light-client/sync-protocol/
/// </remarks>
internal sealed class LightClientStore
{
    private readonly BeaconChainSpec _spec;
    private Snapshot _snapshot;

    private sealed record Snapshot(
        LightClientHeader Header,
        LightClientHeader SyncHeader,
        LightClientHeader OptimisticHeader,
        SyncCommittee CurrentCommittee,
        SyncCommittee? NextCommittee,
        LightClientUpdate? BestUpdate,
        int PreviousMaxParticipants,
        int CurrentMaxParticipants);

    public LightClientStore(BeaconChainSpec spec, Hash256 checkpoint, LightClientBootstrap bootstrap, ulong currentSlot)
    {
        _spec = spec;
        ValidateSupportedSlot(currentSlot);
        ValidateHeader(bootstrap.Header);
        LightClientHeader header = bootstrap.Header!;
        Require(header.Beacon!.Slot <= currentSlot, "Checkpoint is in the future.");
        Require(currentSlot - header.Beacon.Slot <= 14 * 24 * 60 * 60 / spec.SecondsPerSlot, "Checkpoint is older than fourteen days.");
        Require(SszRoots.HashTreeRoot(header.Beacon) == checkpoint, "Bootstrap does not match the trusted checkpoint.");
        ValidateCommittee(bootstrap.CurrentSyncCommittee);
        Require(VerifyBranch(SszRoots.HashTreeRoot(bootstrap.CurrentSyncCommittee!), bootstrap.CurrentSyncCommitteeBranch,
            header.IsGloas ? 2945 : 86, header.Beacon.StateRoot!), "Invalid current sync committee proof.");
        LightClientHeader copy = CloneHeader(header);
        _snapshot = new(copy, copy, copy, Clone(bootstrap.CurrentSyncCommittee!), null, null, 0, 0);
    }

    /// <summary>A detached copy of the most recent authenticated finalized header.</summary>
    public LightClientHeader FinalizedHeader => CloneHeader(Volatile.Read(ref _snapshot).Header);

    /// <summary>A detached copy of the best authenticated header; it may be unfinalized.</summary>
    public LightClientHeader OptimisticHeader => CloneHeader(Volatile.Read(ref _snapshot).OptimisticHeader);

    /// <summary>The sync committee period of the authenticated finalized beacon header.</summary>
    public ulong Period => PeriodAtSlot(Volatile.Read(ref _snapshot).SyncHeader.Beacon!.Slot);

    /// <summary>Whether the next committee is authenticated and available for period rollover.</summary>
    public bool NextSyncCommitteeKnown => Volatile.Read(ref _snapshot).NextCommittee is not null;

    internal int PreviousMaxParticipants => Volatile.Read(ref _snapshot).PreviousMaxParticipants;

    internal int CurrentMaxParticipants => Volatile.Read(ref _snapshot).CurrentMaxParticipants;

    /// <summary>A detached copy of the best signed update available for timeout recovery.</summary>
    internal LightClientUpdate? BestUpdate
    {
        get
        {
            LightClientUpdate? update = Volatile.Read(ref _snapshot).BestUpdate;
            return update is null ? null : CloneUpdate(update);
        }
    }

    public void Process(LightClientFinalityUpdate update, ulong currentSlot) => Process(new LightClientUpdate
    {
        AttestedHeader = update.AttestedHeader,
        FinalizedHeader = update.FinalizedHeader,
        FinalityBranch = update.FinalityBranch,
        SyncAggregate = update.SyncAggregate,
        SignatureSlot = update.SignatureSlot,
    }, currentSlot);

    /// <summary>Validates an update and advances the finalized header only with supermajority finality.</summary>
    /// <exception cref="InvalidDataException">An update is malformed, unsupported, or unauthenticated.</exception>
    public void Process(LightClientUpdate update, ulong currentSlot) => ProcessCore(
        update.AttestedHeader, update.FinalizedHeader, update.FinalityBranch,
        update.NextSyncCommittee, update.NextSyncCommitteeBranch, update.SyncAggregate,
        update.SignatureSlot, currentSlot);

    /// <summary>Tracks a signed head without making it available as finalized state.</summary>
    public void Process(LightClientOptimisticUpdate update, ulong currentSlot) => ProcessCore(
        update.AttestedHeader, null, null, null, null, update.SyncAggregate,
        update.SignatureSlot, currentSlot);

    /// <summary>Advances the sync committee after an update timeout, while retaining the proven finalized head.</summary>
    public bool ForceUpdate(ulong currentSlot)
    {
        Snapshot snapshot = _snapshot;
        ulong timeout = _spec.SlotsPerEpoch * Presets.EpochsPerSyncCommitteePeriod;
        if (snapshot.BestUpdate is null || currentSlot <= snapshot.Header.Beacon!.Slot ||
            currentSlot - snapshot.Header.Beacon.Slot <= timeout)
            return false;

        LightClientUpdate best = snapshot.BestUpdate;
        LightClientHeader target = HasNonzeroBranch(best.FinalityBranch) &&
            best.FinalizedHeader!.Beacon!.Slot > snapshot.SyncHeader.Beacon!.Slot
            ? best.FinalizedHeader : best.AttestedHeader!;
        ulong storePeriod = PeriodAtSlot(snapshot.SyncHeader.Beacon!.Slot);
        ulong targetPeriod = PeriodAtSlot(target.Beacon!.Slot);
        Require(targetPeriod <= storePeriod + 1, "Forced update skips a sync committee period.");
        SyncCommittee currentCommittee = snapshot.CurrentCommittee;
        SyncCommittee? nextCommittee = snapshot.NextCommittee;
        if (targetPeriod == storePeriod + 1)
        {
            Require(nextCommittee is not null, "Cannot rotate an unknown committee.");
            currentCommittee = nextCommittee!;
            nextCommittee = HasNonzeroBranch(best.NextSyncCommitteeBranch) &&
                PeriodAtSlot(best.AttestedHeader!.Beacon!.Slot) == targetPeriod ? Clone(best.NextSyncCommittee!) : null;
        }
        else if (nextCommittee is null && HasNonzeroBranch(best.NextSyncCommitteeBranch) &&
            PeriodAtSlot(best.AttestedHeader!.Beacon!.Slot) == storePeriod)
        {
            nextCommittee = Clone(best.NextSyncCommittee!);
        }

        LightClientHeader optimistic = target.Beacon.Slot > snapshot.OptimisticHeader.Beacon!.Slot
            ? CloneHeader(target) : snapshot.OptimisticHeader;
        Volatile.Write(ref _snapshot, snapshot with
        {
            SyncHeader = CloneHeader(target),
            OptimisticHeader = optimistic,
            CurrentCommittee = currentCommittee,
            NextCommittee = nextCommittee,
            BestUpdate = null,
            PreviousMaxParticipants = targetPeriod == storePeriod + 1 ? snapshot.CurrentMaxParticipants : snapshot.PreviousMaxParticipants,
            CurrentMaxParticipants = targetPeriod == storePeriod + 1 ? 0 : snapshot.CurrentMaxParticipants,
        });
        return true;
    }

    private void ProcessCore(LightClientHeader? attested, LightClientHeader? finalized, Hash256[]? finalityBranch,
        SyncCommittee? next, Hash256[]? nextBranch, SyncAggregate? aggregate, ulong signatureSlot, ulong currentSlot)
    {
        ValidateSupportedSlot(currentSlot);
        ValidateSupportedSlot(signatureSlot);
        ValidateHeader(attested);
        Snapshot snapshot = _snapshot;
        bool hasFinality = HasNonzeroBranch(finalityBranch);
        bool hasNextCommittee = HasNonzeroBranch(nextBranch);
        ulong attestedSlot = attested!.Beacon!.Slot;
        ulong finalizedSlot = hasFinality ? finalized?.Beacon?.Slot ?? 0 : 0;
        Require(currentSlot >= signatureSlot && signatureSlot > attestedSlot && attestedSlot >= finalizedSlot,
            "Invalid light-client update slot ordering.");
        ulong storePeriod = PeriodAtSlot(snapshot.SyncHeader.Beacon!.Slot);
        ulong signaturePeriod = PeriodAtSlot(signatureSlot);
        ulong attestedPeriod = PeriodAtSlot(attestedSlot);
        Require(signaturePeriod == storePeriod || signaturePeriod == storePeriod + 1 && snapshot.NextCommittee is not null,
            "Update skips a known sync committee period.");
        if (attestedSlot <= snapshot.SyncHeader.Beacon.Slot &&
            !(hasNextCommittee && snapshot.NextCommittee is null && attestedPeriod == storePeriod))
            throw new IrrelevantLightClientUpdateException();

        int finalityIndex = attested!.IsGloas ? 735 : 169;
        int nextIndex = attested.IsGloas ? 2946 : 87;
        Require(finalityBranch is null || finalityBranch.Length == System.Numerics.BitOperations.Log2((uint)finalityIndex),
            "Invalid finality branch length.");
        if (hasFinality)
        {
            ValidateHeader(finalized);
            Require(PeriodAtSlot(finalizedSlot) <= storePeriod + 1, "Finalized header skips a sync committee period.");
            Require(VerifyBranch(SszRoots.HashTreeRoot(finalized!.Beacon!), finalityBranch, finalityIndex,
                attested.Beacon.StateRoot!), "Invalid finality proof.");
        }
        else Require(finalized is null || finalized.Beacon?.Slot == 0, "Unproved finalized header is present.");

        Require(nextBranch is null || nextBranch.Length == System.Numerics.BitOperations.Log2((uint)nextIndex),
            "Invalid next committee branch length.");
        if (hasNextCommittee)
        {
            ValidateCommittee(next);
            Require(VerifyBranch(SszRoots.HashTreeRoot(next!), nextBranch, nextIndex, attested.Beacon.StateRoot!),
                "Invalid next sync committee proof.");
            if (attestedPeriod == storePeriod && snapshot.NextCommittee is not null)
                Require(SszRoots.HashTreeRoot(next!) == SszRoots.HashTreeRoot(snapshot.NextCommittee),
                    "Next committee conflicts with the authenticated committee.");
        }
        else Require(next is null || IsEmptyCommittee(next), "Committee is present without a proof.");

        SyncCommittee signingCommittee = signaturePeriod == storePeriod ? snapshot.CurrentCommittee : snapshot.NextCommittee!;
        int participants = VerifySignature(signingCommittee, aggregate, signatureSlot, attested.Beacon);
        int currentMax = Math.Max(snapshot.CurrentMaxParticipants, participants);
        int safetyThreshold = Math.Max(snapshot.PreviousMaxParticipants, currentMax) / 2;
        LightClientHeader optimistic = participants > safetyThreshold && attestedSlot > snapshot.OptimisticHeader.Beacon!.Slot
            ? CloneHeader(attested) : snapshot.OptimisticHeader;

        LightClientUpdate candidate = CloneUpdate(new LightClientUpdate
        {
            AttestedHeader = attested,
            FinalizedHeader = finalized,
            FinalityBranch = finalityBranch,
            NextSyncCommittee = next,
            NextSyncCommitteeBranch = nextBranch,
            SyncAggregate = aggregate,
            SignatureSlot = signatureSlot,
        });
        LightClientUpdate? best = snapshot.BestUpdate is null || Better(candidate, snapshot.BestUpdate) ? candidate : snapshot.BestUpdate;

        bool supermajority = participants * 3 >= Presets.SyncCommitteeSize * 2;
        bool canAdvanceFinality = supermajority && hasFinality &&
            (finalizedSlot > snapshot.Header.Beacon!.Slot ||
             snapshot.NextCommittee is null && hasNextCommittee &&
             PeriodAtSlot(finalizedSlot) == attestedPeriod);
        if (!canAdvanceFinality)
        {
            Volatile.Write(ref _snapshot, snapshot with
            {
                OptimisticHeader = optimistic,
                BestUpdate = best,
                CurrentMaxParticipants = currentMax,
            });
            return;
        }

        ulong finalizedPeriod = PeriodAtSlot(finalizedSlot);
        SyncCommittee currentCommittee = snapshot.CurrentCommittee;
        SyncCommittee? nextCommittee = snapshot.NextCommittee;
        if (finalizedPeriod == storePeriod + 1)
        {
            Require(nextCommittee is not null, "Cannot rotate an unknown committee.");
            currentCommittee = nextCommittee!;
            nextCommittee = hasNextCommittee && attestedPeriod == finalizedPeriod ? Clone(next!) : null;
        }
        else if (nextCommittee is null && hasNextCommittee && attestedPeriod == storePeriod)
            nextCommittee = Clone(next!);

        LightClientHeader header = finalizedSlot > snapshot.Header.Beacon!.Slot ? CloneHeader(finalized!) : snapshot.Header;
        LightClientHeader syncHeader = header.Beacon!.Slot > snapshot.SyncHeader.Beacon!.Slot ? header : snapshot.SyncHeader;
        Volatile.Write(ref _snapshot, snapshot with
        {
            Header = header,
            SyncHeader = syncHeader,
            OptimisticHeader = optimistic.Beacon!.Slot < header.Beacon!.Slot ? header : optimistic,
            CurrentCommittee = currentCommittee,
            NextCommittee = nextCommittee,
            BestUpdate = null,
            PreviousMaxParticipants = finalizedPeriod == storePeriod + 1 ? currentMax : snapshot.PreviousMaxParticipants,
            CurrentMaxParticipants = finalizedPeriod == storePeriod + 1 ? 0 : currentMax,
        });
    }

    private ulong PeriodAtSlot(ulong slot) => _spec.GetEpoch(slot) / Presets.EpochsPerSyncCommitteePeriod;

    private void ValidateSupportedSlot(ulong slot) => Require(
        _spec.GetEpoch(slot) >= _spec.ElectraForkEpoch,
        "Only Electra and later light client data is supported.");

    private void ValidateHeader(LightClientHeader? header)
    {
        Require(header?.Beacon is { ParentRoot: not null, StateRoot: not null, BodyRoot: not null }, "Missing beacon header fields.");
        ValidateSupportedSlot(header!.Beacon!.Slot);
        bool gloasSlot = _spec.GetEpoch(header.Beacon.Slot) >= _spec.GloasForkEpoch;
        Require(header.IsGloas || !gloasSlot, "Light-client header format does not match its fork.");
        if (header.IsGloas)
        {
            Require(header.Execution is null && header.ExecutionBlockHash is not null,
                "Missing or malformed Gloas execution block hash.");
            int index = gloasSlot ? 2856 : 812;
            Require(VerifyNormalizedBranch(header.ExecutionBlockHash!, header.ExecutionBranch, index, header.Beacon.BodyRoot!),
                "Invalid Gloas execution block hash proof.");
            return;
        }
        Require(header.Execution is
        {
            ParentHash: not null, FeeRecipient: not null, StateRoot: not null, ReceiptsRoot: not null,
            LogsBloom: not null, PrevRandao: not null, ExtraData: { Length: <= 32 }, BlockHash: not null,
            TransactionsRoot: not null, WithdrawalsRoot: not null,
        }, "Missing or malformed execution header fields.");
        Require(VerifyBranch(SszRoots.HashTreeRoot(header.Execution!), header.ExecutionBranch, 25, header.Beacon.BodyRoot!), "Invalid execution header proof.");
    }

    private static void ValidateCommittee(SyncCommittee? committee) =>
        Require(committee?.Pubkeys?.Length == Presets.SyncCommitteeSize, "Invalid sync committee size.");

    private static bool IsEmptyCommittee(SyncCommittee committee)
    {
        if (committee.Pubkeys?.Length != Presets.SyncCommitteeSize || committee.AggregatePubkey != default)
            return false;
        foreach (BlsPublicKey key in committee.Pubkeys)
            if (key != default) return false;
        return true;
    }

    private static bool HasNonzeroBranch(Hash256[]? branch)
    {
        if (branch is null) return false;
        foreach (Hash256 hash in branch)
            if (hash is null || hash != Hash256.Zero) return true;
        return false;
    }

    private int VerifySignature(SyncCommittee committee, SyncAggregate? aggregate, ulong signatureSlot, BeaconBlockHeader attested)
    {
        Require(aggregate?.SyncCommitteeBits?.Length == Presets.SyncCommitteeSize, "Invalid sync committee bitfield.");
        int participants = 0;
        for (int i = 0; i < Presets.SyncCommitteeSize; i++)
            if (aggregate!.SyncCommitteeBits![i]) participants++;
        Require(participants > 0, "Update has no sync committee participants.");
        Bls.P1 publicKey = new(stackalloc long[Bls.P1.Sz]);
        Bls.P1Affine decoded = new(stackalloc long[Bls.P1Affine.Sz]);
        for (int i = 0; i < Presets.SyncCommitteeSize; i++)
        {
            if (!aggregate!.SyncCommitteeBits![i]) continue;
            Require(decoded.TryDecode(committee.Pubkeys![i].Bytes, out _) && !decoded.IsInf() && decoded.InGroup(), "Invalid sync committee public key.");
            publicKey.Add(decoded);
        }
        Require(!publicKey.ToAffine().IsInf(), "Aggregate public key is infinity.");
        byte[] version = _spec.VersionForEpoch(_spec.GetEpoch(signatureSlot - 1));
        Hash256 domain = Domains.ComputeDomain(DomainType.SyncCommittee, version, _spec.GenesisValidatorsRoot);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(attested), domain);
        Require(BlsSigner.Verify(publicKey.ToAffine(), aggregate!.SyncCommitteeSignature.Bytes, signingRoot.Bytes), "Invalid sync committee signature.");
        return participants;
    }

    private bool Better(LightClientUpdate candidate, LightClientUpdate previous)
    {
        int candidateParticipants = CountParticipants(candidate.SyncAggregate!);
        int previousParticipants = CountParticipants(previous.SyncAggregate!);
        bool candidateSupermajority = candidateParticipants * 3 >= Presets.SyncCommitteeSize * 2;
        bool previousSupermajority = previousParticipants * 3 >= Presets.SyncCommitteeSize * 2;
        if (candidateSupermajority != previousSupermajority) return candidateSupermajority;
        if (!candidateSupermajority && candidateParticipants != previousParticipants)
            return candidateParticipants > previousParticipants;
        bool candidateCommittee = HasNonzeroBranch(candidate.NextSyncCommitteeBranch) &&
            PeriodAtSlot(candidate.AttestedHeader!.Beacon!.Slot) == PeriodAtSlot(candidate.SignatureSlot);
        bool previousCommittee = HasNonzeroBranch(previous.NextSyncCommitteeBranch) &&
            PeriodAtSlot(previous.AttestedHeader!.Beacon!.Slot) == PeriodAtSlot(previous.SignatureSlot);
        if (candidateCommittee != previousCommittee) return candidateCommittee;
        bool candidateFinality = HasNonzeroBranch(candidate.FinalityBranch);
        bool previousFinality = HasNonzeroBranch(previous.FinalityBranch);
        if (candidateFinality != previousFinality) return candidateFinality;
        if (candidateFinality)
        {
            bool candidateCommitteeFinality = PeriodAtSlot(candidate.FinalizedHeader!.Beacon!.Slot) ==
                PeriodAtSlot(candidate.AttestedHeader!.Beacon!.Slot);
            bool previousCommitteeFinality = PeriodAtSlot(previous.FinalizedHeader!.Beacon!.Slot) ==
                PeriodAtSlot(previous.AttestedHeader!.Beacon!.Slot);
            if (candidateCommitteeFinality != previousCommitteeFinality) return candidateCommitteeFinality;
        }
        if (candidateParticipants != previousParticipants) return candidateParticipants > previousParticipants;
        ulong candidateSlot = candidate.AttestedHeader!.Beacon!.Slot;
        ulong previousSlot = previous.AttestedHeader!.Beacon!.Slot;
        return candidateSlot != previousSlot ? candidateSlot < previousSlot : candidate.SignatureSlot < previous.SignatureSlot;
    }

    private static int CountParticipants(SyncAggregate aggregate)
    {
        int count = 0;
        for (int i = 0; i < Presets.SyncCommitteeSize; i++)
            if (aggregate.SyncCommitteeBits![i]) count++;
        return count;
    }

    private static LightClientHeader CloneHeader(LightClientHeader header)
    {
        if (!header.IsGloas)
        {
            LightClientHeader copy = Clone(header);
            copy.ExecutionBlockHash = copy.Execution?.BlockHash;
            return copy;
        }
        GloasLightClientHeader value = new()
        {
            Beacon = header.Beacon,
            ExecutionBlockHash = header.ExecutionBlockHash,
            ExecutionBranch = header.ExecutionBranch,
        };
        GloasLightClientHeader.Decode(GloasLightClientHeader.Encode(value), out GloasLightClientHeader copyGloas);
        return new()
        {
            Beacon = copyGloas.Beacon,
            ExecutionBlockHash = copyGloas.ExecutionBlockHash,
            ExecutionBranch = copyGloas.ExecutionBranch,
            IsGloas = true,
        };
    }

    private static LightClientUpdate CloneUpdate(LightClientUpdate update) => new()
    {
        AttestedHeader = CloneHeader(update.AttestedHeader!),
        FinalizedHeader = update.FinalizedHeader is null ? null : CloneHeader(update.FinalizedHeader),
        FinalityBranch = CloneBranch(update.FinalityBranch),
        NextSyncCommittee = update.NextSyncCommittee is null ? null : Clone(update.NextSyncCommittee),
        NextSyncCommitteeBranch = CloneBranch(update.NextSyncCommitteeBranch),
        SyncAggregate = Clone(update.SyncAggregate!),
        SignatureSlot = update.SignatureSlot,
    };

    private static Hash256[]? CloneBranch(Hash256[]? branch)
    {
        if (branch is null) return null;
        Hash256[] copy = new Hash256[branch.Length];
        for (int i = 0; i < branch.Length; i++)
            copy[i] = branch[i] is null ? null! : new Hash256(branch[i].Bytes);
        return copy;
    }

    internal static bool VerifyBranch(Hash256 leaf, Hash256[]? branch, int generalizedIndex, Hash256 root)
    {
        int depth = System.Numerics.BitOperations.Log2((uint)generalizedIndex);
        if (branch?.Length != depth) return false;
        Span<byte> pair = stackalloc byte[64];
        Span<byte> value = stackalloc byte[32];
        leaf.Bytes.CopyTo(value);
        for (int i = 0; i < depth; i++)
        {
            if (branch[i] is null) return false;
            bool right = (generalizedIndex & 1) != 0;
            value.CopyTo(pair.Slice(right ? 32 : 0, 32));
            branch[i].Bytes.CopyTo(pair.Slice(right ? 0 : 32, 32));
            SHA256.HashData(pair, value);
            generalizedIndex >>= 1;
        }
        return value.SequenceEqual(root.Bytes);
    }

    private static bool VerifyNormalizedBranch(Hash256 leaf, Hash256[]? branch, int generalizedIndex, Hash256 root)
    {
        if (branch?.Length != 11) return false;
        int extra = branch.Length - System.Numerics.BitOperations.Log2((uint)generalizedIndex);
        for (int i = 0; i < extra; i++)
            if (branch[i] != Hash256.Zero) return false;
        return VerifyBranch(leaf, branch[extra..], generalizedIndex, root);
    }

    private static T Clone<T>(T value) where T : class, ISszCodec<T>
    {
        T.Decode(T.Encode(value), out T clone);
        return clone;
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidDataException(message);
    }
}
