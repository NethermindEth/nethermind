// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

using Nethermind.Core;
using Nethermind.Int256;
using System.Collections.Generic;

namespace Nethermind.Evm.Tracing;

public class FeesTracer : TxTracer, IBlockTracer, IJournal<int>
{
    public override bool IsTracingFees => true;

    public UInt256 Fees { get; private set; } = UInt256.Zero;
    public UInt256 BurntFees { get; private set; } = UInt256.Zero;
    private readonly List<(UInt256 Fees, UInt256 BurntFees)> _snapshots = [];

    public override void ReportFees(UInt256 fees, UInt256 burntFees)
    {
        Fees += fees;
        BurntFees += burntFees;
    }

    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public void StartNewBlockTrace(Block block)
    {
        Fees = UInt256.Zero;
        BurntFees = UInt256.Zero;
        _snapshots.Clear();
    }

    public ITxTracer StartNewTxTrace(Transaction? tx) => this;

    public void EndTxTrace() { }

    public void EndBlockTrace() { }

    public int TakeSnapshot()
    {
        _snapshots.Add((Fees, BurntFees));
        return _snapshots.Count - 1;
    }

    public void Restore(int snapshot)
    {
        (Fees, BurntFees) = _snapshots[snapshot];
        _snapshots.RemoveRange(snapshot, _snapshots.Count - snapshot);
    }
}
