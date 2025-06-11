// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Transition;

// merkle -> verkle transition
// this would consist of four phases
// 1. we just have a merkle tree, and we are working on the merkle tree
// 1.5. we prepare the db to have a direct read access to the database for leaves without traversing the tree
// 2. we have a hard fork, and we started using the verkle tree as an overlay tree to do all the operations
// 2.5 we have the finalization of the hard fork block and every node has a set of preimages with them
// 3. after finalization, we start moving batch of leaves from merkle tree to verkle tree (LEAVES_TO_CONVERT)
// 3.5 everything is moved to verkle tree
// 4. starting using only the verkle tree only
// 4.5 finalization of the last conversion block happens
// 5. remove all the residual merkle state
// TRANSITION IS FINISHED


// idea - hide everything behind a single interface that manages both the merkle and verkle tree
// we can pass this interface across the client and this interface can act as the plugin we want,
// but this plugin also needs input for the block we are currently on [ Flush(long blockNumber) ]
// and the spec that is being used [Flush(long blockNumber, IReleaseSpec releaseSpec)


// also design the interface in a way that we can do a proper archive sync ever after the transition
public class TransitionWorldState(
    StateReader merkleStateReader,
    Hash256 finalizedStateRoot,
    VerkleStateTree verkleTree,
    IKeyValueStoreWithBatching codeDb,
    IKeyValueStore preImageDb,
    ILogManager? logManager)
    : VerkleWorldState(new TransitionStorageProvider(merkleStateReader, finalizedStateRoot, verkleTree, logManager),
        verkleTree,
        codeDb, logManager, null, false), TransitionQueryVisitor.IValueCollector
{
    private const int NumberOfLeavesToMove = 7;
    private Hash256 FinalizedMerkleStateRoot { get; } = finalizedStateRoot;

    // TODO: not needed right now
    private readonly IMerkleStateIterator _merkleStateIterator = new MerkleStateIterator(preImageDb);

    private ValueHash256 _startAccountHash = Keccak.Zero;
    private ValueHash256 _startStorageHash = null;

    protected override Account? GetAndAddToCache(Address address, bool onlyVerkle = false)
    {
        if (_nullAccountReads.Contains(address)) return null;
        Account? account = GetState(address);
        if (!onlyVerkle)
        {
            account ??= merkleStateReader.GetAccountDefault(FinalizedMerkleStateRoot, address);
        }

        if (account is not null)
        {
            PushJustCache(address, account);
        }
        else
        {
            // just for tracing - potential perf hit, maybe a better solution?
            _nullAccountReads.Add(address);
        }

        return account;
    }

    /// <summary>
    /// Technically, there is no use for doing this because we are anyway calling the base class.
    /// But this is just a reminder that we don't try to get anything from a merkle tree her because
    /// GetCodeChunk is only called when you are running the stateless client and that is not supported
    /// while the transition is ongoing.
    /// Stateless clients can only work after the transition in complete.
    /// </summary>
    /// <param name="codeOwner"></param>
    /// <param name="chunkId"></param>
    /// <returns></returns>
    public override byte[] GetCodeChunk(Address codeOwner, UInt256 chunkId)
    {
        return base.GetCodeChunk(codeOwner, chunkId);
    }

    public override void SweepLeaves(int blockNumber)
    {
        var visitor = new TransitionQueryVisitor(_startAccountHash, _startStorageHash, this, nodeLimit: NumberOfLeavesToMove);
        merkleStateReader.RunTreeVisitor(visitor, FinalizedMerkleStateRoot);
        _startAccountHash = visitor.CurrentAccountPath.Path;
        _startStorageHash = visitor.CurrentStoragePath.Path;
        Console.WriteLine($"SweepLeaves {visitor.CurrentAccountPath.Path} {visitor.CurrentStoragePath.Path}");
        Tree.Commit();
    }

    // TODO: does not work
    /// <summary>
    /// This uses an iterator on the merkle state to sweep the leaves - this is not implemented yet. Probably after
    /// Paprika or a flatDb layout.
    /// </summary>
    /// <param name="blockNumber"></param>
    public void SweepLeavesIterator(long blockNumber)
    {
        // have to figure out how to know the starting point
        using IEnumerator<(Address, Account)>?  accountIterator = _merkleStateIterator.GetAccountIterator(Keccak.Zero).GetEnumerator();
        IEnumerator<(StorageCell, byte[])>? currentStorageIterator = null;
        int i = 0;
        while (i < NumberOfLeavesToMove)
        {
            if (currentStorageIterator is not null)
            {
                if (currentStorageIterator.MoveNext())
                {
                    Tree.SetStorage(currentStorageIterator.Current.Item1, currentStorageIterator.Current.Item2);
                    i++;
                }
                else
                {
                    currentStorageIterator.Dispose();
                    currentStorageIterator = null;
                }
            }
            else
            {
                if (accountIterator.MoveNext())
                {
                    SetState(accountIterator.Current.Item1, accountIterator.Current.Item2, true);

                    if (accountIterator.Current.Item2.StorageRoot != Keccak.EmptyTreeHash)
                    {
                        currentStorageIterator = _merkleStateIterator
                            .GetStorageSlotsIterator(accountIterator.Current.Item1, Keccak.Zero).GetEnumerator();
                    }
                    i++;
                }
                else
                {
                    return;
                }
            }
        }
    }

    private bool AccountExistsVerkle(Address address)
    {
        if (IntraBlockCache.TryGetValue(address, out Stack<int>? value))
        {
            return Changes[value.Peek()]!.ChangeType != ChangeType.Delete;
        }

        return GetAndAddToCache(address, true) is not null;
    }

    private bool CodeExistsVerkle(Address address)
    {
        if (IntraBlockCache.TryGetValue(address, out Stack<int>? value))
        {
            return Changes[value.Peek()]!.ChangeType != ChangeType.Delete;
        }

        var account = GetAndAddToCache(address, true);

        try
        {
            GetCodeChunk(address, 0);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return true;
    }
    public void CollectAccount(in ValueHash256 path, CappedArray<byte> value)
    {
        Console.WriteLine($"CollectAccount {path} {value.ToArray().ToHexString()}");
        var addressBytes = preImageDb.Get(path.BytesAsSpan);
        if (addressBytes is null) throw new ArgumentException("PreImage not found");
        var address = new Address(addressBytes);
        Console.WriteLine($"Address {address}");
        if (AccountExistsVerkle(address))
        {
            Console.WriteLine($"AccountExistsVerkle {address}");
            return;
        }
        SetState(address, AccountDecoder.Instance.Decode(value), true);
    }

    public void CollectStorage(in ValueHash256 account, in ValueHash256 path, CappedArray<byte> value)
    {

        var addressBytes = preImageDb.Get(account.BytesAsSpan);
        if (addressBytes is null) throw new ArgumentException("PreImage not found");
        var address = new Address(addressBytes);
        Rlp.ValueDecoderContext rlp = value.AsSpan().AsRlpValueContext();
        var valueU = rlp.DecodeByteArray();
        Console.WriteLine($"CollectStorage {account} {path} {valueU.ToHexString()} {address}");
        var index = preImageDb.Get(path.BytesAsSpan);
        var storageCell = new StorageCell(address, new UInt256(index, true));
        ReadOnlySpan<byte> data = Get(storageCell);
        Hash256 theKey = AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, storageCell.Index);

        var isPresent = ValuePresentInTree(theKey);
        if (!isPresent)
        {
            Set(new StorageCell(address, new UInt256(index, true)), valueU);
        }
    }

    public int CollectCode(in ValueHash256 path, Hash256 codeHash)
    {
        // Console.WriteLine($"CollectCode {path} {codeHash}");
        var addressBytes = preImageDb.Get(path.BytesAsSpan);
        if (addressBytes is null) throw new ArgumentException("PreImage not found");
        var address = new Address(addressBytes);
        var code = _codeDb[codeHash.Bytes];
        Tree.SetCode(address, code);
        return (int)Math.Ceiling((double)code.Length / 31);
    }
}
