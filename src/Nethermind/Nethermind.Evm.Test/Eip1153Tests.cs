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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    internal class Eip1153Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ShanghaiBlockNumber;
        protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

        [Test]
        public void after_shanghai_can_call_tstore_tload()
        {
            byte[] code = Prepare.EvmCode
                .PushData(96) // Value
                .PushData(64) // Index
                .Op(Instruction.TSTORE)
                .PushData(64) // Index
                .Op(Instruction.TLOAD)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
        }

        [Test]
        public void before_shanghai_can_not_call_tstore_tload()
        {
            byte[] code = Prepare.EvmCode
                .PushData(96) // Value
                .PushData(64) // Index
                .Op(Instruction.TSTORE)
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber - 1, 100000, code);
            Assert.AreEqual(StatusCode.Failure, result.StatusCode);
        }

        [Test]
        public void tload_after_tstore()
        {
            byte[] code = Prepare.EvmCode
                .PushData(96) // Value
                .PushData(64) // Index
                .Op(Instruction.TSTORE)
                .PushData(64) // Index
                .Op(Instruction.TLOAD)
                .PushData(0)
                .Op(Instruction.MSTORE) // Store the result in mem
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN) // Return the result
                .Done;

            TestAllTracerWithOutput result = Execute(MainnetSpecProvider.ShanghaiBlockNumber, 100000, code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);

            Assert.AreEqual(96, (int)result.ReturnValue.ToUInt256());            
        }
    }
}
