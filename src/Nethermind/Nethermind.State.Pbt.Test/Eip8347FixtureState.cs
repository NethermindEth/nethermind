// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Pbt.Image;

namespace Nethermind.State.Pbt.Test;

/// <summary>Replays the logical state of an EIP-8347 fixture snapshot, addressed through its preimages.</summary>
internal static class Eip8347FixtureState
{
    public static void Replay(Stream snapshot, Stream preimages, Action<Address, Account, byte[]> account, Action<Address, UInt256, UInt256> storage)
    {
        (_, ulong count) = PbtSnapshotCodec.ReadHeader(snapshot);
        Dictionary<PbtStorageTreeKey, ValueHash256> leaves = [];
        foreach (RebuildEntry entry in PbtSnapshotCodec.ReadLeaves(snapshot, count)) leaves.Add(entry.Key, entry.Leaf);
        PbtPreimageReader reader = new(preimages);
        while (reader.ReadAccount(out Address? address, out uint slotCount))
        {
            ValueHash256 basic = leaves.GetValueOrDefault((PbtStorageTreeKey)PbtStateKey.Account(address!, PbtKeyDerivation.BasicDataLeafKey));
            PbtKeyDerivation.UnpackBasicData(basic.Bytes, out ulong nonce, out UInt256 balance);
            int size = (int)PbtKeyDerivation.ReadBasicDataCodeSize(basic.Bytes);
            byte[] code = new byte[size];
            if (leaves.TryGetValue((PbtStorageTreeKey)PbtStateKey.Account(address!, PbtKeyDerivation.DelegationLeafKey), out ValueHash256 delegation))
                delegation.Bytes[..size].CopyTo(code);
            else
            {
                ValueHash256 codeHash = leaves[(PbtStorageTreeKey)PbtStateKey.Account(address!, PbtKeyDerivation.CodeHashLeafKey)];
                for (int chunk = 0; chunk * 31 < size; chunk++)
                    if (leaves.TryGetValue((PbtStorageTreeKey)PbtStateKey.Code(codeHash, chunk), out ValueHash256 chunkValue))
                        chunkValue.Bytes.Slice(1, Math.Min(31, size - chunk * 31)).CopyTo(code.AsSpan(chunk * 31));
            }

            List<KeyValuePair<ValueHash256, byte[]>> slots = [];
            for (uint index = 0; index < slotCount; index++)
            {
                ValueHash256 rawSlot = reader.ReadSlot();
                UInt256 slot = new(rawSlot.Bytes, isBigEndian: true);
                UInt256 value = new(leaves[PbtStateKey.Storage(address!, slot)].Bytes, isBigEndian: true);
                storage(address!, slot, value);
                slots.Add(new(ValueKeccak.Compute(rawSlot.Bytes), Rlp.Encode(value).Bytes));
            }
            ValueHash256 storageRoot = MptRightmostNodeStore.CalculateRoot(slots, MptRightmostNodeStore.DefaultWindowSize, CancellationToken.None);
            account(address!, new Account(nonce, balance, storageRoot.ToHash256(), Keccak.Compute(code)), code);
        }
    }
}
