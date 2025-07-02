// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class AccessTxTracer(params Address[] addressesToOptimize) : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public override bool IsTracingAccess => true;

        public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            GasSpent += gasSpent.SpentGas;
        }

        public override void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            GasSpent += gasSpent.SpentGas;
        }

        public override void ReportAccess(IReadOnlyCollection<Address> accessedAddresses, IReadOnlyCollection<StorageCell> accessedStorageCells)
        {
            Dictionary<Address, ISet<UInt256>> dictionary = new();
            foreach (Address address in accessedAddresses)
            {
                dictionary.Add(address, new HashSet<UInt256>());
            }

            foreach (StorageCell storageCell in accessedStorageCells)
            {
                if (!dictionary.TryGetValue(storageCell.Address, out ISet<UInt256> set))
                {
                    dictionary[storageCell.Address] = set = new HashSet<UInt256>();
                }

                set.Add(storageCell.Index);
            }

            for (int i = 0; i < addressesToOptimize.Length; i++)
            {
                Address address = addressesToOptimize[i];
                if (dictionary.TryGetValue(address, out ISet<UInt256> set) && set.Count == 0)
                {
                    dictionary.Remove(address);
                }
            }

            AccessList.Builder builder = new();
            foreach ((Address address, ISet<UInt256> storageKeys) in dictionary)
            {
                builder.AddAddress(address);
                foreach (UInt256 storageKey in storageKeys)
                {
                    builder.AddStorage(storageKey);
                }
            }
            AccessList = builder.Build();
        }

        public long GasSpent { get; set; }
        public AccessList? AccessList { get; private set; }
    }
}
