// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Evm.T8n;

public class StorageTxTracer : TxTracer, IBlockTracer
{
    private readonly Dictionary<Address, List<UInt256>> _storages = new();
    public bool IsTracingRewards => false;
    public override bool IsTracingOpLevelStorage => true;

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue,
        ReadOnlySpan<byte> currentValue)
    {
        if (!_storages.TryGetValue(address, out _))
        {
            _storages[address] = [];
        }

        _storages[address].Add(storageIndex);
    }

    public List<UInt256> GetStorageIndexes(Address address)
    {
        _storages.TryGetValue(address, out List<UInt256>? storage);
        return storage ?? [];
    }

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public void StartNewBlockTrace(Block block) { }

    public ITxTracer StartNewTxTrace(Transaction? tx) => this;

    public void EndTxTrace() { }

    public void EndBlockTrace() { }
}
