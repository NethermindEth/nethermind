// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class AccessTxTracer : TxTracer
    {
        private const long ColdVsWarmSLoadDelta = GasCostOf.ColdSLoad - GasCostOf.AccessStorageListEntry;
        public const long MaxStorageAccessToOptimize = GasCostOf.AccessAccountListEntry / ColdVsWarmSLoadDelta;

        private readonly Address[] _addressesToOptimize;

        public override bool IsTracingReceipt => true;
        public override bool IsTracingAccess => true;

        public AccessTxTracer(params Address[] addressesToOptimize)
        {
            _addressesToOptimize = addressesToOptimize;
        }

        public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            GasSpent += gasSpent;
        }

        public override void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
        {
            GasSpent += gasSpent;
        }

        public override void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
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

            for (int i = 0; i < _addressesToOptimize.Length; i++)
            {
                Address address = _addressesToOptimize[i];
                if (dictionary.TryGetValue(address, out ISet<UInt256> set) && set.Count <= MaxStorageAccessToOptimize)
                {
                    GasSpent += (GasCostOf.ColdSLoad - GasCostOf.WarmStateRead) * set.Count;
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
