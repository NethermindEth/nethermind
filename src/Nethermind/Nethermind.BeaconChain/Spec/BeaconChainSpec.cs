// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Spec;

/// <summary>A scheduled beacon chain fork: its version and activation epoch.</summary>
public readonly record struct ForkScheduleEntry(byte[] Version, ulong Epoch);

/// <summary>An EIP-7892 blob-parameters-only schedule entry.</summary>
public readonly record struct BlobScheduleEntry(ulong Epoch, ulong MaxBlobsPerBlock);

/// <summary>
/// Beacon chain configuration: fork schedule, genesis information, and timing parameters.
/// </summary>
/// <remarks>
/// Mirrors the consensus-specs <c>config.yaml</c> values. Preset constants that affect SSZ
/// shapes (list limits, vector lengths) live in the container definitions instead, since the
/// SSZ source generator requires compile-time constants.
/// </remarks>
public class BeaconChainSpec
{
    /// <summary>
    /// The execution-layer chain id this spec was selected for (<c>0</c> for ad hoc specs built
    /// outside <see cref="ForChainId"/>, e.g. in tests).
    /// </summary>
    public ulong ChainId { get; init; }

    /// <summary>Default checkpoint-sync provider for this network, used when no override is configured.</summary>
    public string? CheckpointSyncUrl { get; init; }

    public required ulong SecondsPerSlot { get; init; }
    public required ulong SlotsPerEpoch { get; init; }
    public required ulong GenesisTime { get; init; }
    public required Hash256 GenesisValidatorsRoot { get; init; }

    /// <summary>Fork schedule sorted by ascending activation epoch.</summary>
    public required ForkScheduleEntry[] Forks { get; init; }

    /// <summary>EIP-7892 blob schedule sorted by ascending activation epoch.</summary>
    public required BlobScheduleEntry[] BlobSchedule { get; init; }

    public required ulong ElectraForkEpoch { get; init; }
    public required ulong FuluForkEpoch { get; init; }
    public required ulong MaxBlobsPerBlockElectra { get; init; }

    public ulong GetEpoch(ulong slot) => slot / SlotsPerEpoch;

    public ulong GetSlotAtTime(ulong unixTime) => unixTime < GenesisTime ? 0 : (unixTime - GenesisTime) / SecondsPerSlot;

    public byte[] VersionForEpoch(ulong epoch) => Forks.Last(f => f.Epoch <= epoch).Version;

    /// <summary>
    /// Returns the blob parameters in effect at <paramref name="epoch"/>, or <c>null</c> before Fulu.
    /// </summary>
    /// <remarks>
    /// Before the first scheduled BPO fork, Fulu inherits the Electra blob parameters keyed at
    /// the Electra fork epoch, matching <c>get_blob_parameters</c> in consensus-specs (EIP-7892).
    /// </remarks>
    public BlobScheduleEntry? GetBlobParameters(ulong epoch)
    {
        if (epoch < FuluForkEpoch) return null;

        BlobScheduleEntry? scheduled = null;
        foreach (BlobScheduleEntry entry in BlobSchedule)
        {
            if (entry.Epoch <= epoch && (scheduled is null || entry.Epoch > scheduled.Value.Epoch))
            {
                scheduled = entry;
            }
        }

        return scheduled ?? new BlobScheduleEntry(ElectraForkEpoch, MaxBlobsPerBlockElectra);
    }

    public static BeaconChainSpec Mainnet { get; } = new()
    {
        ChainId = BlockchainIds.Mainnet,
        CheckpointSyncUrl = "https://mainnet.checkpoint.sigp.io",
        SecondsPerSlot = 12,
        SlotsPerEpoch = 32,
        GenesisTime = 1606824023,
        GenesisValidatorsRoot = new Hash256(Bytes.FromHexString("0x4b363db94e286120d76eb905340fdd4e54bfe9f06bf33ff6cf5ad27f511bfe95")),
        Forks =
        [
            new(Bytes.FromHexString("0x00000000"), 0), // phase0
            new(Bytes.FromHexString("0x01000000"), 74240), // altair
            new(Bytes.FromHexString("0x02000000"), 144896), // bellatrix
            new(Bytes.FromHexString("0x03000000"), 194048), // capella
            new(Bytes.FromHexString("0x04000000"), 269568), // deneb
            new(Bytes.FromHexString("0x05000000"), 364032), // electra
            new(Bytes.FromHexString("0x06000000"), 411392), // fulu
        ],
        BlobSchedule =
        [
            new(412672, 15), // BPO1
            new(419072, 21), // BPO2
        ],
        ElectraForkEpoch = 364032,
        FuluForkEpoch = 411392,
        MaxBlobsPerBlockElectra = 9,
    };

    public static BeaconChainSpec Hoodi { get; } = new()
    {
        ChainId = BlockchainIds.Hoodi,
        CheckpointSyncUrl = "https://checkpoint-sync.hoodi.ethpandaops.io",
        SecondsPerSlot = 12,
        SlotsPerEpoch = 32,
        GenesisTime = 1742213400,
        GenesisValidatorsRoot = new Hash256(Bytes.FromHexString("0x212f13fc4df078b6cb7db228f1c8307566dcecf900867401a92023d7ba99cb5f")),
        Forks =
        [
            new(Bytes.FromHexString("0x10000910"), 0), // phase0
            new(Bytes.FromHexString("0x20000910"), 0), // altair
            new(Bytes.FromHexString("0x30000910"), 0), // bellatrix
            new(Bytes.FromHexString("0x40000910"), 0), // capella
            new(Bytes.FromHexString("0x50000910"), 0), // deneb
            new(Bytes.FromHexString("0x60000910"), 2048), // electra
            new(Bytes.FromHexString("0x70000910"), 50688), // fulu
        ],
        BlobSchedule =
        [
            new(52480, 15), // BPO1
            new(54016, 21), // BPO2
        ],
        ElectraForkEpoch = 2048,
        FuluForkEpoch = 50688,
        MaxBlobsPerBlockElectra = 9,
    };

    /// <summary>Selects the beacon chain spec for the execution layer's chain id.</summary>
    /// <exception cref="UnsupportedBeaconNetworkException">
    /// <paramref name="chainId"/> is not a network the embedded beacon chain driver supports.
    /// Never falls back to <see cref="Mainnet"/>: following the wrong chain silently is worse
    /// than refusing to start.
    /// </exception>
    public static BeaconChainSpec ForChainId(ulong chainId) => chainId switch
    {
        BlockchainIds.Mainnet => Mainnet,
        BlockchainIds.Hoodi => Hoodi,
        _ => throw new UnsupportedBeaconNetworkException(chainId),
    };
}
