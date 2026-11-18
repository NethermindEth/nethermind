// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Full eth_getProof trie visit for one contract with a large storage trie, by number of requested slots.
/// </summary>
[MemoryDiagnoser]
public class AccountProofVisitBenchmarks
{
    private const ulong StorageSlots = 1 << 18;

    private readonly Address _address = TestItem.AddressA;
    private StateTree _tree = null!;
    private UInt256[] _keys = null!;

    [Params(1, 2, 4, 8, 16, 64, 256, 1024)]
    public int RequestedSlots { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        MemDb db = new();
        StorageTree storageTree = new(new RawScopedTrieStore(db, _address.ToAccountPath.ToCommitment()), Keccak.EmptyTreeHash, LimboLogs.Instance);
        byte[] value = new byte[32];
        for (ulong i = 0; i < StorageSlots; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(24), i + 1);
            storageTree.Set(new UInt256(i), value);
        }
        storageTree.Commit();

        _tree = new StateTree(new RawScopedTrieStore(db), LimboLogs.Instance);
        _tree.Set(_address, Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject);
        _tree.Set(TestItem.AddressB, Build.An.Account.WithBalance(2).TestObject);
        _tree.Commit();

        Random random = new(42);
        Span<byte> sample = stackalloc byte[sizeof(ulong)];
        _keys = new UInt256[RequestedSlots];
        for (int i = 0; i < _keys.Length; i++)
        {
            random.NextBytes(sample);
            _keys[i] = new UInt256(BinaryPrimitives.ReadUInt64LittleEndian(sample) % StorageSlots);
        }
    }

    [Benchmark]
    public AccountProof Collect()
    {
        AccountProofCollector collector = new(_address, _keys);
        _tree.Accept(collector, _tree.RootHash);
        return collector.BuildResult();
    }
}
