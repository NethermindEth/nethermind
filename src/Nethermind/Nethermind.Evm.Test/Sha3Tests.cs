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
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class Sha3Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => RinkebySpecProvider.ConstantinopleFixBlockNumber;

        private bool _setAuthor;
        
        protected override Block BuildBlock(long blockNumber, SenderRecipientAndMiner senderRecipientAndMiner, Transaction transaction)
        {
            Block block = base.BuildBlock(blockNumber, senderRecipientAndMiner, transaction);
            if(_setAuthor) block.Header.Author = TestItem.AddressC;
            block.Header.Beneficiary = TestItem.AddressB;
            return block;
        }

        [Test]
        public void Spin_sha3()
        {
            _setAuthor = true;
            
            byte[] code = Prepare.EvmCode
                .Op(Instruction.JUMPDEST)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.SHA3)
                .Op(Instruction.POP)
                .PushData(0)
                .Op(Instruction.JUMP)
                .Done;

            var receipt = Execute(8000000, 8000000, code);
            
            AssertGas(receipt, 8000000);
        }
    }
}
