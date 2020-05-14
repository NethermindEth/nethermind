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

using System;
using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts
{
    [TestFixture]
    public class PersistentReceiptStorageTests
    {
        private MemColumnsDb<ReceiptsColumns> _receiptsDb;
        private PersistentReceiptStorage _storage;

        [SetUp]
        public void SetUp()
        {
            _receiptsDb = new MemColumnsDb<ReceiptsColumns>();
            _storage = new PersistentReceiptStorage(_receiptsDb, MainnetSpecProvider.Instance, new ReceiptsRecovery()) {MigratedBlockNumber = 0};
            _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, Bytes.Empty);
        }

        [Test]
        public void Returns_null_for_missing_tx()
        {
            Keccak blockHash = _storage.FindBlockHash(Keccak.Zero);
            blockHash.Should().BeNull();
        }

        [Test]
        public void ReceiptsIterator_doesnt_throw_on_empty_span()
        {
            _storage.TryGetReceiptsIterator(1, Keccak.Zero, out var iterator);
            iterator.TryGetNext(out _).Should().BeFalse();
        }
        
        [Test]
        public void ReceiptsIterator_doesnt_throw_on_null()
        {
            _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, null);
            _storage.TryGetReceiptsIterator(1, Keccak.Zero, out var iterator);
            iterator.TryGetNext(out _).Should().BeFalse();
        }
        
        [Test]
        public void Get_returns_empty_on_empty_span()
        {
            _storage.Get(Keccak.Zero).Should().BeEquivalentTo(Array.Empty<TxReceipt>());
        }
    }
}