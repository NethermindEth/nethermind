// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;

namespace Nethermind.Network.Benchmarks;

/// <summary>Collects the trie leaves of a snap range response and serializes the response, as a snap server does.</summary>
/// <remarks>The trie walk is left out, so that only the per-leaf work of the collectors and the serializers is measured.</remarks>
[MemoryDiagnoser]
public class SnapRangeLeafBenchmarks
{
    /// <summary>The leaves of one response: 40,000 fill about 2 MB, the hard response byte limit.</summary>
    [Params(100, 40_000)]
    public int LeafCount { get; set; }

    private ValueHash256[] _accountPaths = null!;
    private byte[][] _accountLeaves = null!;
    private ValueHash256[] _slotPaths = null!;
    private byte[][] _slotLeaves = null!;
    private byte[] _destination = null!;
    private readonly AccountRangeMessageSerializer _accountSerializer = new();
    private readonly StorageRangesMessageSerializer _storageSerializer = new();

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(1);
        _accountPaths = new ValueHash256[LeafCount];
        _accountLeaves = new byte[LeafCount][];
        for (int i = 0; i < LeafCount; i++)
        {
            _accountPaths[i] = Keccak.Compute(BitConverter.GetBytes(i)).ValueHash256;
            ulong nonce = (ulong)random.Next(0, 2000);
            UInt256 balance = (UInt256)(ulong)random.NextInt64() * (ulong)random.Next(1, 1000);
            // About one account in seven is a contract.
            Account account = i % 7 == 0
                ? new Account(nonce, balance, Keccak.Compute(BitConverter.GetBytes(-i)), Keccak.Compute(BitConverter.GetBytes(i + 1)))
                : new Account(nonce, balance);
            _accountLeaves[i] = AccountDecoder.Instance.Encode(account);
        }

        Array.Sort(_accountPaths);
        _slotPaths = new ValueHash256[LeafCount];
        _slotLeaves = new byte[LeafCount][];
        for (int i = 0; i < LeafCount; i++)
        {
            _slotPaths[i] = Keccak.Compute(BitConverter.GetBytes(i + LeafCount)).ValueHash256;
            byte[] value = new byte[random.Next(1, 33)];
            random.NextBytes(value);
            value[0] |= 1;
            _slotLeaves[i] = Rlp.Encode(value);
        }

        Array.Sort(_slotPaths);
        _destination = new byte[8 * 1024 * 1024];
    }

    [Benchmark]
    public int AccountRange()
    {
        AccountCollector collector = new(ISnapStateServer.HardResponseByteLimit);
        for (int i = 0; i < _accountLeaves.Length; i++) collector.Collect(_accountPaths[i], _accountLeaves[i]);
        using AccountRangeMessage message = new() { PathsWithAccounts = collector.Accounts, Proofs = EmptyByteArrayList.Instance };
        return _accountSerializer.Serialize(_destination, message);
    }

    [Benchmark]
    public int StorageRange()
    {
        EncodedStorageRanges ranges = new(1);
        PathWithStorageCollector collector = ranges.Collector;
        for (int i = 0; i < _slotLeaves.Length; i++) collector.Collect(_slotPaths[i], _slotLeaves[i]);
        ranges.TryCompleteAccount();
        using StorageRangeMessage message = new() { Slots = ranges, Proofs = EmptyByteArrayList.Instance };
        return _storageSerializer.Serialize(_destination, message);
    }

    /// <summary>A storage range response with the slots of 1,200 accounts, the most that the snap client asks for in one request.</summary>
    [Benchmark]
    public int StorageRangeManyAccounts()
    {
        int accountCount = Math.Min(_slotLeaves.Length, 1_200);
        EncodedStorageRanges ranges = new(accountCount);
        for (int account = 0; account < accountCount; account++)
        {
            int start = account * _slotLeaves.Length / accountCount;
            CollectSlots(ranges.Collector, _slotPaths.AsSpan(start..((account + 1) * _slotLeaves.Length / accountCount)), _slotLeaves.AsSpan(start));
            ranges.TryCompleteAccount();
        }

        using StorageRangeMessage message = new() { Slots = ranges, Proofs = EmptyByteArrayList.Instance };
        return _storageSerializer.Serialize(_destination, message);
    }

    private static void CollectSlots(PathWithStorageCollector collector, ReadOnlySpan<ValueHash256> paths, ReadOnlySpan<byte[]> leaves)
    {
        for (int i = 0; i < paths.Length; i++) collector.Collect(paths[i], leaves[i]);
    }
}
