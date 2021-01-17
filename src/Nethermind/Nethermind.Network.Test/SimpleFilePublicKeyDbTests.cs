//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System.IO;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Network.Test
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
            
            SimpleFilePublicKeyDb filePublicKeyDb = new SimpleFilePublicKeyDb("Test", Path.GetTempPath(), LimboLogs.Instance);
            using (filePublicKeyDb.StartBatch())
            {
                filePublicKeyDb[TestItem.PublicKeyA.Bytes] = new byte[] {1, 2, 3};
                filePublicKeyDb[TestItem.PublicKeyB.Bytes] = new byte[] {4, 5, 6};
                filePublicKeyDb[TestItem.PublicKeyC.Bytes] = new byte[] {1, 2, 3};
            }

            SimpleFilePublicKeyDb copy = new SimpleFilePublicKeyDb("Test", Path.GetTempPath(), LimboLogs.Instance);
            Assert.AreEqual(3, copy.Keys.Count);
        }
    }
}
