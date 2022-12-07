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
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Merkleization
{
    [TestFixture]
    public class MerkleTests
    {
        [TestCase]
        public void TestTransaction()
        {
            Transaction transaction = new();
            transaction.Type = TxType.Blob;

            transaction.ChainId = 1;
            transaction.Nonce = 2;
            transaction.GasLimit = 3;
            transaction.DecodedMaxFeePerGas = 4;
            transaction.GasPrice = 5;
            transaction.MaxFeePerDataGas = 6;
            transaction.Value = 7;

            transaction.To = new Address("0xffb38a7a99e3e2335be83fc74b7faa19d5531243");
            transaction.AccessList = new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>> {
                { new Address("0x1000000000000000000000000000000000000007"), new HashSet<UInt256> {
                    new UInt256(8),
                    new UInt256(8 << 25),
                } } });
            byte[] bvha = new byte[32];
            bvha[0] = 42;
            transaction.BlobVersionedHashes = new byte[][] { bvha };
            byte[] data = new byte[33];
            data[0] = 19;
            transaction.Data = data;

            Merkle.Ize(out Dirichlet.Numerics.UInt256 root, transaction);
            var dataToHash = new byte[33];
            dataToHash[0] = 5;
            root.ToLittleEndian(dataToHash.AsSpan(1));
            byte[] hash = Keccak.Compute(dataToHash).Bytes;

            Assert.AreEqual(hash, Bytes.FromHexString("0x39ef04f1d6576eb0a05fb8caba30e405a945225375e596755d0905d0a8f97a6f"));
        }
    }
}
