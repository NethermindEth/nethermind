// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State.Snap.Storage;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Store.Test.SnapSync.Storage
{
    [TestFixture]
    public class SnapStorageTests
    {
        private static readonly byte[] EmptyPath = Array.Empty<byte>();

        [Test]
        public void AddAccounts_OneLayer_GetRange()
        {
            SortedList<Keccak, TrieNode> list = new();

            for (int i = 0; i < 100; i++)
            {
                byte[] accountBytes = TestItem.GenerateIndexedAccountRlp(i);
                Keccak randomKeccak = TestItem.GetRandomKeccak();
                TrieNode leaf = TrieNodeFactory.CreateLeaf(EmptyPath, accountBytes);

                list.Add(randomKeccak, leaf);
            }

            SnapStorage storage = new();
            foreach (var item in list)
            {
                storage.AddLeafNode(item.Key, item.Value, 1001);
            }

            Keccak startingHash = list.Keys[20];
            Keccak endHash = list.Keys[29];

            var range1 = storage.GetRange(startingHash, endHash, 1001);
            var range2 = storage.GetRange(startingHash, endHash, 1002);

            Assert.AreEqual(10, range1.Length);
            Assert.IsTrue(AreEqual(20, 29, list, range1));

            Assert.AreEqual(10, range2.Length);
            Assert.IsTrue(AreEqual(20, 29, list, range2));
        }

        [Test]
        public void AddAccounts_MultipleLayers_GetRange()
        {
            SortedList<Keccak, TrieNode> list = new();

            for (int i = 0; i < 100; i++)
            {
                byte[] accountBytes = TestItem.GenerateIndexedAccountRlp(i);
                Keccak randomKeccak = TestItem.GetRandomKeccak();
                TrieNode leaf = TrieNodeFactory.CreateLeaf(EmptyPath, accountBytes);

                list.Add(randomKeccak, leaf);
            }

            SnapStorage storage = new();

            // create bottom layer
            foreach (var item in list)
            {
                storage.AddLeafNode(item.Key, item.Value, 1001);
            }

            // block 1002
            byte[] updateAccountBytes_1002 = TestItem.GenerateRandomAccountRlp();
            TrieNode updateAccount_1002 = TrieNodeFactory.CreateLeaf(EmptyPath, updateAccountBytes_1002);
            storage.AddLeafNode(list.Keys[21], updateAccount_1002, 1002);

            byte[] newAccountBytes_1002 = TestItem.GenerateRandomAccountRlp();
            TrieNode newAccount_1002 = TrieNodeFactory.CreateLeaf(EmptyPath, newAccountBytes_1002);
            Keccak newAddress = TestItem.GetRandomKeccak();
            while (newAddress <= list.Keys[21] || newAddress >= list.Keys[22])
            {
                newAddress = TestItem.GetRandomKeccak();
            }
            storage.AddLeafNode(newAddress, newAccount_1002, 1002);

            // block 1003
            storage.AddLeafNode(list.Keys[21], null, 1003);


            Keccak startingHash = list.Keys[20];
            Keccak endHash = list.Keys[30];

            var range_1001 = storage.GetRange(startingHash, endHash, 1001);
            var range_1002 = storage.GetRange(startingHash, endHash, 1002);
            var range_1003 = storage.GetRange(startingHash, endHash, 1003);

            Assert.AreEqual(11, range_1001.Length);
            Assert.IsTrue(AreEqual(20, 30, list, range_1001));

            Assert.AreEqual(12, range_1002.Length);
            Assert.AreEqual(range_1002[1].node, updateAccount_1002);
            Assert.AreEqual(range_1002[2].node, newAccount_1002);

            Assert.AreEqual(11, range_1003.Length);
            Assert.AreEqual(range_1003[1].node, newAccount_1002);
        }

        private bool AreEqual(int startIndex, int endIndex, SortedList<Keccak, TrieNode> inputList, (Keccak path, TrieNode node)[] range)
        {
            for (int i = startIndex; i <= endIndex; i++)
            {
                if (inputList.Keys[i] != range[i - startIndex].path)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
