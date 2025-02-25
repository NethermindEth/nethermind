// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Evm.T8n;

public class StorageTxTracer : TxTracer, IBlockTracer
{
    public readonly Dictionary<Address, List<UInt256>> Storages = new();
    public bool IsTracingRewards => false;
    public override bool IsTracingOpLevelStorage => true;

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue,
        ReadOnlySpan<byte> currentValue)
    {
        if (!Storages.TryGetValue(address, out _))
        {
            Storages[address] = [];
        }

        Storages[address].Add(storageIndex);
    }

    public List<UInt256> GetStorageKeys(Address address)
    {
        Storages.TryGetValue(address, out List<UInt256>? storage);
        return storage ?? [];
    }

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public void StartNewBlockTrace(Block block) { }

    public ITxTracer StartNewTxTrace(Transaction? tx) => this;

    public void EndTxTrace() { }

    public void EndBlockTrace() { }
}
