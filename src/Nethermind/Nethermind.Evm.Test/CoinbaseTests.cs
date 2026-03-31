// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class CoinbaseTests : VirtualMachineTestsBase
    {
        private bool _setAuthor;

        protected override Block BuildBlock(ForkActivation activation, SenderRecipientAndMiner senderRecipientAndMiner, Transaction transaction, long blockGasLimit = DefaultBlockGasLimit, ulong excessBlobGas = 0, ulong slotNumber = 0)
        {
            senderRecipientAndMiner ??= new SenderRecipientAndMiner();
            Block block = base.BuildBlock(activation, senderRecipientAndMiner, transaction, blockGasLimit);
            if (_setAuthor) block.Header.Author = TestItem.AddressC;
            block.Header.Beneficiary = senderRecipientAndMiner.Recipient;
            return block;
        }

        [TestCase(true, Description = "When author set, coinbase returns author")]
        [TestCase(false, Description = "When author not set, coinbase returns beneficiary")]
        public void Coinbase_returns_author_or_beneficiary(bool setAuthor)
        {
            _setAuthor = setAuthor;

            byte[] code = Prepare.EvmCode
                .Op(Instruction.COINBASE)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            Execute(code);

            AssertStorage(0, setAuthor ? TestItem.AddressC : TestItem.AddressB);
        }
    }
}
