// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Test.Builders
{
    public class BlockAccessListBuilder : BuilderBase<ReadOnlyBlockAccessList>
    {
        private readonly SortedList<Address, ReadOnlyAccountChanges> _accounts = new(GenericComparer.GetOptimized<Address>());

        public BlockAccessListBuilder() => Rebuild();

        public BlockAccessListBuilder WithAccountChanges(params ReadOnlyAccountChanges[] accountChanges)
        {
            foreach (ReadOnlyAccountChanges a in accountChanges)
            {
                _accounts[a.Address] = a;
            }
            Rebuild();
            return this;
        }

        public BlockAccessListBuilder WithPrecompileChanges(Hash256 parentHash, ulong timestamp)
            => WithAccountChanges(
                Eip2935Changes(parentHash),
                Eip4788Changes(timestamp),
                Eip7002Changes,
                Eip7251Changes,
                Eip8282BuilderDepositChanges,
                Eip8282BuilderExitChanges);

        private static ReadOnlyAccountChanges Eip2935Changes(Hash256 parentHash)
        {
            StorageChange parentHashStorageChange = new(0, new UInt256(parentHash.BytesToArray(), isBigEndian: true));
            return Build.An.AccountChanges
                .WithAddress(Eip2935Constants.BlockHashHistoryAddress)
                .WithStorageChanges(0, parentHashStorageChange)
                .TestObject;
        }

        private static ReadOnlyAccountChanges Eip4788Changes(ulong timestamp)
        {
            UInt256 eip4788Slot1 = timestamp % Eip4788Constants.RingBufferSize;
            UInt256 eip4788Slot2 = (timestamp % Eip4788Constants.RingBufferSize) + Eip4788Constants.RingBufferSize;

            return Build.An.AccountChanges
                .WithAddress(Eip4788Constants.BeaconRootsAddress)
                .WithStorageChanges(eip4788Slot1, [new(0, timestamp)])
                .WithStorageReads(eip4788Slot2)
                .TestObject;
        }

        private static readonly ReadOnlyAccountChanges Eip7002Changes = Build.An.AccountChanges
            .WithAddress(Eip7002Constants.WithdrawalRequestPredeployAddress)
            .WithStorageReads(0, 1, 2, 3)
            .TestObject;

        private static readonly ReadOnlyAccountChanges Eip7251Changes = Build.An.AccountChanges
            .WithAddress(Eip7251Constants.ConsolidationRequestPredeployAddress)
            .WithStorageReads(0, 1, 2, 3)
            .TestObject;

        private static readonly ReadOnlyAccountChanges Eip8282BuilderDepositChanges = Build.An.AccountChanges
            .WithAddress(Eip8282Constants.BuilderDepositRequestPredeployAddress)
            .WithStorageReads(0, 1, 2, 3)
            .TestObject;

        private static readonly ReadOnlyAccountChanges Eip8282BuilderExitChanges = Build.An.AccountChanges
            .WithAddress(Eip8282Constants.BuilderExitRequestPredeployAddress)
            .WithStorageReads(0, 1, 2, 3)
            .TestObject;

        private void Rebuild()
        {
            ReadOnlyAccountChanges[] ordered = new ReadOnlyAccountChanges[_accounts.Count];
            int itemCount = 0;
            int i = 0;
            foreach (KeyValuePair<Address, ReadOnlyAccountChanges> kv in _accounts)
            {
                ordered[i++] = kv.Value;
                itemCount += 1 + kv.Value.StorageChanges.Length + kv.Value.StorageReads.Length;
            }
            TestObjectInternal = new ReadOnlyBlockAccessList(ordered, itemCount);
        }
    }
}
