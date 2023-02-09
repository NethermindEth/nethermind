// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
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
            using (filePublicKeyDb.StartBatch())
            {
                filePublicKeyDb[TestItem.PublicKeyA.Bytes] = new byte[] { 1, 2, 3 };
                filePublicKeyDb[TestItem.PublicKeyB.Bytes] = new byte[] { 4, 5, 6 };
                filePublicKeyDb[TestItem.PublicKeyC.Bytes] = new byte[] { 1, 2, 3 };
            }

            SimpleFilePublicKeyDb copy = new("Test", Path.GetTempPath(), LimboLogs.Instance);
            Assert.AreEqual(3, copy.Keys.Count);
        }
    }
}
