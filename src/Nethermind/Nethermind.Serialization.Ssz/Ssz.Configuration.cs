// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Ssz;

public partial class Ssz
{
    public static int DepositContractTreeDepth { get; private set; }
    private static int JustificationBitsLength;
    public static ulong MaximumDepositContracts { get; private set; }

    public static uint MaxValidatorsPerCommittee { get; private set; }

    public static uint SlotsPerEpoch { get; private set; }
    public static int SlotsPerEth1VotingPeriod { get; private set; }
    public static int SlotsPerHistoricalRoot { get; private set; }

    public static int EpochsPerHistoricalVector { get; private set; }
    public static int EpochsPerSlashingsVector { get; private set; }
    public static ulong HistoricalRootsLimit { get; private set; }
    public static ulong ValidatorRegistryLimit { get; private set; }

    public static uint MaxProposerSlashings { get; private set; }
    public static uint MaxAttesterSlashings { get; private set; }
    public static uint MaxAttestations { get; private set; }
    public static uint MaxDeposits { get; private set; }
    public static uint MaxVoluntaryExits { get; private set; }

    public static void Init(int depositContractTreeDepth,
        int justificationBitsLength,
        ulong maximumValidatorsPerCommittee,
        ulong slotsPerEpoch,
        ulong slotsPerEth1VotingPeriod,
        ulong slotsPerHistoricalRoot,
        ulong epochsPerHistoricalVector,
        ulong epochsPerSlashingsVector,
        ulong historicalRootsLimit,
        ulong validatorRegistryLimit,
        ulong maximumProposerSlashings,
        ulong maximumAttesterSlashings,
        ulong maximumAttestations,
        ulong maximumDeposits,
        ulong maximumVoluntaryExits
    )
    {
        DepositContractTreeDepth = depositContractTreeDepth;
        JustificationBitsLength = justificationBitsLength;
        MaxValidatorsPerCommittee = (uint)maximumValidatorsPerCommittee;
        SlotsPerEpoch = (uint)slotsPerEpoch;
        SlotsPerEth1VotingPeriod = (int)slotsPerEth1VotingPeriod;
        SlotsPerHistoricalRoot = (int)slotsPerHistoricalRoot;
        EpochsPerHistoricalVector = (int)epochsPerHistoricalVector;
        EpochsPerSlashingsVector = (int)epochsPerSlashingsVector;
        HistoricalRootsLimit = historicalRootsLimit;
        ValidatorRegistryLimit = validatorRegistryLimit;
        MaxProposerSlashings = (uint)maximumProposerSlashings;
        MaxAttesterSlashings = (uint)maximumAttesterSlashings;
        MaxAttestations = (uint)maximumAttestations;
        MaxDeposits = (uint)maximumDeposits;
        MaxVoluntaryExits = (uint)maximumVoluntaryExits;

        MaximumDepositContracts = (ulong)1 << depositContractTreeDepth;
    }
}
