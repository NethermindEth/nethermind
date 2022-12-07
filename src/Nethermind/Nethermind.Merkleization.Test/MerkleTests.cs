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
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Merkleization.Test;

[TestFixture]
public class MerkleTests
{
    [TestCase]
    public void TestTransaction()
    {

        EthereumEcdsa ecdsa = new(42, LimboLogs.Instance);
        Transaction transaction = new();
        transaction.Type = TxType.Blob;

        transaction.ChainId = 42;
        transaction.Nonce = 5;
        transaction.GasLimit = 21000;
        transaction.DecodedMaxFeePerGas = 1;
        transaction.GasPrice = 2;
        transaction.MaxFeePerDataGas = 3;
        transaction.Value = 12345678;

        transaction.To = new Address("0xffb38a7a99e3e2335be83fc74b7faa19d5531243");
        transaction.BlobVersionedHashes = new byte[1][] { Bytes.FromHexString("010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c444014") };
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
        ecdsa.Sign(new PrivateKey("0x45a915e4d060149eb4365960e6a7a45f334393093061116b197e3240065ff2d8"),
            transaction, true);
        transaction.Hash = transaction.CalculateHash();

        Assert.AreEqual(transaction.Signature!.ToString(), "0xbcead551d3f695be3b4467f2dc79a1945528d267b960602a697fa9b90e2f65267b71c0d8e12760d37a1e439cbf3ef2ae207c7212cf0bbf33e1071590537db5011b");
        Assert.AreEqual(transaction.Hash!.ToString(), "0x4d392ad3209272765e86471cf02f2605f9e6221a931ae38f331e48ba3123a067");
    }
}
