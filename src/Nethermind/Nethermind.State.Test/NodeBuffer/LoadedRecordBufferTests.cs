// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.Store.Test.NodeBuffer;

internal class LoadedRecordBufferTests
{
    private const int NodeCount = 200;
    private const int ValueLength = 1024;
    // Larger than a chunk, so it gets a chunk of its own.
    private const int OversizedValueLength = 100 * 1024;

    [Test]
    public void Loads_rlp_or_records_into_records_that_stay_readable([Values] bool asRecord)
    {
        byte[][] rlps = [.. Enumerable.Range(0, NodeCount).Select(i => Leaf(i, i == NodeCount / 2 ? OversizedValueLength : ValueLength))];
        Store store = new(rlps.Select((rlp, i) => (Path(i), rlp)), asRecord);
        Store invalid = new([(Path(0), Bytes.FromHexString("c28001"))], asRecord: false);
        using LoadedRecordBuffer loaded = new();
        NodeRecord[] records = new NodeRecord[NodeCount];
        bool[] found = new bool[NodeCount];
        for (int i = 0; i < NodeCount; i++) found[i] = loaded.TryLoad(store, Path(i), ValueKeccak.Compute(rlps[i]), out records[i]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.All.True);
            Assert.That(records.Select(record => record.Rlp.ToArray()), Is.EqualTo(rlps), "records stay readable as later ones fill new chunks");
            Assert.That(records.Select(record => record.Hash), Is.EqualTo(rlps.Select(rlp => ValueKeccak.Compute(rlp))));
            Assert.That(loaded.TryLoad(store, Path(NodeCount), default, out _), Is.False, "a store miss");
            Assert.That(() => loaded.TryLoad(invalid, Path(0), default, out _), Throws.TypeOf<TrieException>(), "invalid RLP");
        }
    }

    private static TreePath Path(int seed) => new(ValueKeccak.Compute(BitConverter.GetBytes(seed)), 64);

    private static byte[] Leaf(int seed, int valueLength)
    {
        byte[] value = new byte[valueLength];
        BitConverter.TryWriteBytes(value, seed);
        return Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes([1, 2], isLeaf: true)), Rlp.Encode(value)).Bytes;
    }

    /// <summary>Hands over each node's RLP, or its record when <paramref name="asRecord"/> is set, as a store holding records does.</summary>
    private sealed class Store(IEnumerable<(TreePath Path, byte[] Rlp)> nodes, bool asRecord) : INodeRecordStore
    {
        private readonly Dictionary<TreePath, byte[]> _nodes = nodes.ToDictionary(node => node.Path, node => asRecord ? Record(node.Rlp) : node.Rlp);

        public bool TryLocate(ulong pointer, out NodeRecord record) => throw new NotSupportedException();

        public INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength) => throw new NotSupportedException();

        public bool TryLoad<TArg>(in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            if (!_nodes.TryGetValue(path, out byte[] node)) return false;

            action(node, arg);
            return true;
        }

        private static byte[] Record(byte[] rlp)
        {
            NodeRecordCodec.TryDecode(rlp, out NodeRecordKind kind, out ushort hashMask);
            byte[] record = new byte[NodeRecordCodec.Length(BitOperations.PopCount(hashMask), rlp.Length)];
            NodeRecordCodec.WriteFromRlp(rlp, kind, hashMask, ValueKeccak.Compute(rlp), 0, record);
            return record;
        }
    }
}
