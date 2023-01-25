// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class ZeroNewBlockMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            IByteBuffer byteBuffer = PooledByteBufferAllocator.Default.Buffer(1024);
            try
            {
                Transaction a = Build.A.Transaction.TestObject;
                Transaction b = Build.A.Transaction.TestObject;
                Block block = Build.A.Block.WithTransactions(a, b).TestObject;
                NewBlockMessage newBlockMessage = new();
                newBlockMessage.Block = block;

                NewBlockMessageSerializer serializer = new();
                serializer.Serialize(byteBuffer, newBlockMessage);
                byte[] expectedBytes = Bytes.FromHexString("f90243f9023ff901f9a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a01afbbda2cfebd56d2d0d1288617084931eb82bc346c678cac5eeff7c7a078e36a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8f840df80018252089400000000000000000000000000000000000000000180808080df80018252089400000000000000000000000000000000000000000180808080c080");
                byte[] bytes = byteBuffer.ReadAllBytesAsArray();

                Assert.AreEqual(expectedBytes.ToHexString(), bytes.ToHexString(), "bytes");
            }
            finally
            {
                byteBuffer.Release();
            }
        }

        [Test]
        public void Roundtrip2()
        {
            IByteBuffer byteBuffer = PooledByteBufferAllocator.Default.Buffer(1024);
            try
            {
                Transaction a = Build.A.Transaction.TestObject;
                Transaction b = Build.A.Transaction.TestObject;
                Block block = Build.A.Block.WithTransactions(a, b).TestObject;
                NewBlockMessage newBlockMessage = new();
                newBlockMessage.Block = block;

                NewBlockMessageSerializer serializer = new();

                serializer.Serialize(byteBuffer, newBlockMessage);
                byte[] expectedBytes = Bytes.FromHexString("f90243f9023ff901f9a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a01afbbda2cfebd56d2d0d1288617084931eb82bc346c678cac5eeff7c7a078e36a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8f840df80018252089400000000000000000000000000000000000000000180808080df80018252089400000000000000000000000000000000000000000180808080c080");
                byte[] bytes = byteBuffer.ReadAllBytesAsArray();

                Assert.AreEqual(expectedBytes.ToHexString(), bytes.ToHexString(), "bytes");
            }
            finally
            {
                byteBuffer.Release();
            }
        }
    }
}
