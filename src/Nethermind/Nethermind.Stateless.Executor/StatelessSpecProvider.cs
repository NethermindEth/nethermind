// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

internal sealed class StatelessSpecProvider(
    IForkAwareSpecProvider baseProvider,
    ForkActivation activeForkActivation,
    IReleaseSpec activeForkSpec)
    : ISpecProvider
{
    public ForkActivation? MergeBlockNumber => baseProvider.MergeBlockNumber;

    public ulong TimestampFork => baseProvider.TimestampFork;

    public UInt256? TerminalTotalDifficulty => baseProvider.TerminalTotalDifficulty;

    public IReleaseSpec GenesisSpec => baseProvider.GenesisSpec;

    public long? DaoBlockNumber => baseProvider.DaoBlockNumber;

    public ulong? BeaconChainGenesisTimestamp => baseProvider.BeaconChainGenesisTimestamp;

    public ulong NetworkId => baseProvider.NetworkId;

    public ulong ChainId => baseProvider.ChainId;

    public ForkActivation[] TransitionActivations => baseProvider.TransitionActivations;

    public IReleaseSpec GetSpec(ForkActivation activation) =>
        activation >= activeForkActivation ? activeForkSpec : baseProvider.GetSpec(activation);

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null) =>
        baseProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);

    public static StatelessSpecProvider Create(IForkAwareSpecProvider baseProvider, ForkConfig forkConfig)
    {
        string? forkName = GetForkNameByIndex(forkConfig.Fork);

        if (forkName is null || !baseProvider.TryGetForkSpec(forkName, out IReleaseSpec? spec))
            throw new ArgumentException($"Unknown fork: {forkConfig.Fork}", nameof(forkConfig));

        spec = forkConfig.BlobSchedule is [{ } blobSchedule]
           ? new StatelessReleaseSpec(spec!, blobSchedule)
           : spec;

        return new(baseProvider, forkConfig.Activation.ToForkActivation(), spec!);
    }

    private static string? GetForkNameByIndex(ulong index) => index switch
    {
        0 => Frontier.Instance.Name,
        1 => Homestead.Instance.Name,
        2 => Dao.Instance.Name,
        3 => TangerineWhistle.Instance.Name,
        4 => SpuriousDragon.Instance.Name,
        5 => Byzantium.Instance.Name,
        7 => ConstantinopleFix.Instance.Name, // TODO: -1 for removed Constantinople
        8 => Istanbul.Instance.Name,
        9 => MuirGlacier.Instance.Name,
        10 => Berlin.Instance.Name,
        11 => London.Instance.Name,
        12 => ArrowGlacier.Instance.Name,
        13 => GrayGlacier.Instance.Name,
        14 => Paris.Instance.Name,
        15 => Shanghai.Instance.Name,
        16 => Cancun.Instance.Name,
        17 => Prague.Instance.Name,
        18 => Osaka.Instance.Name,
        19 => BPO1.Instance.Name,
        20 => BPO2.Instance.Name,
        24 => Amsterdam.Instance.Name,  // TODO: -3 for removed BPOs
        _ => null
    };

    private sealed class StatelessReleaseSpec(IReleaseSpec spec, BlobSchedule blobSchedule) : ReleaseSpecDecorator(spec)
    {
        public override ulong TargetBlobCount => blobSchedule.Target;
        public override ulong MaxBlobCount => blobSchedule.Max;
        public override UInt256 BlobBaseFeeUpdateFraction => new(blobSchedule.BaseFeeUpdateFraction);
    }
}
