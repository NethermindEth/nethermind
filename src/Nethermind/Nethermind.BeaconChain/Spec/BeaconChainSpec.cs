// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Spec;

/// <summary>A scheduled beacon chain fork: its version and activation epoch.</summary>
public readonly record struct ForkScheduleEntry(byte[] Version, ulong Epoch);

/// <summary>An EIP-7892 blob-parameters-only schedule entry.</summary>
public readonly record struct BlobScheduleEntry(ulong Epoch, ulong MaxBlobsPerBlock);

/// <summary>
/// The beacon state shape a fork uses. Deliberately lists only the forks this driver can construct a
/// concrete state for (<see cref="Nethermind.BeaconChain.Types.BeaconStateElectra"/>,
/// <see cref="Nethermind.BeaconChain.Types.BeaconStateFulu"/>,
/// <see cref="Nethermind.BeaconChain.Types.BeaconStateGloas"/>), matching how this codebase never
/// modeled a Phase0/Altair/Bellatrix/Capella/Deneb state type either. Adding a member here means
/// extending the matches in <c>StateTransition/ForkedStateTransition.cs</c> and
/// <c>StateTransition/GloasForkTransition.cs</c> by hand: they throw on an unrecognised fork rather
/// than falling back to Fulu, but they do not fail the build, so the compiler will not remind you.
/// </summary>
public enum BeaconFork
{
    Electra,
    Fulu,
    Gloas,
}

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

    /// <summary>
    /// The Gloas activation epoch, or <see cref="Presets.FarFutureEpoch"/> when this network has none
    /// scheduled yet. <see cref="Presets.FarFutureEpoch"/> is the spec's own placeholder for "TBD"
    /// (<c>specs/gloas/fork.md</c> config table: <c>GLOAS_FORK_EPOCH = Epoch(2**64 - 1) TBD</c>), so a
    /// network with no confirmed date carries it honestly instead of a guessed epoch. As of 2026-09-20
    /// only <see cref="Sepolia"/> has a confirmed Gloas epoch (353024, 2026-10-06); <see cref="Mainnet"/>
    /// and <see cref="Hoodi"/> both leave this at the far-future sentinel and add no matching entry to
    /// <see cref="Forks"/>.
    /// </summary>
    public required ulong GloasForkEpoch { get; init; }

    /// <summary>The Gloas <c>fork_version</c>, meaningless while <see cref="GloasForkEpoch"/> is unscheduled.</summary>
    public required byte[] GloasForkVersion { get; init; }

    /// <summary>The consensus-layer bootnode records for this network.</summary>
    /// <remarks>Held here rather than in a second switch keyed on chain id, which drifted:
    /// Sepolia was added to the spec and not to the bootnodes, and discovery threw on a
    /// network the spec claimed to support.</remarks>
    public required string[] Bootnodes { get; init; }

    public ulong GetEpoch(ulong slot) => slot / SlotsPerEpoch;

    public ulong GetSlotAtTime(ulong unixTime) => unixTime < GenesisTime ? 0 : (unixTime - GenesisTime) / SecondsPerSlot;

    /// <summary>The fork version live at <paramref name="epoch"/>, from the <see cref="Forks"/> schedule.</summary>
    /// <remarks>
    /// This and <see cref="ForkAtEpoch"/> read different fields, so a spec whose scalar fork epochs
    /// disagree with its <see cref="Forks"/> entries would compute a digest for one fork while
    /// processing state as another, and silently lose every peer at the boundary. The shipped specs
    /// are held consistent by a test rather than by construction.
    /// </remarks>
    public byte[] VersionForEpoch(ulong epoch) => Forks.Last(f => f.Epoch <= epoch).Version;

    /// <summary>
    /// The beacon state shape live at <paramref name="epoch"/>. Never falls back to an earlier fork for
    /// an epoch this driver cannot represent: an epoch before <see cref="ElectraForkEpoch"/> throws,
    /// matching how the rest of this driver (e.g. <c>CheckpointSync</c>) already refuses anything it has
    /// no concrete state type for, rather than silently treating it as Electra.
    /// </summary>
    /// <exception cref="StateTransition.BeaconStateException"><paramref name="epoch"/> predates Electra.</exception>
    public BeaconFork ForkAtEpoch(ulong epoch)
    {
        if (epoch >= GloasForkEpoch) return BeaconFork.Gloas;
        if (epoch >= FuluForkEpoch) return BeaconFork.Fulu;
        if (epoch >= ElectraForkEpoch) return BeaconFork.Electra;
        throw new StateTransition.BeaconStateException($"Epoch {epoch} predates Electra (fork epoch {ElectraForkEpoch}); this driver has no state type for it");
    }

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
        Bootnodes = MainnetBootnodes.Enrs,
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
        GloasForkEpoch = Presets.FarFutureEpoch, // not yet scheduled on mainnet as of 2026-09-19
        GloasForkVersion = Bytes.FromHexString("0x07000000"), // specs/gloas/fork.md GLOAS_FORK_VERSION; meaningless while unscheduled
    };

    public static BeaconChainSpec Hoodi { get; } = new()
    {
        ChainId = BlockchainIds.Hoodi,
        CheckpointSyncUrl = "https://checkpoint-sync.hoodi.ethpandaops.io",
        Bootnodes = HoodiBootnodes.Enrs,
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
        GloasForkEpoch = Presets.FarFutureEpoch, // not yet scheduled on Hoodi as of 2026-09-19 (only Sepolia has a confirmed date)
        GloasForkVersion = Bytes.FromHexString("0x07000000"),
    };

    /// <summary>
    /// Sepolia activates Glamsterdam (consensus fork Gloas) at epoch 353024 (2026-10-06). Every value
    /// below was cross-checked against at least two independent sources as of 2026-09-20:
    /// <list type="bullet">
    /// <item>ChainId, GenesisTime, GenesisValidatorsRoot, GenesisForkVersion, and the Phase0-through-Fulu
    /// fork schedule: <c>eth-clients/sepolia</c> <c>metadata/config.yaml</c>, cross-checked live against
    /// a public beacon node's <c>/eth/v1/beacon/genesis</c> and <c>/eth/v1/config/fork_schedule</c>, and
    /// against Prysm's <c>testnet_sepolia_config.go</c> (all three agree).</item>
    /// <item><see cref="GloasForkVersion"/> (0x90000076): ChainSafe/lodestar PR #10119 (merged
    /// 2026-09-17) and sigp/lighthouse's built-in Sepolia config, independently.</item>
    /// <item><see cref="GloasForkEpoch"/> (353024): ethereum/pm PR #2205 (merged 2026-09-15, the
    /// cross-client activation-time proposal) and ChainSafe/lodestar PR #10119 (merged), independently.</item>
    /// <item><see cref="BlobSchedule"/> (BPO1 274176/15, BPO2 275712/21) and
    /// <see cref="MaxBlobsPerBlockElectra"/> (9): <c>eth-clients/sepolia config.yaml</c> and
    /// sigp/lighthouse's built-in Sepolia config, independently.</item>
    /// <item>Bootnode ENRs (<see cref="P2P.Discovery.SepoliaBootnodes"/>): <c>eth-clients/sepolia</c>
    /// <c>metadata/bootstrap_nodes.yaml</c> (the maintained registry, post PR #124's EF fleet
    /// replacement) and OffchainLabs/prysm PR #17474 (merged 2026-09-11, the matching bootnode swap),
    /// independently, for every entry.</item>
    /// </list>
    /// No value here was carried from only one source: see this driver's task notes for the fields that
    /// could not clear that bar and were deliberately left out rather than guessed.
    /// </summary>
    public static BeaconChainSpec Sepolia { get; } = new()
    {
        ChainId = BlockchainIds.Sepolia,
        CheckpointSyncUrl = "https://checkpoint-sync.sepolia.ethpandaops.io",
        Bootnodes = SepoliaBootnodes.Enrs,
        SecondsPerSlot = 12,
        SlotsPerEpoch = 32,
        GenesisTime = 1655733600,
        GenesisValidatorsRoot = new Hash256(Bytes.FromHexString("0xd8ea171f3c94aea21ebc42a1ed61052acf3f9209c00e4efbaaddac09ed9b8078")),
        Forks =
        [
            new(Bytes.FromHexString("0x90000069"), 0), // phase0
            new(Bytes.FromHexString("0x90000070"), 50), // altair
            new(Bytes.FromHexString("0x90000071"), 100), // bellatrix
            new(Bytes.FromHexString("0x90000072"), 56832), // capella
            new(Bytes.FromHexString("0x90000073"), 132608), // deneb
            new(Bytes.FromHexString("0x90000074"), 222464), // electra
            new(Bytes.FromHexString("0x90000075"), 272640), // fulu
            new(Bytes.FromHexString("0x90000076"), 353024), // gloas
        ],
        BlobSchedule =
        [
            new(274176, 15), // BPO1
            new(275712, 21), // BPO2
        ],
        ElectraForkEpoch = 222464,
        FuluForkEpoch = 272640,
        MaxBlobsPerBlockElectra = 9,
        GloasForkEpoch = 353024, // confirmed: ethereum/pm#2205 and lodestar#10119, both merged
        GloasForkVersion = Bytes.FromHexString("0x90000076"),
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
        BlockchainIds.Sepolia => Sepolia,
        _ => throw new UnsupportedBeaconNetworkException(chainId),
    };
}
