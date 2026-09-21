// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// Real Gloas states and blocks for the state-transition tests: a Fulu state advanced through the
/// unmodified Fulu pipeline to the fork boundary, upgraded with <see cref="GloasForkTransition.UpgradeToGloas"/>,
/// plus signed bids, envelopes and minimal blocks valid against it. Every fixture uses distinctive,
/// non-zero values for whatever field a test asserts on - an assertion that would pass against a
/// dropped or zeroed field is not a real assertion.
/// </summary>
internal static class GloasTestFixtures
{
    public const ulong Gwei = 1_000_000_000;

    // Large enough that every (slot, committee) slice in a 32-slot epoch gets at least one member
    // during PTC computation; with too few validators most slices are empty and
    // ComputeBalanceWeightedSelection has nothing to sample from.
    public const int ValidatorCount = 2048;

    // Keeps validator keys clear of the builder key (200) and the deposit keys tests derive (300+).
    private const int ValidatorKeyOffset = 1000;

    public static readonly ulong BoundarySlot = Presets.SlotsPerEpoch;

    private static readonly byte[] MasterSkBytes = Bytes.FromHexString("0x2cd4ba406b522459d57a0bed51a397435c0bb11dd5f3ca1152b3694bb91d7c22");
    private static readonly byte[] GloasVersion = Bytes.FromHexString("0x07000000");

    public static BeaconChainSpec SyntheticSpec(ulong gloasForkEpoch = 0) => new()
    {
        SecondsPerSlot = 12,
        SlotsPerEpoch = 32,
        GenesisTime = 1_606_824_023,
        GenesisValidatorsRoot = Hash256.Zero,
        Forks = [new(Bytes.FromHexString("0x06000000"), 0)],
        BlobSchedule = [],
        ElectraForkEpoch = 0,
        FuluForkEpoch = 0,
        MaxBlobsPerBlockElectra = 9,
        GloasForkEpoch = gloasForkEpoch,
        GloasForkVersion = GloasVersion,
        Bootnodes = [],
    };

    /// <summary>
    /// A post-upgrade Gloas state at the fork boundary slot (<see cref="BoundarySlot"/>), with one
    /// active, payload-builder-version builder (secret key <paramref name="builderSk"/>, index 0,
    /// starting balance 40 Gwei returned as <paramref name="builderStartingBalance"/>) and everyone
    /// proposing from validator 0, so a single derived key signs both blocks and (where exercised) RANDAO.
    /// </summary>
    public static BeaconStateGloas CreateGloasState(out Bls.SecretKey builderSk, out ulong builderStartingBalance)
    {
        BeaconStateGloas state = GloasForkTransition.UpgradeToGloas(CreateFuluStateAtBoundary(ValidatorCount), SyntheticSpec());

        builderSk = DeriveKey(200);
        BlsPublicKey builderPubkey = new(new Bls.P1(builderSk).Compress());
        builderStartingBalance = 40 * Gwei;
        state.Builders = [new Builder
        {
            Pubkey = builderPubkey,
            Version = Presets.PayloadBuilderVersion,
            ExecutionAddress = new Address(BuilderWithdrawalCredentials(0xC0).Bytes[12..]),
            Balance = builderStartingBalance,
            DepositEpoch = 0,
            WithdrawableEpoch = Presets.FarFutureEpoch,
        }];

        return state;
    }

    /// <summary>
    /// The Fulu state <see cref="CreateGloasState"/> upgrades, advanced from slot 0 to the boundary
    /// under the unmodified Fulu pipeline. Deterministic: two calls yield equal states.
    /// </summary>
    public static BeaconStateFulu CreateFuluStateAtBoundary(int validatorCount)
    {
        BeaconStateFulu state = CreateFuluState(validatorCount);
        SlotProcessing.ProcessSlots(state, BoundarySlot, new EpochCache());
        return state;
    }

    public static BeaconStateFulu CreateFuluState(int validatorCount)
    {
        Hash256[] randaoMixes = new Hash256[(int)Presets.EpochsPerHistoricalVector];
        Array.Fill(randaoMixes, Hash(0x42));
        Hash256[] blockRoots = new Hash256[(int)Presets.SlotsPerHistoricalRoot];
        Array.Fill(blockRoots, Hash256.Zero);
        Hash256[] stateRoots = new Hash256[(int)Presets.SlotsPerHistoricalRoot];
        Array.Fill(stateRoots, Hash256.Zero);

        Validator[] validators = new Validator[validatorCount];
        ulong[] balances = new ulong[validatorCount];
        for (int i = 0; i < validatorCount; i++)
        {
            validators[i] = new Validator
            {
                Pubkey = Pubkey((byte)(0x50 + i)),
                WithdrawalCredentials = Hash256.Zero,
                EffectiveBalance = 32 * Gwei,
                ActivationEpoch = 0,
                ExitEpoch = Presets.FarFutureEpoch,
                WithdrawableEpoch = Presets.FarFutureEpoch,
                ActivationEligibilityEpoch = 0,
            };
            balances[i] = 32 * Gwei;
        }

        return new BeaconStateFulu
        {
            GenesisTime = 1_606_824_023,
            GenesisValidatorsRoot = Hash256.Zero,
            Slot = 0,
            Fork = new Fork { PreviousVersion = Bytes.FromHexString("0x05000000"), CurrentVersion = Bytes.FromHexString("0x06000000"), Epoch = 0 },
            LatestBlockHeader = new BeaconBlockHeader { Slot = 0, ProposerIndex = 0, ParentRoot = Hash(0x02), StateRoot = Hash256.Zero, BodyRoot = Hash256.Zero },
            Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero },
            Validators = validators,
            Balances = balances,
            RandaoMixes = randaoMixes,
            BlockRoots = blockRoots,
            StateRoots = stateRoots,
            HistoricalRoots = [],
            Eth1DataVotes = [],
            Slashings = new ulong[(int)Presets.EpochsPerSlashingsVector],
            PreviousEpochParticipation = new byte[validatorCount],
            CurrentEpochParticipation = new byte[validatorCount],
            InactivityScores = new ulong[validatorCount],
            PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            // Epoch 1 (not 0): the builder onboarded by CreateGloasState has DepositEpoch = 0, and
            // is_active_builder requires deposit_epoch strictly less than the finalized epoch.
            FinalizedCheckpoint = new Checkpoint { Epoch = 1, Root = Hash256.Zero },
            JustificationBits = new BitArray(4),
            CurrentSyncCommittee = new SyncCommittee { Pubkeys = FillCommittee(validators[0].Pubkey), AggregatePubkey = Pubkey(0x60) },
            NextSyncCommittee = new SyncCommittee { Pubkeys = FillCommittee(validators[0].Pubkey), AggregatePubkey = Pubkey(0x61) },
            LatestExecutionPayloadHeader = new ExecutionPayloadHeader { ParentHash = Hash(0x70), BlockHash = Hash(0x71), PrevRandao = Hash(0x72), GasLimit = 30_000_000 },
            ProposerLookahead = new ulong[(int)Presets.ProposerLookaheadSlots],
            PendingDeposits = [],
            PendingPartialWithdrawals = [],
            PendingConsolidations = [],
        };
    }

    private static BlsPublicKey[] FillCommittee(BlsPublicKey pubkey)
    {
        BlsPublicKey[] committee = new BlsPublicKey[Presets.SyncCommitteeSize];
        Array.Fill(committee, pubkey);
        return committee;
    }

    /// <summary>The key <see cref="InstallRealValidatorKeys"/> gives validator <paramref name="validatorIndex"/>.</summary>
    public static Bls.SecretKey ValidatorKey(int validatorIndex) => DeriveKey(ValidatorKeyOffset + validatorIndex);

    /// <summary>
    /// Replaces every validator's placeholder pubkey with the real key <see cref="ValidatorKey"/>
    /// derives for it and returns the decompressed cache the signature checks read. The sync
    /// committees are re-pointed at validator 0's new key so <see cref="ApplyBlock"/> still
    /// resolves their members.
    /// </summary>
    public static PubkeyCache InstallRealValidatorKeys(BeaconStateGloas state)
    {
        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            Validator updated = validators[i].Clone();
            updated.Pubkey = new BlsPublicKey(new Bls.P1(ValidatorKey(i)).Compress());
            validators[i] = updated;
        }
        state.CurrentSyncCommittee = new SyncCommittee { Pubkeys = FillCommittee(validators[0].Pubkey), AggregatePubkey = Pubkey(0x60) };
        state.NextSyncCommittee = new SyncCommittee { Pubkeys = FillCommittee(validators[0].Pubkey), AggregatePubkey = Pubkey(0x61) };

        PubkeyCache pubkeys = new();
        pubkeys.Build(validators);
        return pubkeys;
    }

    /// <summary>A header for <paramref name="slot"/> claiming <paramref name="proposerIndex"/>, signed by that validator's <see cref="ValidatorKey"/> over <c>DOMAIN_BEACON_PROPOSER</c>.</summary>
    public static SignedBeaconBlockHeader SignedHeader(BeaconStateGloas state, ulong slot, int proposerIndex, Hash256 bodyRoot)
    {
        BeaconBlockHeader header = new()
        {
            Slot = slot,
            ProposerIndex = (ulong)proposerIndex,
            ParentRoot = Hash(0x11),
            StateRoot = Hash(0x12),
            BodyRoot = bodyRoot,
        };
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(header), domain);
        return new SignedBeaconBlockHeader { Message = header, Signature = Sign(ValidatorKey(proposerIndex), signingRoot) };
    }

    /// <summary>Two validly signed, distinct headers for the same slot from <paramref name="proposerIndex"/>.</summary>
    public static ProposerSlashing Equivocation(BeaconStateGloas state, ulong slot, int proposerIndex) => new()
    {
        SignedHeader1 = SignedHeader(state, slot, proposerIndex, Hash(0x21)),
        SignedHeader2 = SignedHeader(state, slot, proposerIndex, Hash(0x22)),
    };

    /// <summary>Attestation data voting for distinct roots derived from <paramref name="fill"/>, with the given slot and source/target epochs.</summary>
    public static AttestationData Vote(ulong slot, ulong sourceEpoch, ulong targetEpoch, byte fill) => new()
    {
        Slot = slot,
        Index = 0,
        BeaconBlockRoot = Hash(fill),
        Source = new Checkpoint { Epoch = sourceEpoch, Root = Hash((byte)(fill + 1)) },
        Target = new Checkpoint { Epoch = targetEpoch, Root = Hash((byte)(fill + 2)) },
    };

    /// <summary>An indexed attestation by <paramref name="validatorIndices"/> over <paramref name="data"/>, aggregate-signed with their <see cref="ValidatorKey"/>s under <c>DOMAIN_BEACON_ATTESTER</c>.</summary>
    public static IndexedAttestationGloas SignedIndexedAttestation(BeaconStateGloas state, AttestationData data, int[] validatorIndices)
    {
        Hash256 domain = state.GetDomain(DomainType.BeaconAttester, data.Target!.Epoch);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain);
        return new IndexedAttestationGloas
        {
            AttestingIndices = [.. validatorIndices.Select(i => (ulong)i)],
            Data = data,
            Signature = AggregateSignature(signingRoot, validatorIndices),
        };
    }

    /// <summary>A voluntary exit for <paramref name="validatorIndex"/>, signed with its <see cref="ValidatorKey"/> over the fork-agnostic Capella exit domain.</summary>
    public static SignedVoluntaryExit SignedExit(BeaconStateGloas state, int validatorIndex, ulong epoch)
    {
        VoluntaryExit exit = new() { Epoch = epoch, ValidatorIndex = (ulong)validatorIndex };
        Hash256 domain = Domains.ComputeDomain(DomainType.VoluntaryExit, Presets.CapellaForkVersion, state.GenesisValidatorsRoot!);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(exit), domain);
        return new SignedVoluntaryExit { Message = exit, Signature = Sign(ValidatorKey(validatorIndex), signingRoot) };
    }

    /// <summary>A BLS-to-execution change for <paramref name="validatorIndex"/> from <paramref name="fromKey"/>'s pubkey, signed by that key over the genesis-version domain.</summary>
    public static SignedBlsToExecutionChange SignedBlsChange(BeaconStateGloas state, int validatorIndex, Bls.SecretKey fromKey, Address toAddress)
    {
        BlsToExecutionChange change = new()
        {
            ValidatorIndex = (ulong)validatorIndex,
            FromBlsPubkey = new BlsPublicKey(new Bls.P1(fromKey).Compress()),
            ToExecutionAddress = toAddress,
        };
        Hash256 domain = Domains.ComputeDomain(DomainType.BlsToExecutionChange, Presets.GenesisForkVersion, state.GenesisValidatorsRoot!);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(change), domain);
        return new SignedBlsToExecutionChange { Message = change, Signature = Sign(fromKey, signingRoot) };
    }

    /// <summary>The 0x00-prefixed withdrawal credentials committing to <paramref name="pubkey"/>.</summary>
    public static Hash256 BlsWithdrawalCredentials(BlsPublicKey pubkey)
    {
        byte[] credentials = System.Security.Cryptography.SHA256.HashData(pubkey.Bytes);
        credentials[0] = Presets.BlsWithdrawalPrefix;
        return new Hash256(credentials);
    }

    /// <summary>The aggregate of each listed validator's signature over <paramref name="signingRoot"/>; a repeated index signs (and so must be aggregated) once per occurrence.</summary>
    public static BlsSignature AggregateSignature(Hash256 signingRoot, IReadOnlyList<int> validatorIndices)
    {
        BlsSigner.Signature aggregate = BlsSigner.Sign(ValidatorKey(validatorIndices[0]), signingRoot.Bytes);
        for (int i = 1; i < validatorIndices.Count; i++)
        {
            aggregate.Aggregate(BlsSigner.Sign(ValidatorKey(validatorIndices[i]), signingRoot.Bytes));
        }
        return new BlsSignature(aggregate.Bytes);
    }

    public static BlsSignature Sign(Bls.SecretKey key, Hash256 signingRoot) =>
        new(BlsSigner.Sign(key, signingRoot.Bytes).Bytes);

    /// <summary>Returns <paramref name="signature"/> with one byte flipped: still 96 well-formed bytes, just not the right ones.</summary>
    public static BlsSignature Corrupt(BlsSignature signature)
    {
        byte[] corrupted = signature.Bytes.ToArray();
        corrupted[10] ^= 0xFF;
        return new BlsSignature(corrupted);
    }

    /// <summary>A builder-signed bid over <c>DOMAIN_BEACON_BUILDER</c>, valid against <paramref name="state"/> as it stands.</summary>
    public static SignedExecutionPayloadBid ValidBuilderBid(BeaconStateGloas state, Bls.SecretKey builderSk, ulong builderIndex, ulong value, byte blockHashFill = 0x88)
    {
        ExecutionPayloadBid message = new()
        {
            ParentBlockHash = state.LatestBlockHash,
            ParentBlockRoot = state.GetBlockRootAtSlot(state.Slot - 1),
            BlockHash = Hash(blockHashFill),
            PrevRandao = state.GetRandaoMix(state.GetCurrentEpoch()),
            FeeRecipient = new Address(BuilderWithdrawalCredentials(0xC0).Bytes[12..]),
            GasLimit = 30_000_000,
            BuilderIndex = builderIndex,
            Slot = state.Slot,
            Value = value,
            ExecutionPayment = 0,
            BlobKzgCommitments = [],
            ExecutionRequestsRoot = SszRoots.HashTreeRoot(new ExecutionRequestsGloas()),
        };
        Hash256 domain = state.GetDomain(DomainType.BeaconBuilder);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(message), domain);
        BlsSignature signature = new(BlsSigner.Sign(builderSk, signingRoot.Bytes).Bytes);
        return new SignedExecutionPayloadBid { Message = message, Signature = signature };
    }

    /// <summary>A zero-value self-build bid (no builder, no signature to verify) at <paramref name="state"/>'s current slot.</summary>
    public static SignedExecutionPayloadBid SelfBuildBid(BeaconStateGloas state, Hash256 parentBlockHash, Hash256 blockHash) => new()
    {
        Message = new ExecutionPayloadBid
        {
            BuilderIndex = Presets.BuilderIndexSelfBuild,
            ParentBlockHash = parentBlockHash,
            ParentBlockRoot = state.GetBlockRootAtSlot(state.Slot - 1),
            BlockHash = blockHash,
            PrevRandao = state.GetRandaoMix(state.GetCurrentEpoch()),
            FeeRecipient = Address.Zero,
            GasLimit = 30_000_000,
            Slot = state.Slot,
            Value = 0,
            BlobKzgCommitments = [],
            ExecutionRequestsRoot = SszRoots.HashTreeRoot(new ExecutionRequestsGloas()),
        },
        Signature = new BlsSignature(G2PointAtInfinity()),
    };

    /// <summary>
    /// The root of the block whose post-state is <paramref name="state"/>: the latest block header
    /// completed with the state root the next <c>process_slot</c> would write into it.
    /// </summary>
    public static Hash256 BlockRootOf(BeaconStateGloas state) => SszRoots.HashTreeRoot(new BeaconBlockHeader
    {
        Slot = state.LatestBlockHeader!.Slot,
        ProposerIndex = state.LatestBlockHeader.ProposerIndex,
        ParentRoot = state.LatestBlockHeader.ParentRoot,
        StateRoot = SszRoots.HashTreeRoot(state),
        BodyRoot = state.LatestBlockHeader.BodyRoot,
    });

    /// <summary>A builder-signed envelope for the block whose post-state is <paramref name="state"/>, consistent with <paramref name="bid"/>.</summary>
    public static SignedExecutionPayloadEnvelope ValidEnvelope(BeaconStateGloas state, ExecutionPayloadBid bid, Bls.SecretKey builderSk, ulong builderIndex)
    {
        ExecutionPayloadEnvelope message = new()
        {
            Payload = new ExecutionPayloadGloas
            {
                ParentHash = state.LatestBlockHash,
                FeeRecipient = bid.FeeRecipient,
                PrevRandao = bid.PrevRandao,
                GasLimit = bid.GasLimit,
                BlockHash = bid.BlockHash,
                SlotNumber = state.Slot,
                Timestamp = state.ComputeTimeAtSlot(state.Slot),
                Transactions = [],
                Withdrawals = state.PayloadExpectedWithdrawals ?? [],
                ExtraData = [],
                BaseFeePerGas = 0,
                BlockAccessList = [],
            },
            ExecutionRequests = new ExecutionRequestsGloas(),
            BuilderIndex = builderIndex,
            BeaconBlockRoot = BlockRootOf(state),
            ParentBeaconBlockRoot = state.LatestBlockHeader!.ParentRoot,
        };
        Hash256 domain = state.GetDomain(DomainType.BeaconBuilder);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(message), domain);
        BlsSignature signature = new(BlsSigner.Sign(builderSk, signingRoot.Bytes).Bytes);
        return new SignedExecutionPayloadEnvelope { Message = message, Signature = signature };
    }

    /// <summary>A block carrying <paramref name="bid"/> and otherwise-empty operations, at <paramref name="state"/>'s current slot.</summary>
    public static SignedBeaconBlockGloas MinimalBlock(BeaconStateGloas state, SignedExecutionPayloadBid bid) => new()
    {
        Message = new BeaconBlockGloas
        {
            Slot = state.Slot,
            ProposerIndex = state.GetBeaconProposerIndex(),
            ParentRoot = SszRoots.HashTreeRoot(state.LatestBlockHeader!),
            StateRoot = Hash256.Zero,
            Body = new BeaconBlockBodyGloas
            {
                RandaoReveal = default,
                Eth1Data = state.Eth1Data,
                Graffiti = Hash256.Zero,
                ProposerSlashings = [],
                AttesterSlashings = [],
                Attestations = [],
                Deposits = [],
                VoluntaryExits = [],
                SyncAggregate = new SyncAggregate { SyncCommitteeBits = new BitArray(Presets.SyncCommitteeSize), SyncCommitteeSignature = new BlsSignature(G2PointAtInfinity()) },
                BlsToExecutionChanges = [],
                SignedExecutionPayloadBid = bid,
                PayloadAttestations = [],
                ParentExecutionRequests = new ExecutionRequestsGloas(),
            },
        },
        Signature = default,
    };

    /// <summary>Applies <paramref name="block"/> to <paramref name="state"/> through the real pipeline, signatures skipped (the fixtures' RANDAO reveal is unsigned).</summary>
    public static void ApplyBlock(BeaconStateGloas state, SignedBeaconBlockGloas block, EpochCache cache) =>
        GloasBlockProcessing.ProcessBlock(state, block.Message!, cache, new PubkeyCache(), new AcceptingNotifier(), SyntheticSpec(), verifySignatures: false);

    /// <summary>A queued top-up for the validator already at <paramref name="validatorIndex"/>; a known pubkey is credited without a signature check.</summary>
    public static PendingDeposit TopUpDeposit(BeaconStateGloas state, int validatorIndex, ulong amount, ulong slot) => new()
    {
        Pubkey = state.Validators![validatorIndex].Pubkey,
        WithdrawalCredentials = Hash256.Zero,
        Amount = amount,
        Signature = default,
        Slot = slot,
    };

    /// <summary>
    /// A queued deposit for a pubkey not in the registry (the key <see cref="DeriveKey"/> derives for
    /// <paramref name="keyIndex"/>), signed over the genesis deposit domain by that key unless
    /// <paramref name="signerKeyIndex"/> names another one.
    /// </summary>
    public static PendingDeposit NewValidatorDeposit(int keyIndex, ulong amount, ulong slot, int? signerKeyIndex = null)
    {
        BlsPublicKey pubkey = new(new Bls.P1(DeriveKey(keyIndex)).Compress());
        Hash256 withdrawalCredentials = EthWithdrawalCredentials(0xEE);
        DepositMessage.Merkleize(new DepositMessage { Pubkey = pubkey, WithdrawalCredentials = withdrawalCredentials, Amount = amount }, out UInt256 root);
        Hash256 domain = Domains.ComputeDomain(DomainType.Deposit, Presets.GenesisForkVersion, Hash256.Zero);
        Hash256 signingRoot = Domains.ComputeSigningRoot(new Hash256(root.ToLittleEndian()), domain);
        BlsSignature signature = new(BlsSigner.Sign(DeriveKey(signerKeyIndex ?? keyIndex), signingRoot.Bytes).Bytes);
        return new PendingDeposit { Pubkey = pubkey, WithdrawalCredentials = withdrawalCredentials, Amount = amount, Signature = signature, Slot = slot };
    }

    public static Hash256 EthWithdrawalCredentials(byte fill)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill(fill);
        bytes[0] = Presets.EthWithdrawalPrefix;
        return new Hash256(bytes);
    }

    public static Hash256 BuilderWithdrawalCredentials(byte fill)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill(fill);
        bytes[0] = Presets.BuilderWithdrawalPrefix;
        return new Hash256(bytes);
    }

    public static Bls.SecretKey DeriveKey(int index) => new(new Bls.SecretKey(MasterSkBytes, Bls.ByteOrder.LittleEndian), unchecked((uint)index));

    public static Hash256 Hash(byte value)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill(value);
        return new Hash256(bytes);
    }

    public static BlsPublicKey Pubkey(byte value)
    {
        byte[] bytes = new byte[BlsPublicKey.Length];
        bytes.AsSpan().Fill(value);
        return new BlsPublicKey(bytes);
    }

    /// <summary>The compressed BLS G2 point at infinity - duplicated as bytes here rather than reaching into <c>Crypto.SignatureSets</c>'s internal constant from a test assembly.</summary>
    public static byte[] G2PointAtInfinity()
    {
        byte[] bytes = new byte[BlsSignature.Length];
        bytes[0] = 0xc0;
        return bytes;
    }

    public sealed class AcceptingNotifier : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
        public ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests) => ExecutionStatus.Valid;
    }

    /// <summary>The spec store's <c>block_states</c> as a plain dictionary: exactly the roots a test says exist, nothing else.</summary>
    public sealed class BlockStates : IGloasBlockStateProvider
    {
        private readonly Dictionary<Hash256, BeaconStateGloas> _states = [];

        /// <summary>Freezes <paramref name="state"/> as the post-state of the block it was produced by.</summary>
        public BlockStates Add(BeaconStateGloas state)
        {
            _states.Add(BlockRootOf(state), state);
            return this;
        }

        public BeaconStateGloas? GetGloasBlockState(Hash256 blockRoot) => _states.GetValueOrDefault(blockRoot);
    }
}
