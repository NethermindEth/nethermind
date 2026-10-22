// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Taiko.ZkGas;

namespace Nethermind.Taiko.TaikoSpec;

public class TaikoChainSpecBasedSpecProvider(ChainSpec chainSpec,
    TaikoChainSpecEngineParameters chainSpecEngineParameters,
    ILogManager logManager)
    : ChainSpecBasedSpecProvider(chainSpec, logManager)
{
    protected override ReleaseSpec CreateEmptyReleaseSpec() => new TaikoReleaseSpec
    {
        TaikoL2Address = Address.Zero
    };

    protected override ReleaseSpec CreateReleaseSpec(ChainSpec chainSpec, ulong releaseStartBlock, ulong? releaseStartTimestamp = null)
    {
        TaikoReleaseSpec releaseSpec = (TaikoReleaseSpec)base.CreateReleaseSpec(chainSpec, releaseStartBlock, releaseStartTimestamp);

        releaseSpec.IsOntakeEnabled = (chainSpecEngineParameters.OntakeTransition ?? ulong.MaxValue) <= releaseStartBlock;
        releaseSpec.IsPacayaEnabled = (chainSpecEngineParameters.PacayaTransition ?? ulong.MaxValue) <= releaseStartBlock;
        releaseSpec.IsShastaEnabled = (chainSpecEngineParameters.ShastaTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.IsUnzenEnabled = (chainSpecEngineParameters.UnzenTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.UnzenBlockZkGasLimit = chainSpecEngineParameters.UnzenBlockZkGasLimit ?? ZkGasSchedule.BlockZkGasLimit;
        releaseSpec.UnzenTxIntrinsicZkGas = chainSpecEngineParameters.UnzenTxIntrinsicZkGas ?? ZkGasSchedule.TxIntrinsicZkGas;

        TaikoUnzenZkGasSchedule? activeSchedule = SelectSchedule(chainSpecEngineParameters.UnzenZkGasSchedules, releaseStartTimestamp);
        releaseSpec.UnzenOpcodeZkGasMultipliers = ZkGasSchedule.BuildOpcodeTable(activeSchedule?.OpcodeMultipliers);
        releaseSpec.UnzenPrecompileZkGasMultipliers = ZkGasSchedule.BuildPrecompileTable(activeSchedule?.PrecompileMultipliers);
        releaseSpec.UseSurgeGasPriceOracle = chainSpecEngineParameters.UseSurgeGasPriceOracle ?? false;
        releaseSpec.TaikoL2Address = chainSpecEngineParameters.TaikoL2Address;
        releaseSpec.IsRip7728Enabled = (chainSpecEngineParameters.Rip7728TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.IsL1StaticCallEnabled = (chainSpecEngineParameters.L1StaticCallTransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

        return releaseSpec;
    }

    /// <summary>
    /// Picks the schedule with the largest <see cref="TaikoUnzenZkGasSchedule.Timestamp"/> not
    /// exceeding <paramref name="releaseStartTimestamp"/>. When every configured schedule activates
    /// in the future, the earliest entry is returned as the floor so a release within Unzen always
    /// has a multiplier table. Returns <c>null</c> when no schedules are configured.
    /// </summary>
    private static TaikoUnzenZkGasSchedule? SelectSchedule(
        IReadOnlyList<TaikoUnzenZkGasSchedule>? schedules,
        ulong? releaseStartTimestamp)
    {
        if (schedules is null || schedules.Count == 0)
        {
            return null;
        }

        TaikoUnzenZkGasSchedule? floor = null;
        TaikoUnzenZkGasSchedule? active = null;
        foreach (TaikoUnzenZkGasSchedule schedule in schedules)
        {
            if (floor is null || schedule.Timestamp < floor.Timestamp)
            {
                floor = schedule;
            }

            if (releaseStartTimestamp is { } ts && schedule.Timestamp <= ts &&
                (active is null || schedule.Timestamp > active.Timestamp))
            {
                active = schedule;
            }
        }

        return active ?? floor;
    }
}
