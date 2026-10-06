// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Image;
using NUnit.Framework;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Test;

/// <summary>Shared access to the EIP-8347 fixture: its directory, genesis, block metadata and snapshot state.</summary>
internal static class Eip8347FixtureState
{
    public static string Directory => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Eip8347");

    public static ChainSpec LoadChainSpec()
    {
        using Stream genesis = typeof(Eip8347FixtureState).Assembly.GetManifestResourceStream("Nethermind.State.Pbt.Test.Fixtures.Eip8347.genesis.json")!;
        return new GethGenesisLoader(new EthereumJsonSerializer()).Load(genesis);
    }

    public static JsonElement Metadata(string name)
    {
        using JsonDocument blocks = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Directory, "blocks.json")));
        foreach (JsonElement block in blocks.RootElement.EnumerateArray())
            if (block.GetProperty("name").GetString() == name) return block.Clone();
        throw new ArgumentException("Unknown fixture", nameof(name));
    }

    /// <summary>Writes the logical state of a fixture snapshot, addressed through its preimages, as the pre-genesis to <paramref name="anchor"/> transition.</summary>
    public static void ReplayInto(IPersistence persistence, IKeyValueStore codes, BlockHeader anchor, Stream snapshot, Stream preimages)
    {
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(anchor), WriteFlags.None);
        Dictionary<PbtStorageTreeKey, ValueHash256> leaves = [];
        foreach (RebuildEntry entry in PbtSnapshotCodec.ReadLeaves(snapshot)) leaves.Add(entry.Key, entry.Leaf);
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
                batch.SetStorage(address!, slot, value);
                slots.Add(new(ValueKeccak.Compute(rawSlot.Bytes), Rlp.Encode(value).Bytes));
            }
            ValueHash256 storageRoot = MptRightmostNodeStore.CalculateRoot(slots, MptRightmostNodeStore.DefaultWindowSize, CancellationToken.None);
            Account account = new(nonce, balance, storageRoot.ToHash256(), Keccak.Compute(code));
            batch.SetAccount(address!, account);
            if (code.Length != 0) codes[account.CodeHash.Bytes] = code;
        }
    }
}
