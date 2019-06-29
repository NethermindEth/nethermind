/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [TestFixture]
    public class SimpleFilePublicKeyDbTests
    {
        [Test]
        public void Save_and_load()
        {
            File.Delete(Path.Combine(Path.GetTempPath(), SimpleFilePublicKeyDb.DbFileName));
            
            SimpleFilePublicKeyDb filePublicKeyDb = new SimpleFilePublicKeyDb("Test", Path.GetTempPath(), LimboLogs.Instance);
            filePublicKeyDb[TestItem.PublicKeyA.Bytes] = new byte[] {1, 2, 3};
            filePublicKeyDb[TestItem.PublicKeyB.Bytes] = new byte[] {4, 5, 6};
            filePublicKeyDb[TestItem.PublicKeyC.Bytes] = new byte[] {1, 2, 3};
            filePublicKeyDb.CommitBatch();
            
            SimpleFilePublicKeyDb copy = new SimpleFilePublicKeyDb("Test", Path.GetTempPath(), LimboLogs.Instance);
            Assert.AreEqual(3, copy.Keys.Count);
        }
    }
}