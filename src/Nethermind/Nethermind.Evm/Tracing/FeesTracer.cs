// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public class FeesTracer : TxTracer, IBlockTracer
{
    public override bool IsTracingFees => true;

    public UInt256 Fees { get; private set; } = UInt256.Zero;
    public UInt256 BurntFees { get; private set; } = UInt256.Zero;

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
    }

    public ITxTracer StartNewTxTrace(Transaction? tx) => this;

    public void EndTxTrace() { }

    public void EndBlockTrace() { }
}
