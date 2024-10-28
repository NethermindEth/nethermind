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
    public struct StackAccessTracker
    {
        public readonly IReadOnlySet<Address> AccessedAddresses => _accessedAddresses;
        public readonly IReadOnlySet<StorageCell> AccessedStorageCells => _accessedStorageCells;
        public readonly ICollection<LogEntry> Logs => _logs;
        public readonly IReadOnlySet<Address> DestroyList => _destroyList;
        public readonly IReadOnlySet<AddressAsKey> CreateList => _createList;
        private readonly JournalSet<Address> _accessedAddresses;
        private readonly JournalSet<StorageCell> _accessedStorageCells;
        private readonly JournalCollection<LogEntry> _logs;
        private readonly JournalSet<Address> _destroyList;
        private readonly HashSet<AddressAsKey> _createList;

        private int _addressesSnapshots;
        private int _storageKeysSnapshots;
        private int _destroyListSnapshots;
        private int _logsSnapshots;

        public StackAccessTracker(in StackAccessTracker accessTracker)
        {
            _accessedAddresses = accessTracker._accessedAddresses;
            _accessedStorageCells = accessTracker._accessedStorageCells;
            _logs = accessTracker._logs;
            _destroyList = accessTracker._destroyList;
            _createList = accessTracker._createList;
        }

        public StackAccessTracker()
        {
            _accessedAddresses = new();
            _accessedStorageCells = new();
            _logs = new();
            _destroyList = new();
            _createList = new();
        }
        public readonly bool IsCold(Address? address) => !AccessedAddresses.Contains(address);

        public readonly bool IsCold(in StorageCell storageCell) => !_accessedStorageCells.Contains(storageCell);

        public readonly void WarmUp(Address address)
        {
            _accessedAddresses.Add(address);
        }

        public readonly void WarmUp(in StorageCell storageCell)
        {
            _accessedStorageCells.Add(storageCell);
        }

        public readonly void WarmUp(AccessList? accessList)
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
