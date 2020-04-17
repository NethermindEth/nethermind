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

using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts
{
    [TestFixture]
    public class PersistentReceiptStorageTests
    {
        [Test]
        public void Returns_null_for_missing_tx()
        {
            MemColumnsDb<ReceiptsColumns> receiptsDb = new MemColumnsDb<ReceiptsColumns>();
            PersistentReceiptStorage persistentReceiptStorage = new PersistentReceiptStorage(receiptsDb, MainnetSpecProvider.Instance, new ReceiptsRecovery());
            Keccak blockHash = persistentReceiptStorage.FindBlockHash(Keccak.Zero);
            blockHash.Should().BeNull();
        }
    }
}