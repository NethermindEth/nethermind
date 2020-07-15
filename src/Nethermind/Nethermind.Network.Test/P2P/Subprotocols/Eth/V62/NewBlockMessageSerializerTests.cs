﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NewBlockMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            NewBlockMessage message = new NewBlockMessage();
            message.TotalDifficulty = 131200;
            message.Block = Build.A.Block.Genesis.TestObject;
            NewBlockMessageSerializer serializer = new NewBlockMessageSerializer();
            
            SerializerTester.TestZero(serializer, message, "f90205f901fef901f9a00000000000000000000000000000000000000000000000000000000000000000a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424080833d090080830f424083010203a000000000000000000000000000000000000000000000000000000000000000008800000000000003e8c0c083020080");
        }

        [Test]
        public void Roundtrip2()
        {
            Transaction a = Build.A.Transaction.SignedAndResolved().TestObject;
            Transaction b = Build.A.Transaction.SignedAndResolved().TestObject;
            Transaction c = Build.A.Transaction.SignedAndResolved().TestObject;
            Transaction d = Build.A.Transaction.SignedAndResolved().TestObject;
            Transaction e = Build.A.Transaction.SignedAndResolved().TestObject;
            Transaction f = Build.A.Transaction.SignedAndResolved().TestObject;
            Block block = Build.A.Block.WithTransactions(a, b, c, d, e, f).TestObject;
            foreach (Transaction transaction in block.Transactions)
            {
                transaction.SenderAddress = null;
            }
            
            NewBlockMessage message = new NewBlockMessage();
            message.Block = block;

            NewBlockMessageSerializer serializer = new NewBlockMessageSerializer();
            SerializerTester.TestZero(serializer, message, "f9044af90446f901f9a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a0b8fe3489c99b3cfe741222974963cab3c8b74190d31e61876b12cc056ec9c816a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8f90246f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5f85f8001825208940000000000000000000000000000000000000000018025a0ac46223b1f2bb2c1a0397d2e44e0cf82b78a766b3035f6c34be06395db18a8e4a0379b8a0437094d9e1d0ae5c2511d1a4b2cb65be7974d6cbccd5370e14f2df3a5c080");
        }

        [Test]
        public void To_string()
        {
            NewBlockMessage newBlockMessage = new NewBlockMessage();
            _ = newBlockMessage.ToString();
        }
    }
}