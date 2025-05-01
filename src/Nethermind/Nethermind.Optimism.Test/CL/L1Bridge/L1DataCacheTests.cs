// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.Optimism.CL.L1Bridge;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL.L1Bridge
{
    public class L1DataCacheTests
    {
        [Test]
        public void Constructor_ThrowsException_WhenCapacityIsZeroOrLess()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new L1DataCache(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new L1DataCache(-1));
        }

        [Test]
        public void GetBlockByNumber_ReturnsNull_WhenBlockNotInCache()
        {
            var cache = new L1DataCache(10);
            var result = cache.GetBlockByNumber(1);
            Assert.That(result, Is.Null);
        }

        [Test]
        public void GetBlockByHash_ReturnsNull_WhenBlockNotInCache()
        {
            var cache = new L1DataCache(10);
            var hash = new Hash256("0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef");

            Assert.That(cache.GetBlockByHash(hash), Is.Null);
        }

        [Test]
        public void GetReceipts_ReturnsNull_WhenBlockNotInCache()
        {
            var cache = new L1DataCache(10);
            var hash = new Hash256("0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef");

            Assert.That(cache.GetReceipts(hash), Is.Null);
        }

        [Test]
        public void CacheData_StoresBlockAndReceipts_WhenBlockNotInCache()
        {
            var cache = new L1DataCache(10);
            var blockHash = new Hash256("0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef");
            var block = new L1Block { Number = 1, Hash = blockHash };
            var receipts = new[] { new ReceiptForRpc() };

            cache.CacheData(block, receipts);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.GetBlockByNumber(1), Is.EqualTo(block));
                Assert.That(cache.GetBlockByHash(blockHash), Is.EqualTo(block));
                Assert.That(cache.GetReceipts(blockHash), Is.EqualTo(receipts));
            }
        }

        [Test]
        public void CacheData_SkipsStoringDuplicateBlock()
        {
            var cache = new L1DataCache(10);
            var blockHash = new Hash256("0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef");
            var block = new L1Block { Number = 1, Hash = blockHash };
            var receipts1 = new[] {
                new ReceiptForRpc {
                    TransactionHash = new Hash256("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
                }
            };
            var receipts2 = new[] {
                new ReceiptForRpc {
                    TransactionHash = new Hash256("0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                }
            };

            cache.CacheData(block, receipts1);
            cache.CacheData(block, receipts2); // Should be ignored

            var storedReceipts = cache.GetReceipts(blockHash);
            Assert.That(storedReceipts, Is.EqualTo(receipts1));
        }

        [Test]
        public void CacheData_EvictsOldestData_WhenCapacityReached()
        {
            int capacity = 3;
            var cache = new L1DataCache(capacity);
            var blockHashes = new Hash256[capacity + 1];
            var blocks = new L1Block[capacity + 1];
            var receipts = new ReceiptForRpc[capacity + 1];

            for (ulong i = 0; i <= (ulong)capacity; i++)
            {
                blockHashes[i] = new Hash256($"0x{i}234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef");
                blocks[i] = new L1Block { Number = i, Hash = blockHashes[i] };
                receipts[i] = new ReceiptForRpc
                {
                    TransactionHash = new Hash256($"0x{i}aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
                };
                cache.CacheData(blocks[i], [receipts[i]]);
            }

            // First block should have been evicted
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.GetBlockByHash(blockHashes[0]), Is.Null);
                Assert.That(cache.GetBlockByNumber(0), Is.Null);
            }
            // But all others should still be present
            for (ulong i = 1; i <= (ulong)capacity; i++)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(cache.GetBlockByNumber(i), Is.EqualTo(blocks[i]));
                    Assert.That(cache.GetReceipts(blockHashes[i]), Is.EqualTo([receipts[i]]));
                }
            }
        }
    }
}
