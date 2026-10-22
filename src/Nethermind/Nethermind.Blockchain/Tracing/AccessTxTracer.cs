// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing;

public class AccessTxTracer(params Address[] addressesToOptimize) : TxTracer
{
    public override bool IsTracingReceipt => true;
    public override bool IsCollectingLogs => false;
    public override bool IsTracingAccess => true;

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs,
        Hash256? stateRoot = null) => GasSpent += gasSpent.SpentGas;

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error,
        Hash256? stateRoot = null) => GasSpent += gasSpent.SpentGas;

    public override void ReportAccess(IEnumerable<Address> accessedAddresses, IEnumerable<StorageCell> accessedStorageCells)
    {
        Dictionary<Address, HashSet<UInt256>?> dictionary = [];
        foreach (Address address in accessedAddresses)
        {
            dictionary.Add(address, null);
        }

        foreach (StorageCell storageCell in accessedStorageCells)
        {
            ref HashSet<UInt256>? set = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, storageCell.Address, out _);
            set ??= new HashSet<UInt256>();
            set.Add(storageCell.Index);
        }

        for (int i = 0; i < addressesToOptimize.Length; i++)
        {
            Address address = addressesToOptimize[i];
            if (dictionary.TryGetValue(address, out HashSet<UInt256>? set) && set is null)
            {
                dictionary.Remove(address);
            }
        }

        AccessList.Builder builder = new();
        foreach ((Address address, HashSet<UInt256>? storageKeys) in dictionary)
        {
            builder.AddAddress(address);
            if (storageKeys is not null)
            {
                foreach (UInt256 storageKey in storageKeys)
                {
                    builder.AddStorage(storageKey);
                }
            }
        }

        AccessList = builder.Build();
    }

    public ulong GasSpent { get; set; }
    public AccessList? AccessList { get; private set; }

    public void Reset()
    {
        GasSpent = 0;
        AccessList = null;
    }
}
