﻿/*
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

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BlockHeaderTests
    {
        [Test]
        public void Hash_as_expected()
        {
            BlockHeader header = new BlockHeader();
            header.Bloom = new Bloom(
                Bytes.FromHexString("0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")
                    .AsSpan().ToBigEndianBitArray2048());
            header.Beneficiary = new Address("0x8888f1f195afa192cfee860698584c030f4c9db1");
            header.Difficulty = Bytes.FromHexString("0x020000").ToUInt256();
            header.ExtraData = Bytes.Empty;
            header.GasLimit = (long)Bytes.FromHexString("0x2fefba").ToUnsignedBigInteger();
            header.GasUsed = (long)Bytes.FromHexString("0x5208").ToUnsignedBigInteger();
            header.MixHash = new Keccak(Bytes.FromHexString("0x00be1f287e0911ea2f070b3650a1a0346535895b6c919d7e992a0c255a83fc8b"));
            header.Nonce = (ulong)Bytes.FromHexString("0xa0ddc06c6d7b9f48").ToUnsignedBigInteger();
            header.Number = (long)Bytes.FromHexString("0x01").ToUInt256();
            header.ParentHash = new Keccak(Bytes.FromHexString("0x5a39ed1020c04d4d84539975b893a4e7c53eab6c2965db8bc3468093a31bc5ae"));
            header.ReceiptsRoot = new Keccak(Bytes.FromHexString("0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2"));
            header.StateRoot = new Keccak(Bytes.FromHexString("0x5c2e5a51a79da58791cdfe572bcfa3dfe9c860bf7fad7d9738a1aace56ef9332"));
            header.Timestamp = Bytes.FromHexString("0x59d79f18").ToUInt256();
            header.TxRoot = new Keccak(Bytes.FromHexString("0x5c9151c2413d1cd25c51ffb4ac38948acc1359bf08c6b49f283660e9bcf0f516"));
            header.OmmersHash = new Keccak(Bytes.FromHexString("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"));

            Assert.AreEqual(new Keccak(Bytes.FromHexString("0x19a24085f6b1fb174aee0463264cc7163a7ffa165af04d3f40431ab3c3b08b98")), BlockHeader.CalculateHash(header));
        }

        [Test]
        public void Hash_as_expected_2()
        {
            BlockHeader header = new BlockHeader();
            header.Bloom = new Bloom(
                Bytes.FromHexString("0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")
                    .AsSpan().ToBigEndianBitArray2048());
            header.Beneficiary = new Address("0x8888f1f195afa192cfee860698584c030f4c9db1");
            header.Difficulty = Bytes.FromHexString("0x020080").ToUInt256();
            header.ExtraData = Bytes.Empty;
            header.GasLimit = (long)Bytes.FromHexString("0x2fefba").ToUnsignedBigInteger();
            header.GasUsed = (long)Bytes.FromHexString("0x5208").ToUnsignedBigInteger();
            header.MixHash = new Keccak(Bytes.FromHexString("0x615bbf44eb133eab3cb24d5766ae9617d9e45ee00e7a5667db30672b47d22149"));
            header.Nonce = (ulong)Bytes.FromHexString("0x4c4f3d3e055cb264").ToUnsignedBigInteger();
            header.Number = (long)Bytes.FromHexString("0x03").ToUInt256();
            header.ParentHash = new Keccak(Bytes.FromHexString("0xde1457da701ef916533750d46c124e9ae50b974410bd590fbcf4c935a4d19465"));
            header.ReceiptsRoot = new Keccak(Bytes.FromHexString("0x056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2"));
            header.StateRoot = new Keccak(Bytes.FromHexString("0xfb4084a7f8b57e370fefe24a3da3aaea6c4dd8b6f6251916c32440336035160b"));
            header.Timestamp = Bytes.FromHexString("0x59d79f1c").ToUInt256();
            header.TxRoot = new Keccak(Bytes.FromHexString("0x1722b8a91bfc4f5614ce36ee77c7cce6620ab4af36d3c54baa66d7dbeb7bce1a"));
            header.OmmersHash = new Keccak(Bytes.FromHexString("0xe676a42c388d2d24bb2927605d5d5d82fba50fb60d74d44b1cd7d1c4e4eee3c0"));
            header.Hash = BlockHeader.CalculateHash(header);

            Assert.AreEqual(new Keccak(Bytes.FromHexString("0x1423c2875714c31049cacfea8450f66a73ecbd61d7a6ab13089406a491aa9fc2")), header.Hash);
        }

        [Test]
        public void Author()
        {
            Address author = new Address("0x05a56e2d52c817161883f50c441c3228cfe54d9f");

            BlockHeader header = new BlockHeader();
            header.Beneficiary = author;

            Assert.AreEqual(author, header.GasBeneficiary);
        }
    }
}