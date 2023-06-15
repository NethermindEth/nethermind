// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Merkleization
{
    //  public partial class Merkle
    // {
    //     public static int DepositContractTreeDepth { get; private set; }
    //     private static int JustificationBitsLength;
    //     internal static ulong MaximumDepositContracts { get; private set; }
    //
    //     internal static uint MaxValidatorsPerCommittee { get; private set; }
    //
    //     internal static uint SlotsPerEpoch { get; private set; }
    //     internal static int SlotsPerEth1VotingPeriod { get; private set; }
    //     public static int SlotsPerHistoricalRoot { get; private set; }
    //
    //     public static int EpochsPerHistoricalVector { get; private set; }
    //     public static int EpochsPerSlashingsVector { get; private set; }
    //     internal static ulong HistoricalRootsLimit { get; private set; }
    //     internal static ulong ValidatorRegistryLimit { get; private set; }
    //
    //     internal static uint MaxProposerSlashings { get; private set; }
    //     internal static uint MaxAttesterSlashings { get; private set; }
    //     internal static uint MaxAttestations { get; private set; }
    //     internal static uint MaxDeposits { get; private set; }
    //     internal static uint MaxVoluntaryExits { get; private set; }
    //
    //     public static void Init(int depositContractTreeDepth,
    //         int justificationBitsLength,
    //         ulong maximumValidatorsPerCommittee,
    //         ulong slotsPerEpoch,
    //         ulong slotsPerEth1VotingPeriod,
    //         ulong slotsPerHistoricalRoot,
    //         ulong epochsPerHistoricalVector,
    //         ulong epochsPerSlashingsVector,
    //         ulong historicalRootsLimit,
    //         ulong validatorRegistryLimit,
    //         ulong maximumProposerSlashings,
    //         ulong maximumAttesterSlashings,
    //         ulong maximumAttestations,
    //         ulong maximumDeposits,
    //         ulong maximumVoluntaryExits
    //     )
    //     {
    //         DepositContractTreeDepth = depositContractTreeDepth;
    //         JustificationBitsLength = justificationBitsLength;
    //         MaxValidatorsPerCommittee = (uint)maximumValidatorsPerCommittee;
    //         SlotsPerEpoch = (uint)slotsPerEpoch;
    //         SlotsPerEth1VotingPeriod = (int)slotsPerEth1VotingPeriod;
    //         SlotsPerHistoricalRoot = (int)slotsPerHistoricalRoot;
    //         EpochsPerHistoricalVector = (int)epochsPerHistoricalVector;
    //         EpochsPerSlashingsVector = (int)epochsPerSlashingsVector;
    //         HistoricalRootsLimit = historicalRootsLimit;
    //         ValidatorRegistryLimit = validatorRegistryLimit;
    //         MaxProposerSlashings = (uint)maximumProposerSlashings;
    //         MaxAttesterSlashings = (uint)maximumAttesterSlashings;
    //         MaxAttestations = (uint)maximumAttestations;
    //         MaxDeposits = (uint)maximumDeposits;
    //         MaxVoluntaryExits = (uint)maximumVoluntaryExits;
    //
    //         MaximumDepositContracts = (ulong) 1 << depositContractTreeDepth;
    //     }
    // }

    public static partial class Merkle
    {


        //public static void Ize(out UInt256 root, BlsPublicKey container)
        //{
        //    Ize(out root, container.Bytes);
        //}

        //public static void Ize(out UInt256 root, BlsSignature container)
        //{
        //    Ize(out root, container.Bytes);
        //}

        //public static void Ize(out UInt256 root, Gwei container)
        //{
        //    Ize(out root, container.Amount);
        //}

        //public static void Ize(out UInt256 root, Slot container)
        //{
        //    Ize(out root, container.Number);
        //}

        //public static void Ize(out UInt256 root, Epoch container)
        //{
        //    Ize(out root, container.Number);
        //}

        //public static void Ize(out UInt256 root, ValidatorIndex container)
        //{
        //    Ize(out root, container.Number);
        //}

        //public static void Ize(out UInt256 root, CommitteeIndex container)
        //{
        //    Ize(out root, container.Number);
        //}

        //public static void Ize(out UInt256 root, Eth1Data? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(2);
        //    merkleizer.Feed(container.DepositRoot);
        //    merkleizer.Feed(container.DepositCount);
        //    merkleizer.Feed(container.BlockHash);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, DepositMessage container)
        //{
        //    Merkleizer merkleizer = new Merkleizer(2);
        //    merkleizer.Feed(container.PublicKey);
        //    merkleizer.Feed(container.WithdrawalCredentials);
        //    merkleizer.Feed(container.Amount);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, DepositData container)
        //{
        //    Merkleizer merkleizer = new Merkleizer(2);
        //    merkleizer.Feed(container.PublicKey);
        //    merkleizer.Feed(container.WithdrawalCredentials);
        //    merkleizer.Feed(container.Amount);
        //    merkleizer.Feed(container.Signature);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, Ref<DepositData> container)
        //{
        //    if (container.Root is null)
        //    {
        //        Ize(out root, container.Item);
        //        container.Root = new Root(root);
        //    }
        //    else
        //    {
        //        container.Root.AsInt(out root);
        //    }
        //}

        //public static void Ize(out UInt256 root, List<Ref<DepositData>> value)
        //{
        //    Merkleizer merkleizer = new Merkleizer(0);
        //    merkleizer.Feed(value, Ssz.Ssz.MaximumDepositContracts);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, List<DepositData> value)
        //{
        //    Merkleizer merkleizer = new Merkleizer(0);
        //    merkleizer.Feed(value, Ssz.Ssz.MaximumDepositContracts);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, AttestationData? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(3);
        //    merkleizer.Feed(container.Slot);
        //    merkleizer.Feed(container.Index);
        //    merkleizer.Feed(container.BeaconBlockRoot);
        //    merkleizer.Feed(container.Source);
        //    merkleizer.Feed(container.Target);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, BeaconBlockBody? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(3);
        //    merkleizer.Feed(container.RandaoReveal);
        //    merkleizer.Feed(container.Eth1Data);
        //    merkleizer.Feed(container.Graffiti);
        //    merkleizer.Feed(container.ProposerSlashings, Ssz.Ssz.MaxProposerSlashings);
        //    merkleizer.Feed(container.AttesterSlashings, Ssz.Ssz.MaxAttesterSlashings);
        //    merkleizer.Feed(container.Attestations, Ssz.Ssz.MaxAttestations);
        //    merkleizer.Feed(container.Deposits, Ssz.Ssz.MaxDeposits);
        //    merkleizer.Feed(container.VoluntaryExits, Ssz.Ssz.MaxVoluntaryExits);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, BeaconState? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(5);
        //    merkleizer.Feed(container.GenesisTime);
        //    merkleizer.Feed(container.Slot);
        //    merkleizer.Feed(container.Fork);
        //    merkleizer.Feed(container.LatestBlockHeader);
        //    merkleizer.Feed(container.BlockRoots);
        //    merkleizer.Feed(container.StateRoots);
        //    merkleizer.Feed(container.HistoricalRoots.ToArray(), Ssz.Ssz.HistoricalRootsLimit);
        //    merkleizer.Feed(container.Eth1Data);
        //    merkleizer.Feed(container.Eth1DataVotes.ToArray(), (uint)Ssz.Ssz.SlotsPerEth1VotingPeriod);
        //    merkleizer.Feed(container.Eth1DepositIndex);
        //    merkleizer.Feed(container.Validators, Ssz.Ssz.ValidatorRegistryLimit);
        //    merkleizer.Feed(container.Balances.ToArray().ToArray());
        //    merkleizer.Feed(container.PreviousEpochAttestations, Ssz.Ssz.MaxAttestations * Ssz.Ssz.SlotsPerEpoch);
        //    merkleizer.Feed(container.CurrentEpochAttestations, Ssz.Ssz.MaxAttestations * Ssz.Ssz.SlotsPerEpoch);
        //    merkleizer.FeedBitvector(container.JustificationBits);
        //    merkleizer.Feed(container.PreviousJustifiedCheckpoint);
        //    merkleizer.Feed(container.CurrentJustifiedCheckpoint);
        //    merkleizer.Feed(container.FinalizedCheckpoint);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, BeaconBlock container)
        //{
        //    Merkleizer merkleizer = new Merkleizer(2);
        //    merkleizer.Feed(container.Slot);
        //    merkleizer.Feed(container.ParentRoot);
        //    merkleizer.Feed(container.StateRoot);
        //    merkleizer.Feed(container.Body);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, Attestation? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(2);
        //    merkleizer.FeedBitlist(container.AggregationBits, Ssz.Ssz.MaxValidatorsPerCommittee);
        //    merkleizer.Feed(container.Data);
        //    merkleizer.Feed(container.Signature);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, IndexedAttestation? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(2);
        //    merkleizer.Feed(container.AttestingIndices.ToArray(), Ssz.Ssz.MaxValidatorsPerCommittee);
        //    merkleizer.Feed(container.Data);
        //    merkleizer.Feed(container.Signature);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, PendingAttestation? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(2);
        //    merkleizer.FeedBitlist(container.AggregationBits, Ssz.Ssz.MaxValidatorsPerCommittee);
        //    merkleizer.Feed(container.Data);
        //    merkleizer.Feed(container.InclusionDelay);
        //    merkleizer.Feed(container.ProposerIndex);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, AttesterSlashing? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(1);
        //    merkleizer.Feed(container.Attestation1);
        //    merkleizer.Feed(container.Attestation2);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, Deposit? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(1);
        //    merkleizer.Feed(container.Proof);
        //    merkleizer.Feed(container.Data);
        //    merkleizer.CalculateRoot(out root);
        //}

        private static UInt256 RootOfNull;

        //public static void Ize(out UInt256 root, ProposerSlashing container)
        //{
        //    Merkleizer merkleizer = new Merkleizer(2);
        //    merkleizer.Feed(container.ProposerIndex);
        //    merkleizer.Feed(container.SignedHeader1);
        //    merkleizer.Feed(container.SignedHeader2);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, Fork? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(2);
        //    merkleizer.Feed(container.Value.PreviousVersion);
        //    merkleizer.Feed(container.Value.CurrentVersion);
        //    merkleizer.Feed(container.Value.Epoch);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, Checkpoint? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(1);
        //    merkleizer.Feed(container.Value.Epoch);
        //    merkleizer.Feed(container.Value.Root);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, HistoricalBatch? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(1);
        //    merkleizer.Feed(container.BlockRoots);
        //    merkleizer.Feed(container.StateRoots);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, SignedVoluntaryExit container)
        //{
        //    Merkleizer merkleizer = new Merkleizer(1);
        //    merkleizer.Feed(container.Message);
        //    merkleizer.Feed(container.Signature);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, VoluntaryExit container)
        //{
        //    Merkleizer merkleizer = new Merkleizer(1);
        //    merkleizer.Feed(container.Epoch);
        //    merkleizer.Feed(container.ValidatorIndex);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, Validator? container)
        //{
        //    if (container is null)
        //    {
        //        root = RootOfNull;
        //        return;
        //    }

        //    Merkleizer merkleizer = new Merkleizer(3);
        //    merkleizer.Feed(container.PublicKey);
        //    merkleizer.Feed(container.WithdrawalCredentials);
        //    merkleizer.Feed(container.EffectiveBalance);
        //    merkleizer.Feed(container.IsSlashed);
        //    merkleizer.Feed(container.ActivationEligibilityEpoch);
        //    merkleizer.Feed(container.ActivationEpoch);
        //    merkleizer.Feed(container.ExitEpoch);
        //    merkleizer.Feed(container.WithdrawableEpoch);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, BeaconBlockHeader container)
        //{
        //    Merkleizer merkleizer = new Merkleizer(2);
        //    merkleizer.Feed(container.Slot);
        //    merkleizer.Feed(container.ParentRoot);
        //    merkleizer.Feed(container.StateRoot);
        //    merkleizer.Feed(container.BodyRoot);
        //    merkleizer.CalculateRoot(out root);
        //}

        //public static void Ize(out UInt256 root, SignedBeaconBlockHeader container)
        //{
        //    Merkleizer merkleizer = new Merkleizer(1);
        //    merkleizer.Feed(container.Message);
        //    merkleizer.Feed(container.Signature);
        //    merkleizer.CalculateRoot(out root);
        //}
    }
}
