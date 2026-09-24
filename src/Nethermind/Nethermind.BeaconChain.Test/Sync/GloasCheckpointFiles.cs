// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Test.IO;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>A checkpoint state file, and optionally its sibling block file, in a directory deleted on dispose.</summary>
internal sealed class GloasCheckpointFiles : IDisposable
{
    private readonly TempPath _directory = TempPath.GetTempDirectory();

    private GloasCheckpointFiles(byte[] stateSsz, byte[]? blockSsz)
    {
        Directory.CreateDirectory(_directory.Path);
        StateFile = Path.Combine(_directory.Path, "anchor.ssz");
        File.WriteAllBytes(StateFile, stateSsz);
        if (blockSsz is not null)
        {
            File.WriteAllBytes(Path.ChangeExtension(StateFile, ".block.ssz"), blockSsz);
        }
    }

    /// <summary>
    /// <see cref="ForkCrossingChain"/>'s spec with the Gloas version in its fork schedule, as a network with
    /// Gloas scheduled carries it, so a checkpoint state's <c>fork.current_version</c> maps onto a fork.
    /// </summary>
    public static BeaconChainSpec Spec { get; } = WithGloasScheduled(ForkCrossingChain.Instance.Spec, fuluInGloasEpoch: false);

    /// <summary><see cref="Spec"/> with Fulu activating in the Gloas epoch, as on a network that starts at Gloas.</summary>
    public static BeaconChainSpec SharedActivationEpochSpec { get; } = WithGloasScheduled(ForkCrossingChain.Instance.Spec, fuluInGloasEpoch: true);

    /// <summary>The path to configure as <see cref="IBeaconChainConfig.CheckpointStateFile"/>.</summary>
    public string StateFile { get; }

    public static GloasCheckpointFiles Write(BeaconStateGloas state, ForkedSignedBeaconBlock? block) =>
        new(BeaconStateGloas.Encode(state), block is null ? null : SignedBeaconBlockCodec.Encode(block, Spec));

    public void Dispose() => _directory.Dispose();

    private static BeaconChainSpec WithGloasScheduled(BeaconChainSpec spec, bool fuluInGloasEpoch) => new()
    {
        SecondsPerSlot = spec.SecondsPerSlot,
        SlotsPerEpoch = spec.SlotsPerEpoch,
        GenesisTime = spec.GenesisTime,
        GenesisValidatorsRoot = spec.GenesisValidatorsRoot,
        Forks =
        [
            .. fuluInGloasEpoch ? spec.Forks.Select(f => f.Epoch == spec.FuluForkEpoch ? new ForkScheduleEntry(f.Version, spec.GloasForkEpoch) : f) : spec.Forks,
            new ForkScheduleEntry(spec.GloasForkVersion, spec.GloasForkEpoch),
        ],
        BlobSchedule = spec.BlobSchedule,
        ElectraForkEpoch = spec.ElectraForkEpoch,
        FuluForkEpoch = fuluInGloasEpoch ? spec.GloasForkEpoch : spec.FuluForkEpoch,
        MaxBlobsPerBlockElectra = spec.MaxBlobsPerBlockElectra,
        GloasForkEpoch = spec.GloasForkEpoch,
        GloasForkVersion = spec.GloasForkVersion,
        Bootnodes = spec.Bootnodes,
    };
}
