// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State.Transition;

// MERKLE -> VERKLE transition
// Start: We just have a merkle tree and we are working on the merkle tree
// 1. we prepare the db to have a direct read access to the database for leaves without traversing the tree
// 2. we have a hard fork and we started using the verkle tree as a overlay tree to do all the operations
// ---- we have the finalization of the hard fork block and every node has a set of preimages with them
// 3. after finalization we start moving batch of leaves from merkle tree to verkle tree (LEAVES_TO_CONVERT)
// ---- everything is moved to verkle tree
// 4. starting using only the verkle tree only
// ---- finalization of the last conversion block happens
// 5. remove all the residual merkle state
// >>>> TRANSITION IS FINISHED


// idea - hide everything behind a single interface that manages both the merkle and verkle tree
// we can pass this interface across the client and this interface can act as the plugin we want
// but this plugin also needs input for the block we are currently on [ Flush(long blockNumber) ]
// and the spec that is being used [Flush(long blockNumber, IReleaseSpec releaseSpec)


// also design the interface in a way that we can do a proper archive sync ever after the transition


public class TransitionWorldState(
    StateReader merkleStateReader,
    Hash256 finalizedStateRoot,
    VerkleStateTree verkleTree,
    IKeyValueStore codeDb,
    IKeyValueStore preImageDb,
    ILogManager? logManager)
    : VerkleWorldState(new TransitionStorageProvider(merkleStateReader, finalizedStateRoot, verkleTree, logManager),
        verkleTree,
        codeDb, logManager)
{
    private const int NumberOfLeavesToMove = 5000;
    private Hash256 FinalizedMerkleStateRoot { get; } = finalizedStateRoot;
    private readonly IMerkleStateIterator _merkleStateIterator = new MerkleStateIterator(preImageDb);

    protected override Account? GetAndAddToCache(Address address)
    {
        if (_nullAccountReads.Contains(address)) return null;
        Account? account = GetState(address)?? merkleStateReader.GetAccountDefault(FinalizedMerkleStateRoot, address);
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
    /// Technically there is not use for doing this because we are anyways calling the base class.
    /// But, this is just a reminder that we dont try to get anything from merkle tree her because
    /// GetCodeChunk is only called when you are running the stateless client and that is not supported
    /// while the transition in ongoing. Stateless clients can only work after the transition in complete.
    /// </summary>
    /// <param name="codeOwner"></param>
    /// <param name="chunkId"></param>
    /// <returns></returns>
    public override byte[] GetCodeChunk(Address codeOwner, UInt256 chunkId)
    {
        return base.GetCodeChunk(codeOwner, chunkId);
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="blockNumber"></param>
    public void SweepLeaves(long blockNumber)
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
                    _tree.SetStorage(currentStorageIterator.Current.Item1, currentStorageIterator.Current.Item2);
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
                    Account? accountToBeInserted = accountIterator.Current.Item2;
                    if (accountToBeInserted.CodeHash != Keccak.OfAnEmptyString)
                    {
                        accountToBeInserted.Code = _codeDb[accountToBeInserted.CodeHash.Bytes];
                    }
                    SetState(accountIterator.Current.Item1, accountIterator.Current.Item2);

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
}

