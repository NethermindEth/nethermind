//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [Explicit("Disk IO ")]
    public class MemoryMappedKeyValueStoreTests
    {
        private static string TestWorkDirectory => Path.Combine(TestContext.CurrentContext.WorkDirectory,
            TestContext.CurrentContext.Test.Name);

        [SetUp]
        public void SetUp()
        {
            // lazy IO makes these tests hard on shared file names. Let's sleep it through.
            Thread.Sleep(TimeSpan.FromSeconds(5));

            if (Directory.Exists(TestWorkDirectory))
            {
                Directory.Delete(TestWorkDirectory, true);
            }

            Console.WriteLine("Working directory {0}", TestWorkDirectory);
        }

        [Category("Benchmark")]
        [Test]
        public void HammeringOnePageAcrossManyFiles()
        {
            const int prefix = 1;
            
            using MemoryMappedKeyValueStore store = new(TestWorkDirectory, prefix, 4 * 1024);
            store.Initialize();

            byte[] key = new byte[MemoryMappedKeyValueStore.KeyLength];
            Span<byte> tail = key.AsSpan(prefix);
            byte[] value = new byte[1];

            const int size = 4000;

            for (int i = 0; i < size; i++)
            {   
                BinaryPrimitives.WriteInt32LittleEndian(tail, i);
                store.Put(key, value);
            }

            for (int spin = 0; spin < 1000; spin++)
            {
                for (int i = 0; i < size; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(tail, i);
                    if (!store.TryGet(key, out _))
                    {
                        Assert.Fail("The key is missing");
                    }
                }
            }
        }

        [Test]
        public void ReadAndWriteRandoms()
        {
            const int size = 1_000_000;
            const int batchSize = 100;
            const int minLength = 32;
            const int maxLength = 50;

            using MemoryMappedKeyValueStore store = new(TestWorkDirectory, 2, 2 * 1024);
            store.Initialize();

            Random random = new(size);

            List<(byte[] key, byte[] value)> pairs = new();

            MemoryMappedKeyValueStore.IWriteBatch batch = store.StartBatch();
            int batchCount = 0;

            for (int i = 0; i < size; i++)
            {
                int length = random.Next(minLength, maxLength);

                byte[] value = new byte[length];
                byte[] key = new byte[MemoryMappedKeyValueStore.KeyLength];

                value.AsSpan().Fill((byte)i);

                random.NextBytes(key);

                pairs.Add((key, value));

                batch.Put(key, value);
                if (batchCount++ == batchSize)
                {
                    batch.Commit();
                    batch.Dispose();
                    batch = store.StartBatch();
                    batchCount = 0;
                }
            }

            batch.Commit();
            batch.Dispose();

            SpinWait.SpinUntil(() => store.HasNoEntriesToFlush);

            int j = 0;
            foreach ((byte[] key, byte[] expected) in pairs)
            {
                Assert.True(store.TryGet(key, out Span<byte> actual), "Key was not found");
                Assert.AreEqual(expected.Length, actual.Length, "Value lengths are different for value index {0}", j);
                Assert.True(expected.AsSpan().SequenceEqual(actual), "Value is different from the expected one for index {0}", j);
                j++;
            }

            byte[] nonExistent = new byte[MemoryMappedKeyValueStore.KeyLength];
            random.NextBytes(nonExistent);

            Assert.IsFalse(store.TryGet(nonExistent, out _));
        }

        [Test]
        public void Deletes()
        {
            using MemoryMappedKeyValueStore store = new(TestWorkDirectory, 2, 4 * 1024);
            store.Initialize();

            byte[] key = new byte[MemoryMappedKeyValueStore.KeyLength];
            key.AsSpan().Fill(13);

            byte[] value = { 47 };

            store.Put(key, value);

            SpinWait.SpinUntil(() => store.HasNoEntriesToFlush);

            store.Delete(key);

            Assert.False(store.TryGet(key, out _));

            SpinWait.SpinUntil(() => store.HasNoEntriesToFlush);

            Assert.False(store.TryGet(key, out _));
        }
    }
}
