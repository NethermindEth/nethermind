// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using static Nethermind.Benchmarks.State.FlatWorldStateBenchmarkHarness;

namespace Nethermind.Benchmarks.State;

/// <summary>Serves snap account and storage ranges of the response byte limit from a flat state and serializes each reply.</summary>
/// <remarks>
/// The state has <see cref="AccountCount"/> accounts, where every <see cref="ContractInterval"/>th one carries a code hash and a
/// storage root, <see cref="SmallContractCount"/> contracts of <see cref="SmallContractSlots"/> slots and one contract of
/// <see cref="LargeContractSlots"/> slots. Slot values are 1 to 32 bytes long.
/// </remarks>
[MemoryDiagnoser]
[MinIterationCount(10)]
[MaxIterationCount(30)]
public class SnapRangeServingBenchmark
{
    private const int AccountCount = 300_000;
    private const int ContractInterval = 4;
    private const int SmallContractCount = 64;
    private const int SmallContractSlots = 1_000;
    private const int LargeContractSlots = 100_000;
    private const int ReplyBufferSize = 4 * 1024 * 1024;
    private static readonly ValueHash256 MidRange = new("0x8000000000000000000000000000000000000000000000000000000000000000");

    private readonly AccountRangeMessageSerializer _accountSerializer = new();
    private readonly StorageRangesMessageSerializer _storageSerializer = new();
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private INodeRecordBuffer _nodeRecordBuffer = null!;
    private SnapFlatStateServer _server = null!;
    private Hash256 _rootHash = null!;
    private PathWithAccount[] _smallContracts = null!;
    private PathWithAccount[] _largeContract = null!;
    private byte[] _reply = null!;

    [GlobalSetup]
    public void Setup()
    {
        NodeStorage nodeStorage = new(new MemDb());
        StateTree stateTree = new(new RawScopedTrieStore(nodeStorage), NullLogManager.Instance);
        for (int i = 0; i < AccountCount; i++)
        {
            Address address = DeriveAddress(i);
            Account account = i % ContractInterval == 0
                ? new Account((ulong)i, (UInt256)i * 1_000_000_007, BuildStorage(nodeStorage, Keccak.Compute(address.Bytes), 1), Keccak.Compute(BitConverter.GetBytes(~i)))
                : new Account((ulong)(i % 100), (UInt256)i);
            stateTree.Set(address, account);
        }

        _smallContracts = new PathWithAccount[SmallContractCount];
        for (int i = 0; i < SmallContractCount; i++)
        {
            _smallContracts[i] = AddContract(nodeStorage, stateTree, AccountCount + i, SmallContractSlots);
        }

        _largeContract = [AddContract(nodeStorage, stateTree, AccountCount + SmallContractCount, LargeContractSlots)];
        stateTree.Commit();

        StateId stateId = new(0, stateTree.RootHash.ValueHash256);
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        RocksDbPersistence persistence = new(_columnsDb, NullLogManager.Instance, NullTrieNodeLog.Instance);
        new Importer(nodeStorage, persistence, NullLogManager.Instance).Copy(stateId).GetAwaiter().GetResult();

        _nodeRecordBuffer = new NodeRecordBuffer(0, NodeRecordBuffer.DefaultGenerationBytes);
        _rootHash = stateId.StateRoot.ToCommitment();
        _server = new SnapFlatStateServer(
            new SnapFlatStateServerBenchmark.InMemoryFlatDbManager(persistence, _nodeRecordBuffer, []),
            new SnapFlatStateServerBenchmark.SingleStateRootIndex(stateId),
            NullLogManager.Instance);
        _reply = new byte[ReplyBufferSize];
    }

    private static PathWithAccount AddContract(NodeStorage nodeStorage, StateTree stateTree, int index, int slotCount)
    {
        Address address = DeriveAddress(index);
        Hash256 addressHash = Keccak.Compute(address.Bytes);
        Account account = new(1, 0, BuildStorage(nodeStorage, addressHash, slotCount), Keccak.Compute(address.Bytes));
        stateTree.Set(address, account);
        return new PathWithAccount(addressHash, account);
    }

    private static Hash256 BuildStorage(NodeStorage nodeStorage, Hash256 addressHash, int slotCount)
    {
        StorageTree storageTree = new(new RawScopedTrieStore(nodeStorage, addressHash), NullLogManager.Instance);
        byte[] value = new byte[32];
        for (int slot = 0; slot < slotCount; slot++)
        {
            Span<byte> slotValue = value.AsSpan(0, 1 + slot % 32);
            slotValue.Fill((byte)(slot % 255 + 1));
            storageTree.Set((UInt256)slot, slotValue);
        }

        storageTree.Commit();
        return storageTree.RootHash;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _nodeRecordBuffer.Dispose();
        _columnsDb.Dispose();
    }

    [Benchmark]
    public int AccountRangeFromStart() => ServeAccounts(ValueKeccak.Zero);

    [Benchmark]
    public int AccountRangeFromMiddle() => ServeAccounts(MidRange);

    [Benchmark]
    public int StorageRangesFromStart() => ServeStorage(_smallContracts, ValueKeccak.Zero);

    [Benchmark]
    public int StorageRangeFromMiddle() => ServeStorage(_largeContract, MidRange);

    private int ServeAccounts(in ValueHash256 startingHash)
    {
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            _server.GetAccountRanges(_rootHash, startingHash, null, ISnapStateServer.HardResponseByteLimit, CancellationToken.None);
        using AccountRangeMessage message = new() { RequestId = 1, PathsWithAccounts = accounts, Proofs = proofs };
        int length = _accountSerializer.GetLength(message);
        return _accountSerializer.Serialize(_reply.AsSpan(0, length), message);
    }

    private int ServeStorage(PathWithAccount[] accounts, in ValueHash256 startingHash)
    {
        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList proofs) =
            _server.GetStorageRanges(_rootHash, accounts, startingHash, null, ISnapStateServer.HardResponseByteLimit, CancellationToken.None);
        using StorageRangeMessage message = new() { RequestId = 1, Slots = slots, Proofs = proofs ?? EmptyByteArrayList.Instance };
        int length = _storageSerializer.GetLength(message);
        return _storageSerializer.Serialize(_reply.AsSpan(0, length), message);
    }
}
