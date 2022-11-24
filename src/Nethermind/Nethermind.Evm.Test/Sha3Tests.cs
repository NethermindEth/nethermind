// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        protected override Block BuildBlock(long blockNumber, SenderRecipientAndMiner senderRecipientAndMiner, Transaction transaction, long blockGasLimit = DefaultBlockGasLimit, ulong timestamp = 0)
        {
            Block block = base.BuildBlock(blockNumber, senderRecipientAndMiner, transaction, blockGasLimit, timestamp);
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
                .Op(Instruction.SHA3)
                .Op(Instruction.POP)
                .PushData(0)
                .Op(Instruction.JUMP)
                .Done;

            TestAllTracerWithOutput receipt = Execute(8000000, 8000000, code);

            AssertGas(receipt, 8000000);
        }
    }
}
