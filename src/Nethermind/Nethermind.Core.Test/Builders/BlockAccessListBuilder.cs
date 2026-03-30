// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Test.Builders
{
    public class BlockAccessListBuilder : BuilderBase<BlockAccessList>
    {

        public BlockAccessListBuilder()
        {
            TestObjectInternal = new BlockAccessList();
        }

        public BlockAccessListBuilder WithAccountChanges(params AccountChanges[] accountChanges)
        {
            TestObjectInternal.AddAccountChanges(accountChanges);
            return this;
        }


        public BlockAccessListBuilder WithPrecompileChanges(Hash256 parentHash, ulong timestamp)
        {
            TestObjectInternal.AddAccountChanges([
                Eip2935Changes(parentHash),
                Eip4788Changes(timestamp),
                Eip7002Changes,
                Eip7251Changes
            ]);
            return this;
        }

        private static AccountChanges Eip2935Changes(Hash256 parentHash)
        {
            StorageChange parentHashStorageChange = new(0, new UInt256(parentHash.BytesToArray(), isBigEndian: true));
            return Build.An.AccountChanges
                .WithAddress(Eip2935Constants.BlockHashHistoryAddress)
                .WithStorageChanges(0, parentHashStorageChange)
                .TestObject;
        }
        private static AccountChanges Eip4788Changes(ulong timestamp)
        {
            UInt256 eip4788Slot1 = timestamp % Eip4788Constants.RingBufferSize;
            UInt256 eip4788Slot2 = (timestamp % Eip4788Constants.RingBufferSize) + Eip4788Constants.RingBufferSize;

            return Build.An.AccountChanges
                .WithAddress(Eip4788Constants.BeaconRootsAddress)
                .WithStorageChanges(eip4788Slot1, [new(0, timestamp)])
                .WithStorageReads(eip4788Slot2)
                .TestObject;
        }
        private readonly AccountChanges Eip7002Changes = Build.An.AccountChanges
            .WithAddress(Eip7002Constants.WithdrawalRequestPredeployAddress)
            .WithStorageReads(0, 1, 2, 3)
            .TestObject;
        private readonly AccountChanges Eip7251Changes = Build.An.AccountChanges
            .WithAddress(Eip7251Constants.ConsolidationRequestPredeployAddress)
            .WithStorageReads(0, 1, 2, 3)
            .TestObject;
    }
}
