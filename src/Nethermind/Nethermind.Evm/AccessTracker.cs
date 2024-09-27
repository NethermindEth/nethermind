// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public sealed class AccessTracker
    {        
        public readonly JournalSet<Address> AccessedAddresses = new();
        public readonly JournalSet<StorageCell> AccessedStorageCells = new();
        public readonly JournalCollection<LogEntry> Logs = new();
        public readonly JournalSet<Address> DestroyList = new();
        public readonly HashSet<AddressAsKey> CreateList = new();

        private Queue<int> _addressesSnapshots = new();
        private Queue<int> _storageKeysSnapshots = new();
        private Queue<int> _destroyListSnapshots = new();
        private Queue<int> _logsSnapshots = new();

        public bool IsCold(Address? address) => !AccessedAddresses.Contains(address);

        public bool IsCold(in StorageCell storageCell) => !AccessedStorageCells.Contains(storageCell);

        public void Add(Address address)
        {
            AccessedAddresses.Add(address);
        }

        public void Add(StorageCell storageCell)
        {
            AccessedStorageCells.Add(storageCell);
        }

        public void Add(AccessList? accessList)
        {
            if (accessList?.IsEmpty == false)
            {
                foreach ((Address address, AccessList.StorageKeysEnumerable storages) in accessList)
                {
                    AccessedAddresses.Add(address);
                    foreach (UInt256 storage in storages)
                    {
                        AccessedStorageCells.Add(new StorageCell(address, storage));
                    }
                }
            }
        }

        public void TakeSnapshot()
        {
            _addressesSnapshots.Enqueue(AccessedAddresses.TakeSnapshot());
            _storageKeysSnapshots.Enqueue(AccessedStorageCells.TakeSnapshot());
            _destroyListSnapshots.Enqueue(DestroyList.TakeSnapshot());
            _logsSnapshots.Enqueue(Logs.TakeSnapshot());
        }

        public void Restore()
        {
            if (_addressesSnapshots.Count == 0)
                throw new InvalidOperationException("No snapshots available to restore.");
            Logs.Restore(_logsSnapshots.Dequeue());
            DestroyList.Restore(_destroyListSnapshots.Dequeue());
            AccessedAddresses.Restore(_addressesSnapshots.Dequeue());
            AccessedStorageCells.Restore(_storageKeysSnapshots.Dequeue());
        }
    }
}
