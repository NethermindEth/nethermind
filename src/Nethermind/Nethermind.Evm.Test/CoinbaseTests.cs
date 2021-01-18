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
    public class CoinbaseTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => RinkebySpecProvider.SpuriousDragonBlockNumber;

        private bool _setAuthor;
        
        protected override Block BuildBlock(long blockNumber, SenderRecipientAndMiner senderRecipientAndMiner, Transaction transaction)
        {
            senderRecipientAndMiner ??= new SenderRecipientAndMiner();
            Block block = base.BuildBlock(blockNumber, senderRecipientAndMiner, transaction);
            if(_setAuthor) block.Header.Author = TestItem.AddressC;
            block.Header.Beneficiary = senderRecipientAndMiner.Recipient;
            return block;
        }

        [Test]
        public void When_author_set_coinbase_return_author()
        {
            _setAuthor = true;
            
            byte[] code = Prepare.EvmCode
                .Op(Instruction.COINBASE)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);
            
            AssertStorage(0, TestItem.AddressC);
        }
        
        [Test]
        public void When_author_no_set_coinbase_return_beneficiary()
        {
            _setAuthor = false;
            
            byte[] code = Prepare.EvmCode
                .Op(Instruction.COINBASE)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);
            
            AssertStorage(0, TestItem.AddressB);
        }
    }
}
