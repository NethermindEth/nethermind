// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        protected override Block BuildBlock(long blockNumber, SenderRecipientAndMiner senderRecipientAndMiner, Transaction transaction, long blockGasLimit = DefaultBlockGasLimit, ulong timestamp = 0)
        {
            senderRecipientAndMiner ??= new SenderRecipientAndMiner();
            Block block = base.BuildBlock(blockNumber, senderRecipientAndMiner, transaction, blockGasLimit, timestamp);
            if (_setAuthor) block.Header.Author = TestItem.AddressC;
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
