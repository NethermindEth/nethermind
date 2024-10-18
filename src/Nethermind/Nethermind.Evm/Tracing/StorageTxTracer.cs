using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public class StorageTxTracer : TxTracer, IBlockTracer
{
    public Dictionary<Address, Dictionary<UInt256, byte[]>> Storages = new();
    public bool IsTracingRewards => false;
    public override bool IsTracingOpLevelStorage => true;

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue,
        ReadOnlySpan<byte> currentValue)
    {
        if (!Storages.TryGetValue(address, out _))
        {
            Storages[address] = [];
        }

        Storages[address][storageIndex] = newValue.ToArray();
    }


    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public void StartNewBlockTrace(Block block) { }

    public ITxTracer StartNewTxTrace(Transaction? tx) => this;

    public void EndTxTrace() { }

    public void EndBlockTrace() { }
}
