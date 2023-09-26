// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class Keccak256Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => RinkebySpecProvider.ConstantinopleFixBlockNumber;

        private bool _setAuthor;

        protected override Block BuildBlock(ForkActivation activation, SenderRecipientAndMiner senderRecipientAndMiner, Transaction transaction, long blockGasLimit = DefaultBlockGasLimit, ulong excessBlobGas = 0)
        {
            Block block = base.BuildBlock(activation, senderRecipientAndMiner, transaction, blockGasLimit, excessBlobGas);
            if (_setAuthor) block.Header.Author = TestItem.AddressC;
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
                .Op(Instruction.KECCAK256)
                .Op(Instruction.POP)
                .PushData(0)
                .Op(Instruction.JUMP)
                .Done;

            TestAllTracerWithOutput receipt = Execute((8000000, 0), 8000000, code);

            AssertGas(receipt, 8000000);
        }
    }
}
