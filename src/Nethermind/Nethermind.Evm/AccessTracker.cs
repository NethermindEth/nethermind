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
        public JournalSet<Address> AccessedAddresses { get; }
        public JournalSet<StorageCell> AccessedStorageCells { get;  } 
        public JournalCollection<LogEntry> Logs { get; }
        public JournalSet<Address> DestroyList { get; }
        public HashSet<AddressAsKey> CreateList { get; }

        private int _addressesSnapshots;
        private int _storageKeysSnapshots;
        private int _destroyListSnapshots;
        private int _logsSnapshots;
        public AccessTracker(AccessTracker? accessTracker = null)
        {
            if (accessTracker is not null)
            {
                AccessedAddresses = accessTracker.AccessedAddresses;
                AccessedStorageCells = accessTracker.AccessedStorageCells;
                Logs = accessTracker.Logs;
                DestroyList = accessTracker.DestroyList;
                CreateList = accessTracker.CreateList;
            }
            else
            {
                AccessedAddresses = new();
                AccessedStorageCells = new();
                Logs = new();
                DestroyList = new();
                CreateList = new();
            }
        }
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
            _addressesSnapshots = AccessedAddresses.TakeSnapshot();
            _storageKeysSnapshots = AccessedStorageCells.TakeSnapshot();
            _destroyListSnapshots= DestroyList.TakeSnapshot();
            _logsSnapshots = Logs.TakeSnapshot();
        }

        public void Restore()
        {
            Logs.Restore(_logsSnapshots);
            DestroyList.Restore(_destroyListSnapshots);
            AccessedAddresses.Restore(_addressesSnapshots);
            AccessedStorageCells.Restore(_storageKeysSnapshots);
        }
    }
}
