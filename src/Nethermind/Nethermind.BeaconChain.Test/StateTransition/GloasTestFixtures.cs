// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.StateTransition;

internal static class GloasTestFixtures
{
    public const ulong Gwei = 1_000_000_000;

    // PTC sampling needs a nonempty committee at every slot.
    public const int ValidatorCount = 2048;

    // Keeps validator keys clear of the builder key (200) and the deposit keys tests derive (300+).
    private const int ValidatorKeyOffset = 1000;

    public static readonly ulong BoundarySlot = Presets.SlotsPerEpoch;

    private static readonly byte[] MasterSkBytes = Bytes.FromHexString("0x2cd4ba406b522459d57a0bed51a397435c0bb11dd5f3ca1152b3694bb91d7c22");
    private static readonly byte[] GloasVersion = Bytes.FromHexString("0x07000000");

    private const int BuilderKeyIndex = 200;
    private const ulong BuilderStartingBalance = 40 * Gwei;

    // Built once per process and only ever handed out through FixtureCopier, so no test can reach another's state.
    private static readonly ConcurrentDictionary<int, Lazy<BeaconStateFulu>> FuluStatesAtBoundary = new();
    private static readonly Lazy<BeaconStateGloas> GloasStateAtBoundary = new(BuildGloasState);
    private static readonly ConcurrentDictionary<int, byte[]> DerivedKeys = new();
    private static readonly ConcurrentDictionary<int, BlsPublicKey> ValidatorPubkeys = new();

    public static BeaconChainSpec UpgradeEpochSpec() => SyntheticSpec(BoundarySlot / Presets.SlotsPerEpoch);

    public static BeaconChainSpec SyntheticSpec(ulong gloasForkEpoch = 0, byte[]? gloasForkVersion = null) => new()
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
        GloasForkVersion = gloasForkVersion ?? GloasVersion,
        Bootnodes = [],
    };

    public static BeaconStateGloas CreateGloasState(out Bls.SecretKey builderSk, out ulong builderStartingBalance)
    {
        builderSk = DeriveKey(BuilderKeyIndex);
        builderStartingBalance = BuilderStartingBalance;
        return FixtureCopier.Copy(GloasStateAtBoundary.Value);
    }

    private static BeaconStateGloas BuildGloasState()
    {
        BeaconStateGloas state = GloasForkTransition.UpgradeToGloas(CreateFuluStateAtBoundary(ValidatorCount), SyntheticSpec());

        BlsPublicKey builderPubkey = new(new Bls.P1(DeriveKey(BuilderKeyIndex)).Compress());
        state.Builders = [new Builder
        {
            Pubkey = builderPubkey,
            Version = Presets.PayloadBuilderVersion,
            ExecutionAddress = new Address(BuilderWithdrawalCredentials(0xC0).Bytes[12..]),
            Balance = BuilderStartingBalance,
            DepositEpoch = 0,
            WithdrawableEpoch = Presets.FarFutureEpoch,
        }];

        return state;
    }

    public static BeaconStateFulu CreateFuluStateAtBoundary(int validatorCount) =>
        FixtureCopier.Copy(FuluStatesAtBoundary.GetOrAdd(validatorCount, static count => new Lazy<BeaconStateFulu>(() => BuildFuluStateAtBoundary(count))).Value);

    private static BeaconStateFulu BuildFuluStateAtBoundary(int validatorCount)
    {
        BeaconStateFulu state = CreateFuluState(validatorCount);
        SlotProcessing.ProcessSlots(state, BoundarySlot, new EpochCache());
        return state;
    }

    internal static BeaconStateFulu CreateMinimalFuluState(int validatorCount, Hash256 randaoMix, ulong effectiveBalance = 32 * Gwei, int inactiveEvery = 0)
    {
        Hash256[] randaoMixes = Enumerable.Repeat(randaoMix, (int)Presets.EpochsPerHistoricalVector).ToArray();

        Validator[] validators = new Validator[validatorCount];
        ulong[] balances = new ulong[validatorCount];
        for (int i = 0; i < validatorCount; i++)
        {
            bool inactive = inactiveEvery > 0 && i % inactiveEvery == 0;
            validators[i] = CreateActiveValidator(default);
            validators[i].EffectiveBalance = effectiveBalance;
            validators[i].ActivationEpoch = inactive ? Presets.FarFutureEpoch : 0;
            balances[i] = effectiveBalance;
        }

        return new BeaconStateFulu
        {
            Slot = 0,
            Validators = validators,
            Balances = balances,
            RandaoMixes = randaoMixes,
            Slashings = new ulong[(int)Presets.EpochsPerSlashingsVector],
            ProposerLookahead = new ulong[(int)Presets.ProposerLookaheadSlots],
            FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
        };
    }

    public static BeaconStateFulu CreateFuluState(int validatorCount)
    {
        Hash256[] randaoMixes = Enumerable.Repeat(Hash(0x42), (int)Presets.EpochsPerHistoricalVector).ToArray();
        Hash256[] blockRoots = Enumerable.Repeat(Hash256.Zero, (int)Presets.SlotsPerHistoricalRoot).ToArray();
        Hash256[] stateRoots = Enumerable.Repeat(Hash256.Zero, (int)Presets.SlotsPerHistoricalRoot).ToArray();

        Validator[] validators = new Validator[validatorCount];
        ulong[] balances = new ulong[validatorCount];
        for (int i = 0; i < validatorCount; i++)
        {
            validators[i] = CreateActiveValidator(Pubkey((byte)(0x50 + i)));
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
            // Builder activity requires finalized epoch strictly after deposit epoch 0.
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

    public static Validator CreateActiveValidator(BlsPublicKey pubkey) => new()
    {
        Pubkey = pubkey,
        WithdrawalCredentials = Hash256.Zero,
        EffectiveBalance = 32 * Gwei,
        ActivationEpoch = 0,
        ExitEpoch = Presets.FarFutureEpoch,
        WithdrawableEpoch = Presets.FarFutureEpoch,
        ActivationEligibilityEpoch = 0,
    };

    private static BlsPublicKey[] FillCommittee(BlsPublicKey pubkey) => Enumerable.Repeat(pubkey, Presets.SyncCommitteeSize).ToArray();

    public static Bls.SecretKey ValidatorKey(int validatorIndex) => DeriveKey(ValidatorKeyOffset + validatorIndex);

    public static PubkeyCache InstallRealValidatorKeys(BeaconStateGloas state)
    {
        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            Validator updated = validators[i].Clone();
            updated.Pubkey = ValidatorPubkeys.GetOrAdd(i, static index => new BlsPublicKey(new Bls.P1(ValidatorKey(index)).Compress()));
            validators[i] = updated;
        }
        state.CurrentSyncCommittee = new SyncCommittee { Pubkeys = FillCommittee(validators[0].Pubkey), AggregatePubkey = Pubkey(0x60) };
        state.NextSyncCommittee = new SyncCommittee { Pubkeys = FillCommittee(validators[0].Pubkey), AggregatePubkey = Pubkey(0x61) };

        PubkeyCache pubkeys = new();
        pubkeys.Build(validators);
        return pubkeys;
    }

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

    public static ProposerSlashing Equivocation(BeaconStateGloas state, ulong slot, int proposerIndex) => new()
    {
        SignedHeader1 = SignedHeader(state, slot, proposerIndex, Hash(0x21)),
        SignedHeader2 = SignedHeader(state, slot, proposerIndex, Hash(0x22)),
    };

    public static AttestationData Vote(ulong slot, ulong sourceEpoch, ulong targetEpoch, byte fill) => new()
    {
        Slot = slot,
        Index = 0,
        BeaconBlockRoot = Hash(fill),
        Source = new Checkpoint { Epoch = sourceEpoch, Root = Hash((byte)(fill + 1)) },
        Target = new Checkpoint { Epoch = targetEpoch, Root = Hash((byte)(fill + 2)) },
    };

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

    public static SignedVoluntaryExit SignedExit(BeaconStateGloas state, int validatorIndex, ulong epoch)
    {
        VoluntaryExit exit = new() { Epoch = epoch, ValidatorIndex = (ulong)validatorIndex };
        Hash256 domain = Domains.ComputeDomain(DomainType.VoluntaryExit, BeaconChainSpec.Mainnet.CapellaForkVersion, state.GenesisValidatorsRoot!);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(exit), domain);
        return new SignedVoluntaryExit { Message = exit, Signature = Sign(ValidatorKey(validatorIndex), signingRoot) };
    }

    public static SignedBlsToExecutionChange SignedBlsChange(BeaconStateGloas state, int validatorIndex, Bls.SecretKey fromKey, Address toAddress)
    {
        BlsToExecutionChange change = new()
        {
            ValidatorIndex = (ulong)validatorIndex,
            FromBlsPubkey = new BlsPublicKey(new Bls.P1(fromKey).Compress()),
            ToExecutionAddress = toAddress,
        };
        Hash256 domain = Domains.ComputeDomain(DomainType.BlsToExecutionChange, BeaconChainSpec.Mainnet.GenesisForkVersion, state.GenesisValidatorsRoot!);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(change), domain);
        return new SignedBlsToExecutionChange { Message = change, Signature = Sign(fromKey, signingRoot) };
    }

    public static Hash256 BlsWithdrawalCredentials(BlsPublicKey pubkey)
    {
        byte[] credentials = System.Security.Cryptography.SHA256.HashData(pubkey.Bytes);
        credentials[0] = Presets.BlsWithdrawalPrefix;
        return new Hash256(credentials);
    }

    public static AttestationGloas CommitteeAttestation(BeaconStateGloas state, AttestationData data, CommitteeCache committees, int committeeIndex, bool sign)
    {
        int[] committee = committees.GetBeaconCommittee(data.Slot, committeeIndex).ToArray();
        BitArray committeeBits = new(Presets.MaxCommitteesPerSlot);
        committeeBits[committeeIndex] = true;

        BlsSignature signature = default;
        if (sign)
        {
            Hash256 domain = state.GetDomain(DomainType.BeaconAttester, data.Target!.Epoch);
            signature = AggregateSignature(Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain), committee);
        }

        return new AttestationGloas
        {
            AggregationBits = new BitArray(committee.Length, true),
            Data = data,
            Signature = signature,
            CommitteeBits = committeeBits,
        };
    }

    public static Attestation ToFuluAttestation(AttestationGloas attestation) => new()
    {
        AggregationBits = attestation.AggregationBits,
        Data = attestation.Data,
        Signature = attestation.Signature,
        CommitteeBits = attestation.CommitteeBits,
    };

    public static AttestationData VoteFor(BeaconStateGloas state, ulong slot, ulong targetEpoch, Hash256 headRoot, ulong index = 0) => new()
    {
        Slot = slot,
        Index = index,
        BeaconBlockRoot = headRoot,
        Source = targetEpoch == state.GetCurrentEpoch() ? state.CurrentJustifiedCheckpoint : state.PreviousJustifiedCheckpoint,
        Target = new Checkpoint { Epoch = targetEpoch, Root = state.GetBlockRoot(targetEpoch) },
    };

    // Repeated PTC members sign once per seat, not once per distinct validator.
    public static PayloadAttestation PtcAttestation(BeaconStateGloas state, PayloadAttestationData data, int[] positions, bool sign)
    {
        BitArray bits = new((int)Presets.PtcSize);
        foreach (int position in positions)
        {
            bits[position] = true;
        }

        BlsSignature signature = default;
        if (sign)
        {
            ulong[] ptc = state.GetPtc(data.Slot, UpgradeEpochSpec()).Indices!;
            Hash256 domain = state.GetDomain(DomainType.PtcAttester, BeaconStateAccessors.ComputeEpochAtSlot(data.Slot));
            signature = AggregateSignature(Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain), [.. positions.Select(p => (int)ptc[p])]);
        }

        return new PayloadAttestation { AggregationBits = bits, Data = data, Signature = signature };
    }

    // Repeated indices contribute a signature once per occurrence.
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

    public static BlsSignature Corrupt(BlsSignature signature)
    {
        byte[] corrupted = signature.Bytes.ToArray();
        corrupted[10] ^= 0xFF;
        return new BlsSignature(corrupted);
    }

    public static void AssertRefusedWithoutMutation(BeaconStateGloas state, Action process, string expectedMessage, string mutationMessage)
    {
        Hash256 rootBefore = SszRoots.HashTreeRoot(state);
        BeaconStateException exception = Assert.Throws<BeaconStateException>(() => process())!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
        Assert.That(SszRoots.HashTreeRoot(state), Is.EqualTo(rootBefore), mutationMessage);
    }

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

    public static Hash256 BlockRootOf(BeaconStateGloas state) => SszRoots.HashTreeRoot(new BeaconBlockHeader
    {
        Slot = state.LatestBlockHeader!.Slot,
        ProposerIndex = state.LatestBlockHeader.ProposerIndex,
        ParentRoot = state.LatestBlockHeader.ParentRoot,
        StateRoot = SszRoots.HashTreeRoot(state),
        BodyRoot = state.LatestBlockHeader.BodyRoot,
    });

    public static SignedExecutionPayloadEnvelope ValidEnvelope(BeaconStateGloas state, ExecutionPayloadBid bid, Bls.SecretKey builderSk, ulong builderIndex, Hash256? blockRoot = null)
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
            BeaconBlockRoot = blockRoot ?? BlockRootOf(state),
            ParentBeaconBlockRoot = state.LatestBlockHeader!.ParentRoot,
        };
        Hash256 domain = state.GetDomain(DomainType.BeaconBuilder);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(message), domain);
        BlsSignature signature = new(BlsSigner.Sign(builderSk, signingRoot.Bytes).Bytes);
        return new SignedExecutionPayloadEnvelope { Message = message, Signature = signature };
    }

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

    // Fixture RANDAO reveals are unsigned; ApplyBlock deliberately skips signatures.
    public static void ApplyBlock(BeaconStateGloas state, SignedBeaconBlockGloas block, EpochCache cache) =>
        GloasBlockProcessing.ProcessBlock(state, block.Message!, cache, new PubkeyCache(), new AcceptingNotifier(), UpgradeEpochSpec(), verifySignatures: false);

    public static PendingDeposit TopUpDeposit(BeaconStateGloas state, int validatorIndex, ulong amount, ulong slot) => new()
    {
        Pubkey = state.Validators![validatorIndex].Pubkey,
        WithdrawalCredentials = Hash256.Zero,
        Amount = amount,
        Signature = default,
        Slot = slot,
    };

    public static PendingDeposit NewValidatorDeposit(int keyIndex, ulong amount, ulong slot, int? signerKeyIndex = null)
    {
        Hash256 withdrawalCredentials = EthWithdrawalCredentials(0xEE);
        (BlsPublicKey pubkey, BlsSignature signature) = SignDeposit(DeriveKey(keyIndex), withdrawalCredentials, amount, signerKeyIndex);
        return new PendingDeposit { Pubkey = pubkey, WithdrawalCredentials = withdrawalCredentials, Amount = amount, Signature = signature, Slot = slot };
    }

    public static (BlsPublicKey Pubkey, BlsSignature Signature) SignDeposit(Bls.SecretKey key, Hash256 withdrawalCredentials, ulong amount, int? signerKeyIndex = null)
    {
        BlsPublicKey pubkey = new(new Bls.P1(key).Compress());
        DepositMessage.Merkleize(new DepositMessage { Pubkey = pubkey, WithdrawalCredentials = withdrawalCredentials, Amount = amount }, out UInt256 root);
        Hash256 domain = Domains.ComputeDomain(DomainType.Deposit, BeaconChainSpec.Mainnet.GenesisForkVersion, Hash256.Zero);
        Hash256 signingRoot = Domains.ComputeSigningRoot(new Hash256(root.ToLittleEndian()), domain);
        return (pubkey, Sign(signerKeyIndex is int index ? DeriveKey(index) : key, signingRoot));
    }

    public static Hash256 EthWithdrawalCredentials(byte fill)
    {
        byte[] bytes = Enumerable.Repeat(fill, 32).ToArray();
        bytes[0] = Presets.EthWithdrawalPrefix;
        return new Hash256(bytes);
    }

    public static Hash256 BuilderWithdrawalCredentials(byte fill)
    {
        byte[] bytes = Enumerable.Repeat(fill, 32).ToArray();
        bytes[0] = Presets.BuilderWithdrawalPrefix;
        return new Hash256(bytes);
    }

    public static Bls.SecretKey DeriveKey(int index) =>
        new(DerivedKeys.GetOrAdd(index, static i => new Bls.SecretKey(new Bls.SecretKey(MasterSkBytes, Bls.ByteOrder.LittleEndian), unchecked((uint)i)).ToLendian()), Bls.ByteOrder.LittleEndian);

    public static Hash256 Hash(byte value) => new(Enumerable.Repeat(value, 32).ToArray());

    public static BlsPublicKey Pubkey(byte value) => new(Enumerable.Repeat(value, BlsPublicKey.Length).ToArray());

    public static byte[] G1PointAtInfinity()
    {
        byte[] bytes = new byte[BlsPublicKey.Length];
        bytes[0] = 0xc0;
        return bytes;
    }

    public static byte[] G2PointAtInfinity()
    {
        byte[] bytes = new byte[BlsSignature.Length];
        bytes[0] = 0xc0;
        return bytes;
    }

    // Tests mutate element objects in place, so this copier must deep-copy beyond production GloasStateClone; preserve graph aliases and nulls.
    private static class FixtureCopier
    {
        private static readonly Func<object, object> ShallowCopy =
            typeof(object).GetMethod(nameof(MemberwiseClone), BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<object, object>>();

        private static readonly ConcurrentDictionary<Type, bool> Leaves = new();
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> MutableFields = new();

        public static T Copy<T>(T value) where T : class =>
            (T)Copy(value, new Dictionary<object, object>(ReferenceEqualityComparer.Instance))!;

        private static object? Copy(object? value, Dictionary<object, object> copies)
        {
            if (value is null || IsLeaf(value.GetType()))
                return value;
            if (copies.TryGetValue(value, out object? existing))
                return existing;

            switch (value)
            {
                case BitArray bits:
                    BitArray bitsCopy = new(bits);
                    copies.Add(value, bitsCopy);
                    return bitsCopy;
                case Array array:
                    Array arrayCopy = (Array)array.Clone();
                    copies.Add(value, arrayCopy);
                    if (!IsLeaf(array.GetType().GetElementType()!))
                    {
                        for (int i = 0; i < array.Length; i++)
                            arrayCopy.SetValue(Copy(array.GetValue(i), copies), i);
                    }
                    return arrayCopy;
            }

            Type type = value.GetType();
            if (type.IsValueType)
                throw new NotSupportedException($"{type} holds a reference that a copy would share");

            object copy = ShallowCopy(value);
            copies.Add(value, copy);
            foreach (FieldInfo field in MutableFields.GetOrAdd(type, FindMutableFields))
                field.SetValue(copy, Copy(field.GetValue(value), copies));
            return copy;
        }

        private static FieldInfo[] FindMutableFields(Type type)
        {
            List<FieldInfo> fields = [];
            for (Type? t = type; t is not null; t = t.BaseType)
            {
                foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (IsLeaf(field.FieldType))
                        continue;
                    if (field.FieldType.IsValueType)
                        throw new NotSupportedException($"{type}.{field.Name} is a {field.FieldType} holding a reference that a copy would share");
                    fields.Add(field);
                }
            }
            return [.. fields];
        }

        // Hash256 and Address are immutable by convention; a struct is a leaf only if all its fields are.
        private static bool IsLeaf(Type type) => Leaves.GetOrAdd(type, static t =>
            t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Hash256) || t == typeof(Address) ||
            t.IsValueType && t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).All(static f => IsLeaf(f.FieldType)));
    }

    public sealed class AcceptingNotifier : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
        public ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests) => ExecutionStatus.Valid;
    }

    public sealed class BlockStates : IGloasBlockStateProvider
    {
        private readonly Dictionary<Hash256, BeaconStateGloas> _states = [];

        public BlockStates Add(BeaconStateGloas state)
        {
            _states.Add(BlockRootOf(state), state);
            return this;
        }

        public BeaconStateGloas? GetGloasBlockState(Hash256 blockRoot) => _states.GetValueOrDefault(blockRoot);
    }
}
