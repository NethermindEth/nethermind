// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;

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
public partial record BeaconChainSpec
{
    /// <summary>Configuration copies retain reference identity.</summary>
    public virtual bool Equals(BeaconChainSpec? other) => ReferenceEquals(this, other);
    /// <inheritdoc/>
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    /// <inheritdoc/>
    public override string ToString() => base.ToString()!;

    /// <summary>
    /// The execution-layer chain id this spec was selected for (<c>0</c> for ad hoc specs built
    /// outside <see cref="ForExecution"/>, e.g. in tests).
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
    /// scheduled yet. Unscheduled forks have no matching entry in <see cref="Forks"/>.
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
    /// are consistent by construction in <see cref="ForExecution"/>; a copy made with <c>with</c> is not checked.
    /// </remarks>
    public byte[] VersionForEpoch(ulong epoch)
    {
        for (int i = Forks.Length - 1; i >= 0; i--)
            if (Forks[i].Epoch <= epoch) return Forks[i].Version;
        throw new InvalidOperationException($"No fork is scheduled at epoch {epoch}.");
    }
    /// <summary><c>GENESIS_FORK_VERSION</c>: the phase0 entry of <see cref="Forks"/>, which deposit and BLS-to-execution-change domains use.</summary>
    public byte[] GenesisForkVersion => Forks[0].Version;
    /// <summary><c>CAPELLA_FORK_VERSION</c>: the Capella entry of <see cref="Forks"/>, which EIP-7044 fixes as the voluntary-exit domain.</summary>
    public byte[] CapellaForkVersion => Forks[3].Version;

    /// <summary>
    /// The beacon state shape live at <paramref name="epoch"/>. Never falls back to an earlier fork for
    /// an epoch this driver cannot represent: an epoch before <see cref="ElectraForkEpoch"/> throws,
    /// matching how the rest of this driver (e.g. <c>CheckpointSync</c>) already refuses anything it has
    /// no concrete state type for, rather than silently treating it as Electra.
    /// </summary>
    /// <exception cref="StateTransition.BeaconStateException"><paramref name="epoch"/> predates Electra.</exception>
    /// <exception cref="NotSupportedException">A <see cref="Forks"/> entry scheduled after the Gloas entry is live at <paramref name="epoch"/>.</exception>
    public BeaconFork ForkAtEpoch(ulong epoch)
    {
        if (epoch >= GloasForkEpoch)
        {
            ThrowIfForkAfterGloasIsLive(epoch);
            return BeaconFork.Gloas;
        }
        if (epoch >= FuluForkEpoch) return BeaconFork.Fulu;
        if (epoch >= ElectraForkEpoch) return BeaconFork.Electra;
        throw new StateTransition.BeaconStateException($"Epoch {epoch} predates Electra (fork epoch {ElectraForkEpoch}); this driver has no state type for it");
    }

    private void ThrowIfForkAfterGloasIsLive(ulong epoch)
    {
        int gloasIndex = Array.FindIndex(Forks, entry => entry.Version.AsSpan().SequenceEqual(GloasForkVersion));
        if (gloasIndex < 0) return;

        for (int index = gloasIndex + 1; index < Forks.Length; index++)
        {
            if (Forks[index].Epoch <= epoch)
                throw new NotSupportedException($"Epoch {epoch} is in the fork with version {Forks[index].Version.ToHexString(true)}, scheduled after Gloas, which this driver does not support");
        }
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

    public static BeaconChainSpec Mainnet { get; } = ForExecution(MainnetSpecProvider.Instance);
    public static BeaconChainSpec Hoodi { get; } = ForExecution(HoodiSpecProvider.Instance);
    public static BeaconChainSpec Sepolia { get; } = ForExecution(SepoliaSpecProvider.Instance);
    public static BeaconChainSpec Gnosis { get; } = ForExecution(GnosisSpecProvider.Instance);
    public static BeaconChainSpec Chiado { get; } = ForExecution(ChiadoSpecProvider.Instance);

    /// <summary>The spec of the network <paramref name="execution"/> follows, with its fork timing read from that execution schedule.</summary>
    /// <remarks>
    /// <para>
    /// The fork versions, slot timing, the Altair and Bellatrix epochs and the bootnodes come from the
    /// built-in network definition. The genesis time comes from the chainspec's <c>beaconChainGenesisTimestamp</c>.
    /// Each later fork activates at the epoch of the execution fork that ships with it: Capella with Shanghai
    /// (EIP-4895), Deneb with Cancun (EIP-4844), Electra with Prague (EIP-7002), Fulu with Osaka (EIP-7594) and
    /// Gloas with Amsterdam (EIP-7928).
    /// </para>
    /// <para>
    /// Each execution fork from Osaka on that changes the blob parameters adds an EIP-7892 blob schedule entry, and so
    /// does each later blob-parameter-only fork in <paramref name="blobScheduleTimestamps"/> even when it repeats the
    /// previous parameters, since the fork digest hashes the entry's epoch. Any other later execution fork changes nothing
    /// the beacon chain uses (for example a Gnosis bytecode rewrite) and is ignored. Timestamps above
    /// <see cref="Core.Specs.SpecProviderExtensions.LastScheduledForkTimestamp"/> are the execution layer's placeholders for
    /// unscheduled forks and are ignored.
    /// </para>
    /// </remarks>
    /// <param name="execution">The execution-layer fork schedule; its chain id selects the network.</param>
    /// <param name="gloasForkVersion"><c>BeaconChain.GloasForkVersion</c>: the Gloas version as 4 bytes of hex, or
    /// <c>null</c> for the network's published one.</param>
    /// <param name="blobScheduleTimestamps">The activation timestamps of the chainspec's <c>blobSchedule</c> entries, or
    /// <c>null</c> to infer blob-parameter-only forks from parameter changes alone.</param>
    /// <exception cref="UnsupportedBeaconNetworkException">The chain id is not a supported network.</exception>
    /// <exception cref="InvalidConfigurationException">The execution schedule has no beacon genesis time, activates a
    /// fork off an epoch boundary or out of order, changes blob parameters between Prague and Osaka, or schedules
    /// Amsterdam without a usable Gloas fork version.</exception>
    public static BeaconChainSpec ForExecution(ISpecProvider execution, string? gloasForkVersion = null, IEnumerable<ulong>? blobScheduleTimestamps = null)
    {
        BeaconNetwork network = BeaconNetwork.ForChainId(execution.ChainId);
        ulong genesisTime = execution.BeaconChainGenesisTimestamp
            ?? throw Conflict("The execution chainspec has no beaconChainGenesisTimestamp; the beacon chain fork schedule is derived from it.");
        ulong epochSeconds = network.SecondsPerSlot * network.SlotsPerEpoch;

        // Forks at or before the execution genesis are not transitions, so timestamp 0 stands in for them.
        ulong[] timestamps = [.. execution.TransitionActivations
            .Select(static activation => activation.Timestamp ?? 0)
            .Where(static timestamp => timestamp <= Core.Specs.SpecProviderExtensions.LastScheduledForkTimestamp)
            .Append(0ul)
            .Distinct()
            .Order()];
        IReleaseSpec[] specs = [.. timestamps.Select(timestamp => execution.GetSpec(long.MaxValue - 1, timestamp))];

        int capella = Activation(static spec => spec.IsEip4895Enabled, "Shanghai");
        int deneb = Activation(static spec => spec.IsEip4844Enabled, "Cancun");
        int electra = Activation(static spec => spec.IsEip7002Enabled, "Prague");
        int fulu = Activation(static spec => spec.IsEip7594Enabled, "Osaka");
        int gloas = Array.FindIndex(specs, static spec => spec.IsAmsterdamEnabled);

        ulong[] epochs = [0, network.AltairForkEpoch, network.BellatrixForkEpoch, EpochAt(capella, "Shanghai"), EpochAt(deneb, "Cancun"), EpochAt(electra, "Prague"), EpochAt(fulu, "Osaka")];
        List<ForkScheduleEntry> forks = [.. network.ForkVersions.Select((version, fork) => new ForkScheduleEntry(version, epochs[fork]))];

        HashSet<ulong> blobParameterForks = [.. blobScheduleTimestamps ?? []];
        List<BlobScheduleEntry> blobSchedule = [];
        for (int i = electra + 1; i < timestamps.Length; i++)
        {
            bool changed = !SameBlobParameters(specs[i - 1], specs[i]);
            if (changed && i < fulu)
                throw Conflict($"The execution chainspec changes blob parameters at timestamp {timestamps[i]}, between Prague and Osaka, where the beacon chain has no blob schedule.");
            if (changed || (i > fulu && i != gloas && blobParameterForks.Contains(timestamps[i])))
                blobSchedule.Add(new BlobScheduleEntry(EpochAt(i, "a blob parameter fork"), specs[i].MaxBlobCount));
        }

        ulong gloasEpoch = Presets.FarFutureEpoch;
        byte[] gloasVersion = network.GloasForkVersion;
        if (gloas >= 0)
        {
            if (network.ChainId is BlockchainIds.Gnosis or BlockchainIds.Chiado)
                throw Conflict("A Gnosis Gloas preset and fork schedule are not configured.");

            gloasEpoch = EpochAt(gloas, "Amsterdam");
            gloasVersion = gloasForkVersion is not null ? ParseForkVersion(gloasForkVersion)
                : network.GloasForkVersionConfirmed ? network.GloasForkVersion
                : throw Conflict($"The execution chainspec activates Amsterdam at timestamp {timestamps[gloas]}, but this release has no published Gloas fork version for chain id {network.ChainId}; set BeaconChain.GloasForkVersion.");
            foreach (ForkScheduleEntry fork in forks)
            {
                if (fork.Version.AsSpan().SequenceEqual(gloasVersion))
                    throw Conflict($"BeaconChain.GloasForkVersion {gloasVersion.ToHexString(withZeroX: true)} repeats an earlier fork version of this network.");
            }

            forks.Add(new ForkScheduleEntry(gloasVersion, gloasEpoch));
        }
        else if (gloasForkVersion is not null)
        {
            throw Conflict("BeaconChain.GloasForkVersion has no effect because the execution chainspec does not schedule Amsterdam.");
        }

        for (int i = 1; i < forks.Count; i++)
        {
            if (forks[i].Epoch < forks[i - 1].Epoch)
                throw Conflict($"The execution chainspec activates the fork with beacon version {forks[i].Version.ToHexString(withZeroX: true)} at epoch {forks[i].Epoch}, before the previous fork at epoch {forks[i - 1].Epoch}.");
        }

        return new BeaconChainSpec
        {
            ChainId = network.ChainId,
            CheckpointSyncUrl = network.CheckpointSyncUrl,
            Bootnodes = network.Bootnodes,
            SecondsPerSlot = network.SecondsPerSlot,
            SlotsPerEpoch = network.SlotsPerEpoch,
            GenesisTime = genesisTime,
            GenesisValidatorsRoot = network.GenesisValidatorsRoot,
            Forks = [.. forks],
            BlobSchedule = [.. blobSchedule],
            ElectraForkEpoch = epochs[5],
            FuluForkEpoch = epochs[6],
            MaxBlobsPerBlockElectra = specs[electra].MaxBlobCount,
            GloasForkEpoch = gloasEpoch,
            GloasForkVersion = gloasVersion,
        };

        int Activation(Func<IReleaseSpec, bool> enabled, string fork)
        {
            int index = Array.FindIndex(specs, spec => enabled(spec));
            return index >= 0 ? index : throw Conflict($"The execution chainspec never activates {fork}, which the embedded beacon chain requires.");
        }

        ulong EpochAt(int index, string fork)
        {
            ulong timestamp = timestamps[index];
            if (timestamp <= genesisTime) return 0;
            ulong elapsed = timestamp - genesisTime;
            return elapsed % epochSeconds == 0 ? elapsed / epochSeconds
                : throw Conflict($"The execution chainspec activates {fork} at timestamp {timestamp}, which is not on a beacon epoch boundary ({epochSeconds} s epochs from beaconChainGenesisTimestamp {genesisTime}).");
        }

        static bool SameBlobParameters(IReleaseSpec left, IReleaseSpec right) =>
            left.MaxBlobCount == right.MaxBlobCount
            && left.TargetBlobCount == right.TargetBlobCount
            && left.BlobBaseFeeUpdateFraction == right.BlobBaseFeeUpdateFraction;

        static InvalidConfigurationException Conflict(string message) => new(message, ExitCodes.ConflictingConfigurations);
    }

    private static byte[] ParseForkVersion(string version)
    {
        ReadOnlySpan<char> digits = version.AsSpan().Trim();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) digits = digits[2..];
        if (digits.Length != 8 || digits.ContainsAnyExcept("0123456789abcdefABCDEF"))
        {
            throw new InvalidConfigurationException($"BeaconChain.GloasForkVersion '{version}' is not 4 bytes of hex (for example 0x90000076).", ExitCodes.ConflictingConfigurations);
        }

        return Bytes.FromHexString(digits.ToString());
    }

#if MINIMAL_PRESET
    private static readonly BeaconChainSpec s_minimalSigningSpec = Mainnet with
    {
        Forks = [.. Mainnet.Forks.Select(static fork => new ForkScheduleEntry([.. fork.Version[..3], 1], fork.Epoch))],
    };
#endif

    /// <summary>The supported network a state with <paramref name="genesisValidatorsRoot"/> belongs to.</summary>
    /// <remarks>
    /// Signing domains fixed to a config fork version (<see cref="GenesisForkVersion"/>, <see cref="CapellaForkVersion"/>)
    /// resolve the network through the state's own root, so a testnet state never signs with mainnet's versions. Any
    /// other root is a consensus-spec test state, whose fixed signing versions match the compiled preset.
    /// </remarks>
    public static BeaconChainSpec ForGenesisValidatorsRoot(Hash256 genesisValidatorsRoot) =>
        genesisValidatorsRoot == Hoodi.GenesisValidatorsRoot ? Hoodi
        : genesisValidatorsRoot == Sepolia.GenesisValidatorsRoot ? Sepolia
        : genesisValidatorsRoot == Gnosis.GenesisValidatorsRoot ? Gnosis
        : genesisValidatorsRoot == Chiado.GenesisValidatorsRoot ? Chiado
#if MINIMAL_PRESET
        : s_minimalSigningSpec;
#else
        : Mainnet;
#endif
}
