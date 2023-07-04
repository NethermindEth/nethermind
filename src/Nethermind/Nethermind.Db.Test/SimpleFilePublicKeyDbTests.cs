// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SimpleFilePublicKeyDbTests
    {
        [Test]
        public void Save_and_load()
        {
            using TempPath tempPath = TempPath.GetTempFile(SimpleFilePublicKeyDb.DbFileName);
            tempPath.Dispose();

            SimpleFilePublicKeyDb filePublicKeyDb = new("Test", Path.GetTempPath(), LimboLogs.Instance);

            Random random = new Random();
            Dictionary<byte[], byte[]> dict = new Dictionary<byte[], byte[]>(Bytes.EqualityComparer);
            for (int i = 0; i < 1024; i++)
            {
                byte[] key = new byte[random.Next(64, 128)];
                byte[] value = new byte[random.Next(200, 250)];

                random.NextBytes(key);
                random.NextBytes(value);

                dict[key] = value;
            }

            using (filePublicKeyDb.StartBatch())
            {
                foreach (var kv in dict)
                {
                    filePublicKeyDb[kv.Key] = kv.Value;
                }
            }

            SimpleFilePublicKeyDb copy = new("Test", Path.GetTempPath(), LimboLogs.Instance);
            Assert.That(copy.Keys.Count, Is.EqualTo(dict.Count));
            foreach (var kv in dict)
            {
                Assert.True(filePublicKeyDb[kv.Key].AsSpan().SequenceEqual(kv.Value));
            }
        }
    }
}
