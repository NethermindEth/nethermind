// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Core.Test.Builders
{
    public class AccountChangesBuilder : BuilderBase<AccountChanges>
    {
        public AccountChangesBuilder()
        {
            TestObjectInternal = new AccountChanges(Address.Zero);
        }

        public AccountChangesBuilder WithAddress(Address address)
        {
            TestObjectInternal.Address = address;
            return this;
        }

        public AccountChangesBuilder WithStorageReads(params UInt256[] keys)
        {
            foreach (UInt256 key in keys)
            {
                TestObjectInternal.AddStorageRead(key);
            }
            return this;
        }


        public AccountChangesBuilder WithStorageChanges(UInt256 key, params StorageChange[] storageChanges)
        {
            SlotChanges slotChanges = TestObjectInternal.GetOrAddSlotChanges(key);
            foreach (StorageChange storageChange in storageChanges)
            {
                slotChanges.Changes.Add(storageChange.BlockAccessIndex, storageChange);
            }
            return this;
        }

        public AccountChangesBuilder WithNonceChanges(params NonceChange[] nonceChanges)
        {
            foreach (NonceChange nonceChange in nonceChanges)
            {
                TestObjectInternal.AddNonceChange(nonceChange);
            }
            return this;
        }

        public AccountChangesBuilder WithBalanceChanges(params BalanceChange[] balanceChanges)
        {
            foreach (BalanceChange balanceChange in balanceChanges)
            {
                TestObjectInternal.AddBalanceChange(balanceChange);
            }
            return this;
        }

        public AccountChangesBuilder WithCodeChanges(params CodeChange[] codeChanges)
        {
            foreach (CodeChange codeChange in codeChanges)
            {
                TestObjectInternal.AddCodeChange(codeChange);
            }
            return this;
        }
    }
}
