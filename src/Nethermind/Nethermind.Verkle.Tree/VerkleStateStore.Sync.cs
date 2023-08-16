// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.Utils;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree;

public partial class VerkleStateStore
{
    public bool IsFullySynced(Keccak stateRoot) => StateRootToBlocks[new VerkleCommitment(stateRoot.Bytes.ToArray())] != -2;

    public IEnumerable<PathWithSubTree> GetLeafRangeIterator(Stem fromRange, Stem toRange, VerkleCommitment stateRoot, long bytes)
    {
        if(bytes == 0)  yield break;

        long blockNumber = StateRootToBlocks[stateRoot];
        byte[] fromRangeBytes = new byte[32];
        byte[] toRangeBytes = new byte[32];
        fromRange.BytesAsSpan.CopyTo(fromRangeBytes);
        toRange.BytesAsSpan.CopyTo(toRangeBytes);
        fromRangeBytes[31] = 0;
        toRangeBytes[31] = 255;

        using LeafEnumerator enumerator = GetLeafRangeIterator(fromRangeBytes, toRangeBytes, blockNumber).GetEnumerator();

        int usedBytes = 0;

        HashSet<Stem> listOfStem = new();
        Stem currentStem = fromRange;
        List<LeafInSubTree> subTree = new(256);

        while (enumerator.MoveNext())
        {
            KeyValuePair<byte[], byte[]> current = enumerator.Current;
            if (listOfStem.Contains(current.Key.Slice(0,31)))
            {
                subTree.Add(new LeafInSubTree(current.Key[31], current.Value));
                usedBytes += 31;
            }
            else
            {
                if (subTree.Count != 0) yield return new PathWithSubTree(currentStem, subTree.ToArray());
                if (usedBytes >= bytes) break;
                subTree.Clear();
                currentStem = new Stem(current.Key.Slice(0,31).ToArray());
                listOfStem.Add(currentStem);
                subTree.Add(new LeafInSubTree(current.Key[31], current.Value));
                usedBytes += 31 + 33;
            }
        }
        if (subTree.Count != 0) yield return new PathWithSubTree(currentStem, subTree.ToArray());
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> GetLeafRangeIterator(byte[] fromRange, byte[] toRange, long blockNumber)
    {
        if(BlockCache is null) yield break;

        // this will contain all the iterators that we need to fulfill the GetSubTreeRange request
        List<LeafEnumerator> iterators = new();

        // kvMap is used to keep a map of keyValues we encounter - this is for ease of access - but not optimal
        // TODO: remove this - merge kvMap and kvEnumMap
        Dictionary<byte[], KeyValuePair<int, byte[]>> kvMap = new(Bytes.EqualityComparer);
        // this created a sorted structure for all the keys and the corresponding enumerators. the idea is that get
        // the first key (sorted), remove the key, then move the enumerator to next and insert the new key and
        // enumerator again
        DictionarySortedSet<byte[], LeafIterator> keyEnumMap = new(Bytes.Comparer);

        // TODO: optimize this to start from a specific blockNumber - or better yet get the list of enumerators directly
        using StackQueue<(long, ReadOnlyVerkleMemoryDb)>.StackEnumerator blockEnumerator =
            BlockCache.GetStackEnumerator();
        try
        {
            int iteratorPriority = 0;
            while (blockEnumerator.MoveNext())
            {
                // enumerate till we get to the required block number
                if(blockEnumerator.Current.Item1 > blockNumber) continue;

                // TODO: here we construct a set from the LeafTable so that we can do the GetViewBetween
                //   obviously this is very un-optimal but the idea is to replace the LeafTable with SortedSet in the
                //   blockCache itself. The reason we want to use GetViewBetween because this is optimal to do seek
                DictionarySortedSet<byte[], byte[]> currentSet = new (blockEnumerator.Current.Item2.LeafTable, Bytes.Comparer);

                // construct the iterators that starts for the specific range using GetViewBetween
                IEnumerator<KeyValuePair<byte[],byte[]>> enumerator = currentSet
                    .GetViewBetween(
                        new KeyValuePair<byte[], byte[]>(fromRange, Pedersen.Zero.Bytes),
                        new KeyValuePair<byte[], byte[]>(toRange, Pedersen.Zero.Bytes))
                    .GetEnumerator();

                // find the first value in iterator that is not already used
                bool isIteratorUsed = false;
                while (enumerator.MoveNext())
                {
                    KeyValuePair<byte[], byte[]> current = enumerator.Current;
                    // add the key and corresponding value
                    if (kvMap.TryAdd(current.Key, new(iteratorPriority, current.Value)))
                    {
                        isIteratorUsed = true;
                        iterators.Add(enumerator);
                        // add the new key and the corresponding enumerator
                        keyEnumMap.Add(current.Key, new(enumerator, iteratorPriority));
                        break;
                    }
                }
                if (!isIteratorUsed)
                {
                    enumerator.Dispose();
                    continue;
                }
                iteratorPriority++;
            }

            LeafEnumerator persistentLeafsIterator = Storage.LeafDb.GetIterator(fromRange, toRange).GetEnumerator();
            bool isPersistentIteratorUsed = false;
            while (persistentLeafsIterator.MoveNext())
            {
                KeyValuePair<byte[], byte[]> current = persistentLeafsIterator.Current;
                // add the key and corresponding value
                if (kvMap.TryAdd(current.Key, new(iteratorPriority, current.Value)))
                {
                    isPersistentIteratorUsed = true;
                    iterators.Add(persistentLeafsIterator);
                    // add the new key and the corresponding enumerator
                    keyEnumMap.Add(current.Key, new (persistentLeafsIterator, iteratorPriority));
                    break;
                }
            }
            if (!isPersistentIteratorUsed)
            {
                persistentLeafsIterator.Dispose();
            }

            void InsertAndMoveIteratorRecursive(LeafIterator leafIterator)
            {
                while (leafIterator.Enumerator.MoveNext())
                {
                    KeyValuePair<byte[], byte[]> newKeyValuePair = leafIterator.Enumerator.Current;
                    byte[] newKeyToInsert = newKeyValuePair.Key;
                    // now here check if the value already exist and if the priority of value of higher or lower and
                    // update accordingly
                    KeyValuePair<int, byte[]> valueToInsert = new(leafIterator.Priority, newKeyValuePair.Value);

                    if (kvMap.TryGetValue(newKeyToInsert, out KeyValuePair<int, byte[]> valueExisting))
                    {
                        // priority of the new value is smaller (more) than the priority of old value
                        if (valueToInsert.Key < valueExisting.Key)
                        {
                            keyEnumMap.TryGetValue(newKeyValuePair.Key, out LeafIterator? prevIterator);
                            keyEnumMap.Remove(newKeyValuePair.Key);

                            // replace the existing value
                            keyEnumMap.Add(newKeyValuePair.Key, leafIterator);
                            kvMap[newKeyValuePair.Key] = valueToInsert;

                            // since we replacing the existing value, we need to move the prevIterator iterator to
                            // next value till we get the new value
                            InsertAndMoveIteratorRecursive(prevIterator);
                            break;
                        }

                        // since we were not able to add current value from this iterator, move to next value and try
                        // to add that
                    }
                    else
                    {
                        // this is the most simple case
                        // since there was no existing value - we just insert without modifying other iterators
                        keyEnumMap.Add(newKeyValuePair.Key, leafIterator);
                        kvMap.Add(newKeyValuePair.Key, valueToInsert);
                        break;
                    }
                }
            }

            while (keyEnumMap.Count > 0)
            {
                // get the first value from the sorted set
                KeyValuePair<byte[], LeafIterator> value = keyEnumMap.Min;
                // remove the corresponding element because it will be used
                keyEnumMap.Remove(value.Key);

                // get the enumerator and move it to next and insert the corresponding values recursively
                InsertAndMoveIteratorRecursive(value.Value);

                byte[] returnValue = kvMap[value.Key].Value;
                kvMap.Remove(value.Key);

                // return the value
                yield return new KeyValuePair<byte[], byte[]> (value.Key, returnValue);
            }
        }
        finally
        {
            foreach (LeafEnumerator t in iterators) t.Dispose();
        }
    }

    public List<PathWithSubTree>? GetLeafRangeIterator(byte[] fromRange, byte[] toRange, VerkleCommitment stateRoot, long bytes)
    {
        long blockNumber = StateRootToBlocks[stateRoot];
        using IEnumerator<KeyValuePair<byte[], byte[]>> ranges = GetLeafRangeIterator(fromRange, toRange, blockNumber).GetEnumerator();

        long currentBytes = 0;

        SpanDictionary<byte, List<LeafInSubTree>> rangesToReturn = new(Bytes.SpanEqualityComparer);

        if (!ranges.MoveNext()) return null;

        // handle the first element
        Span<byte> stem = ranges.Current.Key.AsSpan()[..31];
        rangesToReturn.TryAdd(stem, new List<LeafInSubTree>());
        rangesToReturn[stem].Add(new LeafInSubTree(ranges.Current.Key[31], ranges.Current.Value!));
        currentBytes += 64;


        bool bytesConsumed = false;
        while (ranges.MoveNext())
        {
            if (currentBytes > bytes)
            {
                bytesConsumed = true;
                break;
            }
        }

        if (bytesConsumed)
        {
            // this means the iterator is not empty but the bytes is consumed, now we need to complete the current
            // subtree we are processing
            while (ranges.MoveNext())
            {
                // if stem is present that means we have to complete that subTree
                stem = ranges.Current.Key.AsSpan()[..31];
                if (rangesToReturn.TryGetValue(stem, out List<LeafInSubTree>? listOfLeafs))
                {
                    listOfLeafs.Add(new LeafInSubTree(ranges.Current.Key[31], ranges.Current.Value!));
                    continue;
                }
                break;
            }
        }

        List<PathWithSubTree> pathWithSubTrees = new(rangesToReturn.Count);
        foreach (KeyValuePair<byte[], List<LeafInSubTree>> keyVal in rangesToReturn)
        {
            pathWithSubTrees.Add(new PathWithSubTree(keyVal.Key, keyVal.Value.ToArray()));
        }

        return pathWithSubTrees;
    }


}
