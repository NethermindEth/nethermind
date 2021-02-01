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
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [Explicit("Disk IO ")]
    public class MemoryMappedKeyValueStoreTests
    {
        private static string TestWorkDirectory => Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);

        [SetUp]
        public void SetUp()
        {
            // lazy IO makes these tests hard on shared file names. Let's sleep it through.
            Thread.Sleep(TimeSpan.FromSeconds(5));

            if (Directory.Exists(TestWorkDirectory))
            {
                Directory.Delete(TestWorkDirectory, true);
            }
        }

        [Test]
        public void Test()
        {
            const int size = 1000;
            const int batchSize = 10;
            const int minLength = 32;
            const int maxLength = 50;

            Console.WriteLine("Working directory {0}", TestContext.CurrentContext.WorkDirectory);

            using MemoryMappedKeyValueStore store = new(TestWorkDirectory, 256);
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

                Span<int> k = MemoryMarshal.Cast<byte, int>(key);
                k[0] = random.Next();
                k[^1] = i;

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
                Assert.AreEqual(expected.Length, actual.Length, "Value lengths are different for value index {0}",  j);
                Assert.True(expected.AsSpan().SequenceEqual(actual), "Value is different from the expected one for index {0}", j);
                j++;
            }
        }

        [Test]
        public void Deletes()
        {
            using MemoryMappedKeyValueStore store = new(TestWorkDirectory, 16 * 1024);
            store.Initialize();

            byte[] key = new byte[MemoryMappedKeyValueStore.KeyLength];
            key.AsSpan().Fill(13);

            byte[] value = { 47 };

            store.Set(key, value);

            SpinWait.SpinUntil(() => store.HasNoEntriesToFlush);

            store.Delete(key);

            Assert.False(store.TryGet(key, out _));

            SpinWait.SpinUntil(() => store.HasNoEntriesToFlush);

            Assert.False(store.TryGet(key, out _));
        }
    }
}
