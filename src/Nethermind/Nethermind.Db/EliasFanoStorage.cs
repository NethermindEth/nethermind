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
    private Dictionary<int, EliasFano> storage;

    public EliasFanoStorage()
    {
        storage = new Dictionary<int, EliasFano>();
    }

    public EliasFano Get(int key)
    {
        // if (storage.ContainsKey(key))
        // {
        return storage[key];
        // }
        // TODO: what to return in case of no key?
    }

    // Add single a
    // block number to the storage.
    public void Put(int key, ulong new_val)
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
    public void PutAll(int key, List<ulong> values)
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

    public List<ulong> FindBlockNumbers(int address, int topic)
    {
        EliasFano addressEf = Get(address);
        EliasFano topicEf = Get(topic);
        // Store results.
        List<ulong> matchingValues = new List<ulong>();
        // Get the list of values for iteration. May need to change to just use EliasFanoIterator.
        List<ulong> addressList = addressEf.GetEnumerator(0).ToList();
        List<ulong> topicList = topicEf.GetEnumerator(0).ToList();
        int i = 0;
        int j = 0;
        while (i < addressList.Count() && j < topicList.Count())
        {
            if (addressList[i] == topicList[j])
            {
                matchingValues.Add(topicList[j]);
                i++;
                j++;
            }
            else if (addressList[i] < topicList[j])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return matchingValues;
    }

    public IEnumerable<long> Match(long? startBlock, long? endBlock, IEnumerable<Address>? addresses,
        IEnumerable<IEnumerable<Keccak>> topics)
    {
        // Basically we add a address enumerator after collecting the block numbers
        // We add a topic enumerator for each First level of the list AFTER we unionize the second levels with OR
        List<List<long>> enumerators = new List<List<long>>();
        IEnumerable<long> resultBlockNumbers = new List<long>();
        IEnumerable<long> addressesBlockNumbersFromIndex = new List<long>();

        // Add all block numbers from the addresses in our list of addresses. (OR relation)
        foreach (Address addr in addresses)
        {
            IEnumerable<ulong> temp = Get(addr.GetHashCode()).GetEnumerator(0);
            foreach (ulong t in temp)
            {
                if (startBlock != null && endBlock != null)
                {
                    long? tc = (long?)t;
                    if (!temp.Contains(t) && startBlock <= tc && tc <= endBlock)
                    {
                        addressesBlockNumbersFromIndex = addressesBlockNumbersFromIndex.Append((long)t);
                    }
                }
                else
                {
                    // if (!temp.Contains(t))
                    // {
                        addressesBlockNumbersFromIndex = addressesBlockNumbersFromIndex.Append((long)t);
                    // }
                }
            }
        }

        enumerators.Add(addressesBlockNumbersFromIndex.ToList());
        // First level of the list represents AND logical condition, second level represents OR logical condition.
        // So we should union the second levels
        // But we should iterate over first levels at the same time?
        foreach (IEnumerable<Keccak> topic in topics)
        {
            IEnumerable<long> temp = new List<long>();
            foreach (Keccak t in topic)
            {
                // List of block numbers
                foreach (long bn in Get(t.GetHashCode()).GetEnumerator(0))
                {
                    if (startBlock != null && endBlock != null)
                    {
                        if (!temp.Contains(bn) && startBlock <= bn && bn <= endBlock)
                        {
                            temp = temp.Append(bn);
                        }
                    }
                    else
                    {
                        if (!temp.Contains(bn))
                        {
                            temp = temp.Append(bn);
                        }
                    }
                }
            }

            enumerators.Add(temp.ToList());
        }

        // TODO: if they are all pointing to the same block -> yield return that block
        // advance the lowest one

        int[] indices = new int[enumerators.Count];

        // Use a loop to iterate until all enumerators are exhausted
        bool done = false;
        while (!done)
        {
            // Use a variable to store the current block number of each enumerator
            long[] blocks = new long[enumerators.Count];

            // Use a loop to get the current block number of each enumerator
            for (int i = 0; i < enumerators.Count; i++)
            {
                // Check if the enumerator has reached the end
                if (indices[i] >= enumerators[i].Count)
                {
                    // Set the block number to -1 to indicate the end
                    blocks[i] = -1;
                }
                else
                {
                    // Get the block number from the enumerator at the current index
                    blocks[i] = enumerators[i][indices[i]];
                }
            }

            // Use a variable to store the minimum block number among all enumerators
            long minBlock = long.MaxValue;

            // Use a loop to find the minimum block number among all enumerators
            for (int i = 0; i < blocks.Length; i++)
            {
                // Check if the block number is valid and smaller than the current minimum
                if (blocks[i] != -1 && blocks[i] < minBlock)
                {
                    // Update the minimum block number
                    minBlock = blocks[i];
                }
            }

            // Check if the minimum block number is valid
            if (minBlock != long.MaxValue)
            {
                // Use a variable to store whether all enumerators have the same block number
                bool sameBlock = true;

                // Use a loop to check whether all enumerators have the same block number
                for (int i = 0; i < blocks.Length; i++)
                {
                    // Check if the block number is different from the minimum block number
                    if (blocks[i] != minBlock)
                    {
                        // Set the flag to false and break the loop
                        sameBlock = false;
                        break;
                    }
                }

                // Check if all enumerators have the same block number
                if (sameBlock)
                {
                    // Add the block number to the result list
                    resultBlockNumbers = resultBlockNumbers.Append(minBlock);
                }

                // Use a loop to advance the enumerators that have the minimum block number
                for (int i = 0; i < blocks.Length; i++)
                {
                    // Check if the block number is equal to the minimum block number
                    if (blocks[i] == minBlock)
                    {
                        // Increment the index of the enumerator
                        indices[i]++;
                    }
                }
            }
            else
            {
                // Set the flag to true to indicate that all enumerators are exhausted
                done = true;
            }
        }

        return resultBlockNumbers;
    }
}
