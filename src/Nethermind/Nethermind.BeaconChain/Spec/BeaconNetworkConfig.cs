// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Nethermind.BeaconChain.Spec;

/// <summary>The consensus <c>config.yaml</c> of a network that is not built in, read with the <c>genesis.ssz</c> beside it.</summary>
/// <remarks>
/// The file stands in for a built-in <see cref="BeaconNetwork"/>: it gives the fork versions and the Altair and Bellatrix epochs,
/// and <c>genesis.ssz</c> gives the genesis time and genesis validators root. Everything else this release either derives from
/// the execution chainspec, as for a built-in network, or fixes at compile time, so the file may only repeat it
/// (<see cref="ThrowIfContradicts"/>). Keys this release does not use are ignored.
/// </remarks>
internal sealed class BeaconNetworkConfig(string path, Dictionary<string, string> values, BlobScheduleEntry[] blobSchedule, BeaconNetwork network)
{
    private const string GenesisStateFileName = "genesis.ssz";

    public BeaconNetwork Network => network;

    /// <summary>Reads <paramref name="path"/> and the <c>genesis.ssz</c> in its directory as the network of <paramref name="chainId"/>.</summary>
    /// <exception cref="InvalidConfigurationException">Either file is unreadable, or the config lacks a fork version or the Altair or Bellatrix epoch.</exception>
    public static BeaconNetworkConfig Load(string path, ulong chainId)
    {
        Dictionary<string, string> values = [];
        BlobScheduleEntry[] blobSchedule = [];
        try
        {
            YamlStream yaml = [];
            using (StreamReader reader = File.OpenText(path)) yaml.Load(reader);
            foreach ((YamlNode key, YamlNode value) in ((YamlMappingNode)yaml.Documents[0].RootNode).Children)
            {
                string name = ((YamlScalarNode)key).Value!;
                if (value is YamlScalarNode scalar) values[name] = scalar.Value ?? "";
                else if (name == "BLOB_SCHEDULE")
                    // An entry at FAR_FUTURE_EPOCH is a placeholder for an unscheduled fork, which the execution chainspec drops too.
                    blobSchedule = [.. ((YamlSequenceNode)value).Select(entry => new BlobScheduleEntry(
                        Number(path, "BLOB_SCHEDULE EPOCH", ((YamlScalarNode)entry["EPOCH"]).Value),
                        Number(path, "BLOB_SCHEDULE MAX_BLOBS_PER_BLOCK", ((YamlScalarNode)entry["MAX_BLOBS_PER_BLOCK"]).Value)))
                        .Where(static entry => entry.Epoch != Presets.FarFutureEpoch)];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or YamlException or InvalidCastException or KeyNotFoundException or ArgumentException)
        {
            throw new InvalidConfigurationException($"BeaconChain.NetworkConfigFile {path} is not a readable consensus config.yaml: {e.Message}", ExitCodes.ForbiddenOptionValue);
        }

        // Every beacon state begins with genesis_time (uint64) and genesis_validators_root (Bytes32), whatever its fork.
        string genesisFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, GenesisStateFileName);
        byte[] genesis = new byte[sizeof(ulong) + Hash256.Size];
        try
        {
            using FileStream stream = File.OpenRead(genesisFile);
            stream.ReadExactly(genesis);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidConfigurationException($"BeaconChain.NetworkConfigFile {path} needs the network's genesis state beside it, but {genesisFile} is not readable: {e.Message}", ExitCodes.ForbiddenOptionValue);
        }

        return new BeaconNetworkConfig(path, values, blobSchedule, new BeaconNetwork
        {
            ChainId = chainId,
            CheckpointSyncUrl = null,
            Bootnodes = [],
            SecondsPerSlot = Presets.SecondsPerSlot,
            SlotsPerEpoch = Presets.SlotsPerEpoch,
            GenesisTime = BinaryPrimitives.ReadUInt64LittleEndian(genesis),
            GenesisValidatorsRoot = new Hash256(genesis.AsSpan(sizeof(ulong))),
            ForkVersions =
            [
                Version(path, values, "GENESIS_FORK_VERSION"),
                Version(path, values, "ALTAIR_FORK_VERSION"),
                Version(path, values, "BELLATRIX_FORK_VERSION"),
                Version(path, values, "CAPELLA_FORK_VERSION"),
                Version(path, values, "DENEB_FORK_VERSION"),
                Version(path, values, "ELECTRA_FORK_VERSION"),
                Version(path, values, "FULU_FORK_VERSION"),
            ],
            AltairForkEpoch = Number(path, "ALTAIR_FORK_EPOCH", Required(path, values, "ALTAIR_FORK_EPOCH")),
            BellatrixForkEpoch = Number(path, "BELLATRIX_FORK_EPOCH", Required(path, values, "BELLATRIX_FORK_EPOCH")),
            GloasForkVersion = values.ContainsKey("GLOAS_FORK_VERSION") ? Version(path, values, "GLOAS_FORK_VERSION") : new byte[4],
            GloasForkVersionConfirmed = values.ContainsKey("GLOAS_FORK_VERSION"),
        });
    }

    /// <summary>Refuses a file whose value of a key this release uses differs from the value <paramref name="spec"/> runs with.</summary>
    /// <param name="spec">The spec resolved from this file's <see cref="Network"/> and the execution schedule.</param>
    /// <param name="chainspecGenesisTime">The execution chainspec's <c>beaconChainGenesisTimestamp</c>, if it has one.</param>
    /// <exception cref="InvalidConfigurationException">A value contradicts the execution chainspec, chain id or a constant of this release; the message names its key.</exception>
    public void ThrowIfContradicts(BeaconChainSpec spec, ulong? chainspecGenesisTime)
    {
        if (chainspecGenesisTime is { } chainspecTime && chainspecTime != spec.GenesisTime)
            throw Contradiction($"The genesis_time {spec.GenesisTime} of the {GenesisStateFileName} beside it is not the execution chainspec's beaconChainGenesisTimestamp {chainspecTime}.");

        foreach ((string key, string expected, string source) in RunValues(spec))
        {
            if (values.TryGetValue(key, out string? value) && !string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
                throw Contradiction($"It sets {key} to {value}, but this node runs {expected} from {source}.");
        }

        // Compared by effect, as get_blob_parameters (EIP-7892) and the fork digest see them: a chainspec activating every fork at
        // genesis folds the Electra limit and the genesis BPO entries into the parameters of its genesis spec.
        foreach (ulong epoch in spec.BlobSchedule.Select(static entry => entry.Epoch).Concat(blobSchedule.Select(static entry => entry.Epoch)).Append(spec.FuluForkEpoch))
        {
            if (epoch < spec.FuluForkEpoch) continue;
            BlobScheduleEntry expected = spec.GetBlobParameters(epoch)!.Value;
            BlobScheduleEntry configured = BlobParameters(epoch, spec);
            if (configured != expected)
                throw Contradiction($"At epoch {epoch} its BLOB_SCHEDULE and MAX_BLOBS_PER_BLOCK_ELECTRA allow {configured.MaxBlobsPerBlock} blobs per block from epoch {configured.Epoch}, but the execution chainspec allows {expected.MaxBlobsPerBlock} from epoch {expected.Epoch}.");
        }
    }

    /// <summary>The file's <c>get_blob_parameters(epoch)</c> for an epoch from Fulu on.</summary>
    private BlobScheduleEntry BlobParameters(ulong epoch, BeaconChainSpec spec)
    {
        BlobScheduleEntry? scheduled = null;
        foreach (BlobScheduleEntry entry in blobSchedule)
        {
            if (entry.Epoch <= epoch && (scheduled is null || entry.Epoch > scheduled.Value.Epoch)) scheduled = entry;
        }

        return scheduled ?? new BlobScheduleEntry(spec.ElectraForkEpoch,
            values.TryGetValue("MAX_BLOBS_PER_BLOCK_ELECTRA", out string? max) ? Number(path, "MAX_BLOBS_PER_BLOCK_ELECTRA", max) : spec.MaxBlobsPerBlockElectra);
    }

    /// <summary>The config keys this release uses with the value <paramref name="spec"/> runs with and where that value comes from.</summary>
    private static IEnumerable<(string Key, string Value, string Source)> RunValues(BeaconChainSpec spec)
    {
        const string chainspec = "the execution chainspec";
        yield return ("CAPELLA_FORK_EPOCH", spec.Forks[3].Epoch.ToString(), chainspec);
        yield return ("DENEB_FORK_EPOCH", spec.Forks[4].Epoch.ToString(), chainspec);
        yield return ("ELECTRA_FORK_EPOCH", spec.ElectraForkEpoch.ToString(), chainspec);
        yield return ("FULU_FORK_EPOCH", spec.FuluForkEpoch.ToString(), chainspec);
        yield return ("GLOAS_FORK_EPOCH", spec.GloasForkEpoch.ToString(), chainspec);
        yield return ("GLOAS_FORK_VERSION", spec.GloasForkVersion.ToHexString(withZeroX: true), "BeaconChain.GloasForkVersion");
        if (spec.ElectraForkEpoch < spec.FuluForkEpoch)
            yield return ("MAX_BLOBS_PER_BLOCK_ELECTRA", spec.MaxBlobsPerBlockElectra.ToString(), chainspec);
        yield return ("DEPOSIT_CHAIN_ID", spec.ChainId.ToString(), "the execution chain id");
        yield return ("PRESET_BASE", Presets.PresetBase, "the preset of this release");

        foreach ((string key, ulong value) in Constants())
            yield return (key, value.ToString(), "a constant of this release");
    }

    private static (string Key, ulong Value)[] Constants() =>
    [
        ("SECONDS_PER_SLOT", Presets.SecondsPerSlot),
        ("SLOT_DURATION_MS", Presets.SecondsPerSlot * 1000),
        ("MIN_VALIDATOR_WITHDRAWABILITY_DELAY", Presets.MinValidatorWithdrawabilityDelay),
        ("SHARD_COMMITTEE_PERIOD", Presets.ShardCommitteePeriod),
        ("MIN_BUILDER_WITHDRAWABILITY_DELAY", Presets.MinBuilderWithdrawabilityDelay),
        ("PROPOSER_REORG_CUTOFF_BPS", GloasTiming.ProposerReorgCutoffBps),
        ("ATTESTATION_DUE_BPS", GloasTiming.AttestationDueBps),
        ("ATTESTATION_DUE_BPS_GLOAS", GloasTiming.AttestationDueBpsGloas),
        ("PAYLOAD_ATTESTATION_DUE_BPS", GloasTiming.PayloadAttestationDueBps),
        ("INACTIVITY_SCORE_BIAS", Presets.InactivityScoreBias),
        ("INACTIVITY_SCORE_RECOVERY_RATE", Presets.InactivityScoreRecoveryRate),
        ("EJECTION_BALANCE", Presets.EjectionBalance),
        ("MIN_PER_EPOCH_CHURN_LIMIT", Presets.MinPerEpochChurnLimit),
        ("CHURN_LIMIT_QUOTIENT", Presets.ChurnLimitQuotient),
        ("MAX_PER_EPOCH_ACTIVATION_CHURN_LIMIT", Presets.MaxPerEpochActivationChurnLimit),
        ("MIN_PER_EPOCH_CHURN_LIMIT_ELECTRA", Presets.MinPerEpochChurnLimitElectra),
        ("MAX_PER_EPOCH_ACTIVATION_EXIT_CHURN_LIMIT", Presets.MaxPerEpochActivationExitChurnLimit),
        ("CHURN_LIMIT_QUOTIENT_GLOAS", Presets.ChurnLimitQuotientGloas),
        ("CONSOLIDATION_CHURN_LIMIT_QUOTIENT", Presets.ConsolidationChurnLimitQuotient),
        ("MAX_PER_EPOCH_ACTIVATION_CHURN_LIMIT_GLOAS", Presets.MaxPerEpochActivationChurnLimitGloas),
        ("PROPOSER_SCORE_BOOST", ProtoArrayForkChoice.DefaultProposerScoreBoostPercent),
        ("REORG_HEAD_WEIGHT_THRESHOLD", ForkChoiceRunner.ReorgHeadWeightThresholdPercent),
        ("REORG_PARENT_WEIGHT_THRESHOLD", ForkChoiceRunner.ReorgParentWeightThresholdPercent),
        ("REORG_MAX_EPOCHS_SINCE_FINALIZATION", ForkChoiceRunner.ReorgMaxEpochsSinceFinalization),
        ("CONFIRMATION_BYZANTINE_THRESHOLD", FastConfirmation.ByzantineThreshold),
        ("MAX_PAYLOAD_SIZE", (ulong)ReqRespFraming.MaxPayloadSize),
        ("MAX_REQUEST_BLOCKS_DENEB", BlocksProtocolBase.MaxRequestBlocks),
        ("MAX_REQUEST_DATA_COLUMN_SIDECARS", DataColumnSidecarsProtocolBase.MaxRequestDataColumnSidecars),
        ("MAX_REQUEST_PAYLOADS", ExecutionPayloadEnvelopesProtocolBase.MaxRequestPayloads),
        ("MAXIMUM_GOSSIP_CLOCK_DISPARITY", (ulong)GossipRouter.MaximumGossipClockDisparityMs),
        ("NUMBER_OF_CUSTODY_GROUPS", Eip7594DasConstants.NumberOfCustodyGroups),
        ("DATA_COLUMN_SIDECAR_SUBNET_COUNT", Eip7594DasConstants.DataColumnSidecarSubnetCount),
        ("SAMPLES_PER_SLOT", Eip7594DasConstants.SamplesPerSlot),
        ("CUSTODY_REQUIREMENT", Eip7594DasConstants.CustodyRequirement),
        ("MIN_EPOCHS_FOR_BLOB_SIDECARS_REQUESTS", DataAvailabilityBoundary.MinEpochsForBlobSidecarsRequests),
        ("MIN_EPOCHS_FOR_DATA_COLUMN_SIDECARS_REQUESTS", Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests),
    ];

    private InvalidConfigurationException Contradiction(string problem) =>
        new($"BeaconChain.NetworkConfigFile {path} contradicts this node: {problem}", ExitCodes.ConflictingConfigurations);

    private static string Required(string path, Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) ? value
            : throw new InvalidConfigurationException($"BeaconChain.NetworkConfigFile {path} has no {key}.", ExitCodes.ForbiddenOptionValue);

    private static ulong Number(string path, string key, string? value) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) ? number
            : throw new InvalidConfigurationException($"BeaconChain.NetworkConfigFile {path} sets {key} to '{value}', which is not an unsigned integer.", ExitCodes.ForbiddenOptionValue);

    private static byte[] Version(string path, Dictionary<string, string> values, string key)
    {
        string value = Required(path, values, key);
        return value.Length == 10 && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && !value.AsSpan(2).ContainsAnyExcept("0123456789abcdefABCDEF")
            ? Bytes.FromHexString(value)
            : throw new InvalidConfigurationException($"BeaconChain.NetworkConfigFile {path} sets {key} to '{value}', which is not 4 bytes of 0x-prefixed hex.", ExitCodes.ForbiddenOptionValue);
    }
}
