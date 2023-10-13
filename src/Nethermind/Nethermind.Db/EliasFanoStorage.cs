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

    //
    // public void Put(int key, ulong values)
    // {
    //     if (storage.ContainsKey(key))
    //     {
    //         // TODO
    //         EliasFano ef = storage[key];
    //         EliasFanoBuilder efb = new EliasFanoBuilder(ef._universe, ef.Length + 1);
    //         // storage[key].Push(values);
    //     }
    //     else
    //     {
    //         EliasFanoBuilder efb = new EliasFanoBuilder();
    //         storage[key] = new List<long>(values);
    //     }
    // }

    public void PutAll(int key, List<ulong> values)
    {
        // TODO: handle different cases: when it has key and when not
        EliasFanoBuilder efb = new EliasFanoBuilder(values.Max(), values.Count);
        efb.Extend(values);
        EliasFano ef = efb.Build();
        storage[key] = ef;
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
}
