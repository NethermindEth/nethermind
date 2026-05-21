// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Core.Test.Builders
{
    public class AccountChangesBuilder : BuilderBase<ReadOnlyAccountChanges>
    {
        private Address _address = Address.Zero;
        // Per-slot working buffer keyed by slot; we materialize into a sorted ReadOnlySlotChanges[]
        // (sorted by slot key) on every Rebuild so the produced BAL matches the on-wire invariant.
        private readonly SortedDictionary<UInt256, List<StorageChange>> _slotChangesScratch
            = new(GenericComparer.GetOptimized<UInt256>());
        private readonly List<UInt256> _storageReads = [];
        private readonly List<BalanceChange> _balanceChanges = [];
        private readonly List<NonceChange> _nonceChanges = [];
        private readonly List<CodeChange> _codeChanges = [];

        public AccountChangesBuilder() => Rebuild();

        public AccountChangesBuilder WithAddress(Address address)
        {
            _address = address;
            Rebuild();
            return this;
        }

        public AccountChangesBuilder WithStorageReads(params UInt256[] keys)
        {
            _storageReads.AddRange(keys);
            Rebuild();
            return this;
        }

        public AccountChangesBuilder WithStorageChanges(UInt256 key, params StorageChange[] storageChanges)
        {
            if (!_slotChangesScratch.TryGetValue(key, out List<StorageChange>? scratch))
            {
                scratch = [];
                _slotChangesScratch.Add(key, scratch);
            }
            scratch.AddRange(storageChanges);
            Rebuild();
            return this;
        }

        public AccountChangesBuilder WithNonceChanges(params NonceChange[] nonceChanges)
        {
            _nonceChanges.AddRange(nonceChanges);
            Rebuild();
            return this;
        }

        public AccountChangesBuilder WithBalanceChanges(params BalanceChange[] balanceChanges)
        {
            _balanceChanges.AddRange(balanceChanges);
            Rebuild();
            return this;
        }

        public AccountChangesBuilder WithCodeChanges(params CodeChange[] codeChanges)
        {
            _codeChanges.AddRange(codeChanges);
            Rebuild();
            return this;
        }

        private void Rebuild()
        {
            ReadOnlySlotChanges[] orderedStorageChanges = _slotChangesScratch
                .Select(kv => new ReadOnlySlotChanges(kv.Key, [.. kv.Value]))
                .ToArray();
            TestObjectInternal = new ReadOnlyAccountChanges(
                _address,
                orderedStorageChanges,
                [.. _storageReads],
                [.. _balanceChanges],
                [.. _nonceChanges],
                [.. _codeChanges]);
        }
    }
}
