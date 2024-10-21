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
        public IReadOnlySet<Address> AccessedAddresses => _accessedAddresses;
        public IReadOnlySet<StorageCell> AccessedStorageCells => _accessedStorageCells;
        public ICollection<LogEntry> Logs => _logs;
        public IReadOnlySet<Address> DestroyList => _destroyList;
        public IReadOnlySet<AddressAsKey> CreateList => _createList; 
        private JournalSet<Address> _accessedAddresses { get; }
        private JournalSet<StorageCell> _accessedStorageCells { get; }
        private JournalCollection<LogEntry> _logs { get; }
        private JournalSet<Address> _destroyList { get; }
        private HashSet<AddressAsKey> _createList { get; }

        private int _addressesSnapshots;
        private int _storageKeysSnapshots;
        private int _destroyListSnapshots;
        private int _logsSnapshots;
        public AccessTracker(AccessTracker? accessTracker = null)
        {
            if (accessTracker is not null)
            {
                _accessedAddresses = accessTracker._accessedAddresses;
                _accessedStorageCells = accessTracker._accessedStorageCells;
                _logs = accessTracker._logs;
                _destroyList = accessTracker._destroyList;
                _createList = accessTracker._createList;
            }
            else
            {
                _accessedAddresses = new();
                _accessedStorageCells = new();
                _logs = new();
                _destroyList = new();
                _createList = new();
            }
        }
        public bool IsCold(Address? address) => !AccessedAddresses.Contains(address);

        public bool IsCold(in StorageCell storageCell) => !_accessedStorageCells.Contains(storageCell);

        public void WarmUp(Address address)
        {
            _accessedAddresses.Add(address);
        }

        public void WarmUp(StorageCell storageCell)
        {
            _accessedStorageCells.Add(storageCell);
        }

        public void WarmUp(AccessList? accessList)
        {
            if (accessList?.IsEmpty == false)
            {
                foreach ((Address address, AccessList.StorageKeysEnumerable storages) in accessList)
                {
                    _accessedAddresses.Add(address);
                    foreach (UInt256 storage in storages)
                    {
                        _accessedStorageCells.Add(new StorageCell(address, storage));
                    }
                }
            }
        }

        public void ToBeDestroyed(Address address)
        {
            _destroyList.Add(address);
        }

        public void WasCreated(Address address)
        {
            _createList.Add(address);
        }

        public void TakeSnapshot()
        {
            _addressesSnapshots = _accessedAddresses.TakeSnapshot();
            _storageKeysSnapshots = _accessedStorageCells.TakeSnapshot();
            _destroyListSnapshots = _destroyList.TakeSnapshot();
            _logsSnapshots = _logs.TakeSnapshot();
        }

        public void Restore()
        {
            _logs.Restore(_logsSnapshots);
            _destroyList.Restore(_destroyListSnapshots);
            _accessedAddresses.Restore(_addressesSnapshots);
            _accessedStorageCells.Restore(_storageKeysSnapshots);
        }
    }
}
