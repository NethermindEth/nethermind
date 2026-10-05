// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Test.IO;

namespace Nethermind.BeaconChain.Test.Sync;

internal sealed class GloasCheckpointFiles : IDisposable
{
    private readonly TempPath _directory = TempPath.GetTempDirectory();

    private GloasCheckpointFiles(byte[] stateSsz, byte[]? blockSsz, byte[]? postStateSsz = null)
    {
        Directory.CreateDirectory(_directory.Path);
        StateFile = Path.Combine(_directory.Path, "anchor.ssz");
        File.WriteAllBytes(StateFile, stateSsz);
        if (blockSsz is not null)
        {
            File.WriteAllBytes(Path.ChangeExtension(StateFile, ".block.ssz"), blockSsz);
        }

        if (postStateSsz is not null)
        {
            File.WriteAllBytes(PostStateFile, postStateSsz);
        }
    }

    /// <summary>Include the Gloas version in the schedule so checkpoint decoding can resolve fork.current_version.</summary>
    public static BeaconChainSpec Spec { get; } = WithGloasScheduled(ForkCrossingChain.Instance.Spec, fuluInGloasEpoch: false);

    public static BeaconChainSpec SharedActivationEpochSpec { get; } = WithGloasScheduled(ForkCrossingChain.Instance.Spec, fuluInGloasEpoch: true);

    public string StateFile { get; }

    public string PostStateFile => Path.ChangeExtension(StateFile, ".post-state.ssz");

    public static GloasCheckpointFiles Write(BeaconStateGloas state, ForkedSignedBeaconBlock? block) =>
        new(BeaconStateGloas.Encode(state), block is null ? null : SignedBeaconBlockCodec.Encode(block, Spec));

    public static GloasCheckpointFiles Write(byte[] stateSsz, ForkedSignedBeaconBlock block, byte[]? postStateSsz = null) =>
        new(stateSsz, SignedBeaconBlockCodec.Encode(block, Spec), postStateSsz);

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
