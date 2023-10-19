// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Collections.EliasFano;
using Nethermind.Core.Crypto;

namespace Nethermind.Db;

public class EliasFanoStorage
{
    private class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[] x, byte[] y)
        {
            if (x.Length != y.Length)
            {
                return false;
            }

            for (int i = 0; i < x.Length; i++)
            {
                if (x[i] != y[i])
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(byte[] obj)
        {
            int hash = 17;
            foreach (byte b in obj)
            {
                hash = hash * 31 + b;
            }

            return hash;
        }
    }

    private Dictionary<byte[], EliasFano> storage;

    public EliasFanoStorage()
    {
        storage = new Dictionary<byte[], EliasFano>(new ByteArrayComparer());
    }

    public EliasFano Get(byte[] key)
    {
        if (storage.ContainsKey(key))
        {
            return storage[key];
        }
        else
        {
            return new EliasFano();
        }
    }

    // Add single a
    // block number to the storage.
    public void Put(byte[] key, ulong new_val)
    {
        if (storage.ContainsKey(key))
        {
            List<ulong> curr_list = storage[key].GetEnumerator(0).ToList();
            curr_list.Add(new_val);
            EliasFanoBuilder efb = new EliasFanoBuilder(curr_list.Max() + 1, curr_list.Count);
            curr_list.Sort();
            efb.Extend(curr_list);
            storage[key] = efb.Build();
        }
        else
        {
            EliasFanoBuilder efb = new EliasFanoBuilder(new_val + 1, 1);
            efb.Push(new_val);
            storage[key] = efb.Build();
        }
    }

    // Add a list of block numbers to storage.
    public void PutAll(byte[] key, List<ulong> values)
    {
        if (storage.ContainsKey(key))
        {
            List<ulong> curr_list = storage[key].GetEnumerator(0).ToList();
            curr_list.AddRange(values);
            EliasFanoBuilder efb = new EliasFanoBuilder(curr_list.Max() + 1, curr_list.Count);
            curr_list.Sort();
            efb.Extend(curr_list);
            storage[key] = efb.Build();
        }
        else
        {
            EliasFanoBuilder efb = new EliasFanoBuilder(values.Max() + 1, values.Count);
            efb.Extend(values);
            EliasFano ef = efb.Build();
            storage[key] = ef;
        }
    }

    public IEnumerable<long> Match(long? startBlock, long? endBlock, IEnumerable<Address>? addresses,
        IEnumerable<IEnumerable<Keccak>> topics)
    {
        // Basically we add a address enumerator after collecting the block numbers
        // We add a topic enumerator for each First level of the list AFTER we unionize the second levels with OR
        List<List<long>> enumerators = new List<List<long>>();
        IEnumerable<long> resultBlockNumbers = new List<long>();

        // IEnumerable<long> addressesBlockNumbersFromIndex = new List<long>();
        SortedSet<long> addressesBlockNumbersFromIndex = new SortedSet<long>();

        // Add all block numbers from the addresses in our list of addresses. (OR relation)
        foreach (Address addr in addresses)
        {
            IEnumerable<ulong> temp = Get(addr.Bytes).GetEnumerator(0);
            foreach (long t in temp)
            {
                if (startBlock != null && endBlock != null)
                {
                    if (startBlock <= t && t <= endBlock)
                    {
                        addressesBlockNumbersFromIndex.Add(t);
                    }
                }
                else if (startBlock != null)
                {
                    if (startBlock <= t)
                    {
                        addressesBlockNumbersFromIndex.Add(t);
                    }
                }
                else if (endBlock != null)
                {
                    if (t <= endBlock)
                    {
                        addressesBlockNumbersFromIndex.Add(t);
                    }
                }
                else
                {
                    addressesBlockNumbersFromIndex.Add(t);
                }
            }
        }

        enumerators.Add(addressesBlockNumbersFromIndex.ToList());
        // First level of the list represents AND logical condition, second level represents OR logical condition.
        // So we should union the second levels
        // But we should iterate over first levels at the same time?
        foreach (IEnumerable<Keccak> topic in topics)
        {
            SortedSet<long> temp = new SortedSet<long>();
            foreach (Keccak t in topic)
            {
                // List of block numbers
                foreach (long bn in Get(t.Bytes.ToArray()).GetEnumerator(0))
                {
                    if (startBlock != null && endBlock != null)
                    {
                        if (startBlock <= bn && bn <= endBlock)
                        {
                            temp.Add(bn);
                        }
                    }
                    else if (startBlock != null)
                    {
                        if (startBlock <= bn)
                        {
                            temp.Add(bn);
                        }
                    }
                    else if (endBlock != null)
                    {
                        if (bn <= endBlock)
                        {
                            temp.Add(bn);
                        }
                    }
                    else
                    {
                        temp.Add(bn);
                    }
                }
            }

            enumerators.Add(temp.ToList());
        }

        // Loop to find matching block numbers
        // if they are all pointing to the same block -> yield return that block
        // advance the lowest one

        // Use a variable to store the current index of each enumerator
        int[] indices = new int[enumerators.Count];

        bool done = false;
        while (!done)
        {
            // Use a variable to store the current block number of each enumerator
            long[] blocks = new long[enumerators.Count];

            // Use a loop to get the current block number of each enumerator
            for (int i = 0; i < enumerators.Count; i++)
            {
                if (indices[i] >= enumerators[i].Count)
                {
                    blocks[i] = -1;
                }
                else
                {
                    blocks[i] = enumerators[i][indices[i]];
                }
            }

            long minBlock = long.MaxValue;

            // Use a loop to find the minimum block number among all enumerators
            for (int i = 0; i < blocks.Length; i++)
            {
                if (blocks[i] != -1 && blocks[i] < minBlock)
                {
                    minBlock = blocks[i];
                }
            }

            // Check if the minimum block number is valid
            if (minBlock != long.MaxValue)
            {
                bool sameBlock = true;

                // Use a loop to check whether all enumerators have the same block number
                for (int i = 0; i < blocks.Length; i++)
                {
                    if (blocks[i] != minBlock)
                    {
                        sameBlock = false;
                        break;
                    }
                }

                // Check if all enumerators have the same block number -> add it to the result
                if (sameBlock)
                {
                    resultBlockNumbers = resultBlockNumbers.Append(minBlock);
                }

                // Use a loop to advance the enumerators that have the minimum block number
                for (int i = 0; i < blocks.Length; i++)
                {
                    if (blocks[i] == minBlock)
                    {
                        indices[i]++;
                    }
                }
            }
            else
            {
                done = true;
            }
        }

        return resultBlockNumbers;
    }
}
