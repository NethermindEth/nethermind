// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Db;

public class KeyValueStorage
{
    // Keys: Hashcode Address or Topic byte[]
    // Values: Elias-Fano encoded block numbers in monotically increasing order.
    private Dictionary<int, List<long>> storage;

    public KeyValueStorage()
    {
        storage = new Dictionary<int, List<long>>();
    }

    public List<long> Get(int key)
    {
        if (storage.ContainsKey(key))
        {
            return storage[key];
        }
        else
        {
            return new List<long>();
        }
    }

    public void Put(int key, List<long> values)
    {
        if (storage.ContainsKey(key))
        {
            storage[key].AddRange(values);
        }
        else
        {
            storage[key] = new List<long>(values);
        }
    }

    public List<long> FindBlockNumbers(int address, int topic)
    {
        // Get the values for the given keys
        var addressValues = Get(address);
        var topicValues = Get(topic.GetHashCode());

        // Convert the IEnumerable<long> to List<long> for easier manipulation
        var addressList = addressValues.ToList();
        var topicList = topicValues.ToList();

        // Initialize an empty list to store the matching values
        var matchingValues = new List<long>();

        // Initialize indices for iterating over the two lists
        int i = 0, j = 0;

        // Iterate over both lists
        while (i < addressList.Count && j < topicList.Count)
        {
            if (addressList[i] == topicList[j])
            {
                // If there is a match, add it to the result list and move both indices forward
                matchingValues.Add(addressList[i]);
                i++;
                j++;
            }
            else if (addressList[i] < topicList[j])
            {
                // If the current value in addressList is smaller, move its index forward
                i++;
            }
            else
            {
                // If the current value in topicList is smaller, move its index forward
                j++;
            }
        }

        return matchingValues;
    }
}


// TestCase snippets for refereence:
// Make a simple test case:
// 1. (maybe not needed). Create a Block with address, number, and topic
// 1b. Create a receipt and a log for it and get the address, block number, and topic
// 2. Store the key values in the storage.
// 3. Use the storage to see if it can find the block number.
// private void test(Address address, Keccak topic)
// {
//     // Add a address key and value
//     // Add a topic key and value
//     // Make a FilterLog instantiation
//     // Use Logfinder to find the block numbers that match.
//     FilterBuilder filterBuilder = AllBlockFilter();
//     filterBuilder = filterBuilder.WithAddress(address);
//     var logFilter = filterBuilder.Build();
//     // var logFilter = AllBlockFilter().WithTopicExpressions(topics).Build();

// }

// public async Task Eth_get_logs(string parameter, string expected)
// {
//     using Context ctx = await Context.Create();
//     IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
//     bridge.GetLogs(Arg.Any<BlockParameter>(), Arg.Any<BlockParameter>(), Arg.Any<object>(), Arg.Any<IEnumerable<object>>(), Arg.Any<CancellationToken>())
//         .Returns(new[] { new FilterLog(1, 0, 1, TestItem.KeccakA, 1, TestItem.KeccakB, TestItem.AddressA, new byte[] { 1, 2, 3 }, new[] { TestItem.KeccakC, TestItem.KeccakD }) });
//     bridge.FilterExists(1).Returns(true);
//
//     ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
//     string serialized = await ctx.Test.TestEthRpc("eth_getLogs", parameter);
//
//     Assert.That(serialized, Is.EqualTo(expected));
// }

// [Test, Timeout(Timeout.MaxTestTime)]
// public void filter_all_logs([ValueSource(nameof(WithBloomValues))] bool withBloomDb, [Values(false, true)] bool allowReceiptIterator)
// {
//     SetUp(allowReceiptIterator);
//     StoreTreeBlooms(withBloomDb);
//     var logFilter = AllBlockFilter().Build();
//     var logs = _logFinder.FindLogs(logFilter).ToArray();
//     logs.Length.Should().Be(5);
//     var indexes = logs.Select(l => (int)l.LogIndex).ToArray();
//     // indexes[0].Should().Be(0);
//     // indexes[1].Should().Be(1);
//     // indexes[2].Should().Be(0);
//     // indexes[3].Should().Be(1);
//     // indexes[4].Should().Be(2);
//     indexes.Should().BeEquivalentTo(new[] { 0, 1, 0, 1, 2 });
// }
//
// [Test, Timeout(Timeout.MaxTestTime)]
// public void filter_all_logs_iteratively([ValueSource(nameof(WithBloomValues))] bool withBloomDb, [Values(false, true)] bool allowReceiptIterator)
// {
//     SetUp(allowReceiptIterator);
//     LogFilter logFilter = AllBlockFilter().Build();
//     FilterLog[] logs = _logFinder.FindLogs(logFilter).ToArray();
//     logs.Length.Should().Be(5);
//     var indexes = logs.Select(l => (int)l.LogIndex).ToArray();
//     // indexes[0].Should().Be(0);
//     // indexes[1].Should().Be(1);
//     // indexes[2].Should().Be(0);
//     // indexes[3].Should().Be(1);
//     // indexes[4].Should().Be(2);
//     // BeEquivalentTo does not check the ordering!!! :O
//     indexes.Should().BeEquivalentTo(new[] { 0, 1, 0, 1, 2 });
// }

// [TestCaseSource(nameof(FilterByAddressTestsData))]
// public void filter_by_address(Address[] addresses, int expectedCount, bool withBloomDb)
// {
//     StoreTreeBlooms(withBloomDb);
//     var filterBuilder = AllBlockFilter();
//     filterBuilder = addresses.Length == 1 ? filterBuilder.WithAddress(addresses[0]) : filterBuilder.WithAddresses(addresses);
//     var logFilter = filterBuilder.Build();
//
//     var logs = _logFinder.FindLogs(logFilter).ToArray();
//
//     logs.Length.Should().Be(expectedCount);
// }

// [TestCaseSource(nameof(FilterByTopicsTestsData))]
// public void filter_by_topics_and_return_logs_in_order(TopicExpression[] topics, bool withBloomDb,
//     long[] expectedBlockNumbers)
// {
//     StoreTreeBlooms(withBloomDb);
//     var logFilter = AllBlockFilter().WithTopicExpressions(topics).Build();
//
//     var logs = _logFinder.FindLogs(logFilter).ToArray();
//
//     var blockNumbers = logs.Select((log) => log.BlockNumber).ToArray();
//     Assert.That(expectedBlockNumbers, Is.EqualTo(blockNumbers));
// }
