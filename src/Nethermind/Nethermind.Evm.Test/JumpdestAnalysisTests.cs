using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class JumpdestAnalysisTests : VirtualMachineTestsBase
    {

        [Test]
        public void CreateInLoop()
        {
            byte[] createCode = InitTest();
            TestAllTracerWithOutput res = CreateInLoop( 10_000_000, createCode);

            res.Error.Should().Contain("OutOfGas");
        }

        public TestAllTracerWithOutput CreateInLoop(int tranGasLimit, byte[] createCode)
        {
            var res = Execute(0, tranGasLimit, createCode, 50_000_000);

            return res;
        }

        public byte[] InitTest()
        {
            long initCodeSize = 32;
            
            // 32 bytes of code: JUMP + 29 of JUMPDEST
            byte[] longJumpCode = Prepare.EvmCode
                .PushData(initCodeSize - 1) // location of the last JUMPDEST
                .Op(Instruction.JUMP)
                .Op(Instruction.JUMPDEST, 29)
                .Done;
            
            var createCode = Prepare.EvmCode
                .StoreDataInMemoryWithoutTrailingZeros(0, longJumpCode) // insert the long JUMP code at the beginning of the memory
                .AddJumpDest(out int jumpDestLoc) // JUMPDEST for the infinite loop
                .Create((UInt256)initCodeSize, 0, 0) // CREATE init code from the memory
                .PushData(jumpDestLoc) // the infinite loop jump destination
                .Op(Instruction.JUMP) // the infinite loop jump
                .Done;

            return createCode;
        }
    }
}
