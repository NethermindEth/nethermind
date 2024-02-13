// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;

namespace Nethermind.State.Transition;

public class MerkleStateIterator : IMerkleStateIterator
{
    public IKeyValueStore _preImageDb;

    public MerkleStateIterator(IKeyValueStore preImageDb)
    {
        _preImageDb = preImageDb;
    }
    public IEnumerable<(Address, Account)> GetAccountIterator(Hash256 startAddressKey)
    {
        IEnumerable<(Hash256, Account)>? accountIterator = GetKeccakAccountIterator(startAddressKey);
        foreach ((Hash256? addrHash, Account? account) in accountIterator)
        {
            byte[] addr = _preImageDb[addrHash.Bytes] ?? throw new ArgumentException($"cannot find preimage for the hash {addrHash}");
            yield return (new Address(addr), account);
        }
    }

    public IEnumerable<(StorageCell, byte[])> GetStorageSlotsIterator(Address addressKey, Hash256 startIndexHash)
    {
        IEnumerable<(Hash256, byte[])>? accountIterator = GetKeccakStorageIterator(Keccak.Compute(addressKey.Bytes), startIndexHash);
        foreach ((Hash256? slotIndexHash, byte[] slotValue) in accountIterator)
        {
            byte[] slotKey = _preImageDb[slotIndexHash.Bytes] ?? throw new ArgumentException($"cannot find preimage for the hash {slotIndexHash}");
            StorageCell storageCell = new StorageCell(addressKey, new UInt256(slotKey));
            yield return (storageCell, slotValue);
        }
    }

    private IEnumerable<(Hash256, Account)> GetKeccakAccountIterator(Hash256 startAddressKey)
    {
        throw new NotImplementedException();
    }

    private IEnumerable<(Hash256, byte[])> GetKeccakStorageIterator(Hash256 startAddressKey, Hash256 startIndexHash)
    {
        throw new NotImplementedException();
    }
}
