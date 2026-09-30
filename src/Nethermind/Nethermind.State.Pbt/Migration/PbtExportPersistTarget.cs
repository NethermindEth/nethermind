// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.State.Flat;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Pins flat persistence to the EIP-8347 export anchor.</summary>
/// <remarks>Unpinned until the export step resolves the anchor, which it cannot do before the node is live:
/// the default anchor is whatever is persisted at that point. The persistence worker reads this on its own
/// thread, hence the volatile handoff.</remarks>
public sealed class PbtExportPersistTarget(IPbtConfig config, IFlatDbConfig flatConfig) : IPersistTarget
{
    private const long Unpinned = -1;

    private long _targetBlock = Unpinned;

    public ulong? TargetBlock
    {
        get
        {
            long target = Volatile.Read(ref _targetBlock);
            return target == Unpinned ? null : (ulong)target;
        }
    }

    public ulong StepDistance { get; } = config.ExportStepDistance > 0 ? (ulong)config.ExportStepDistance : flatConfig.CompactSize;

    public void PinTo(ulong blockNumber) => Volatile.Write(ref _targetBlock, (long)blockNumber);
}
