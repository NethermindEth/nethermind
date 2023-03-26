// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture]
    [Parallelizable(ParallelScope.Self)]
    public class GethLikeTxTracerTests : VirtualMachineTestsBase
    {
        [Test]
        public void Can_trace_gas()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x1")
                .PushData("0x2")
                .Op(Instruction.ADD)
                .Op(Instruction.STOP)
                .Done;

            int[] gasCosts = new int[] { 3, 3, 3, 0 };

            GethLikeTxTrace trace = ExecuteAndTrace(code);

            int gasTotal = 0;
            for (int i = 0; i < gasCosts.Length; i++)
            {
                Assert.AreEqual(79000 - gasTotal, trace.Entries[i].Gas, $"gas[{i}]");
                Assert.AreEqual(gasCosts[i], trace.Entries[i].GasCost, $"gasCost[{i}]");
                gasTotal += gasCosts[i];
            }
        }

        [Test]
        [Todo("Verify the exact error string in Geth")]
        public void Can_trace_stack_underflow_failure()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.ADD)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(code);
            Assert.AreEqual(trace.Failed, true);
            Assert.AreEqual("StackUnderflow", trace.Entries[0].Error);
        }

        [Test]
        [Todo("Verify the exact error string in Geth")]
        public void Can_trace_stack_overflow_failure()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.JUMPDEST)
                .PushData("0xab")
                .PushData("0x0")
                .Op(Instruction.JUMP)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(code);
            Assert.AreEqual(trace.Failed, true);
            Assert.AreEqual("StackOverflow", trace.Entries.Last().Error);
        }

        [Test]
        [Todo("Verify the exact error string in Geth")]
        public void Can_trace_invalid_jump_failure()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0xab")
                .Op(Instruction.JUMP)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(code);
            Assert.AreEqual(trace.Failed, true);
            Assert.AreEqual("BadJumpDestination", trace.Entries.Last().Error);
        }

        [Test]
        [Todo("Verify the exact error string in Geth")]
        public void Can_trace_invalid_opcode_failure()
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.INVALID)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(code);
            Assert.AreEqual(trace.Failed, true);
            Assert.AreEqual("BadInstruction", trace.Entries.Last().Error);
        }

        [Test]
        public void Can_trace_opcodes()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0xa01234")
                .PushData("0x0")
                .Op(Instruction.STOP)
                .Done;

            string[] opCodes = new[] { "PUSH3", "PUSH1", "STOP" };

            GethLikeTxTrace trace = ExecuteAndTrace(code);
            for (int i = 0; i < opCodes.Length; i++)
            {
                Assert.AreEqual(opCodes[i], trace.Entries[i].Opcode);
            }
        }

        [Test]
        public void Can_trace_call_depth()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(code);
            int[] depths = new int[]
            {
                1, 1, 1, 1, 1, 1, 1, 1, // STACK FOR CALL
                  2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // CALL
                    3, 3, 3, 3, 3, 3, // CREATE
                  2, // STOP 
                1, // STOP
            };

            Assert.AreEqual(depths.Length, trace.Entries.Count);
            for (int i = 0; i < depths.Length; i++)
            {
                Assert.AreEqual(depths[i], trace.Entries[i].Depth, $"entries[{i}]");
            }
        }

        [Test]
        public void Stack_is_cleared_and_restored_when_moving_between_call_levels()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .PushData(SampleHexData1) // just to test if stack is restored
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1) // just to test if stack is restored
                .Call(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(code);
            /* depths 
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, // SAMPLE STACK + STACK FOR CALL [0..8]
                2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 // SAMPLE STACK + CALL [9..19]
                3, 3, 3, 3, 3, 3, // CREATE [20..25]
                2, // STOP [26]
                1, // STOP [27]
            }; */

            Assert.AreEqual(0, trace.Entries[0].Stack.Count, "BEGIN 1");
            Assert.AreEqual(8, trace.Entries[8].Stack.Count, "CALL FROM 1");
            Assert.AreEqual(0, trace.Entries[9].Stack.Count, "BEGIN 2");
            Assert.AreEqual(4, trace.Entries[19].Stack.Count, "CREATE FROM 2");
            Assert.AreEqual(0, trace.Entries[20].Stack.Count, "BEGIN 3");
            Assert.AreEqual(2, trace.Entries[2].Stack.Count, "END 3");
            Assert.AreEqual(2, trace.Entries[26].Stack.Count, "END 2");
            Assert.AreEqual(2, trace.Entries[27].Stack.Count, "END 1");
        }

        [Test]
        public void Memory_is_cleared_and_restored_when_moving_between_call_levels()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .StoreDataInMemory(32, SampleHexData1.PadLeft(64, '0')) // just to test if memory is restored
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .StoreDataInMemory(64, SampleHexData2.PadLeft(64, '0')) // just to test if memory is restored
                .Call(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(code);
            /* depths 
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 // MEMORY + STACK FOR CALL [0..10]
                2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 // MEMORY + CALL [11..23]
                3, 3, 3, 3, 3, 3, // CREATE [24..29]
                2, // STOP [30]
                1, // STOP [21]
            }; */

            Assert.AreEqual(0, trace.Entries[0].Memory.Count, "BEGIN 1");
            Assert.AreEqual(3, trace.Entries[10].Memory.Count, "CALL FROM 1");
            Assert.AreEqual(0, trace.Entries[11].Memory.Count, "BEGIN 2");
            Assert.AreEqual(2, trace.Entries[23].Memory.Count, "CREATE FROM 2");
            Assert.AreEqual(0, trace.Entries[24].Memory.Count, "BEGIN 3");
            Assert.AreEqual(1, trace.Entries[29].Memory.Count, "END 3");
            Assert.AreEqual(2, trace.Entries[30].Memory.Count, "END 2");
            Assert.AreEqual(3, trace.Entries[31].Memory.Count, "END 1");
        }

        [Test]
        public void Storage_is_cleared_and_restored_when_moving_between_call_levels()
        {
            byte[] deployedCode = new byte[3];

            byte[] initCode = Prepare.EvmCode
                .ForInitOf(deployedCode)
                .Done;

            byte[] createCode = Prepare.EvmCode
                .PersistData("0x1", HexZero) // just to test if storage is restored
                .Create(initCode, 0)
                .Op(Instruction.STOP)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .PersistData("0x2", HexZero) // just to test if storage is restored
                .PersistData("0x3", HexZero) // just to test if storage is restored
                .Call(TestItem.AddressC, 70000)
                .Op(Instruction.STOP)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(code);
            /* depths 
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 // 2x SSTORE + STACK FOR CALL [0..13]
                2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 // SSTORE + CALL [14..26]
                3, 3, 3, 3, 3, 3, // CREATE [27..32]
                2, // STOP [33]
                1, // STOP [34]
            }; */

            Assert.AreEqual(0, trace.Entries[0].Storage.Count, "BEGIN 1");
            Assert.AreEqual(2, trace.Entries[13].Storage.Count, "CALL FROM 1");
            Assert.AreEqual(0, trace.Entries[14].Storage.Count, "BEGIN 2");
            Assert.AreEqual(1, trace.Entries[26].Storage.Count, "CREATE FROM 2");
            Assert.AreEqual(0, trace.Entries[27].Storage.Count, "BEGIN 3");
            Assert.AreEqual(0, trace.Entries[32].Storage.Count, "END 3");
            Assert.AreEqual(1, trace.Entries[33].Storage.Count, "END 2");
            Assert.AreEqual(2, trace.Entries[34].Storage.Count, "END 1");
        }

        [Test]
        public void Can_trace_pc()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x0") // 0
                .Op(Instruction.JUMPDEST) // 2
                .PushData("0x1") // 3
                .Op(Instruction.ADD) // 5
                .Op(Instruction.DUP1) // 6
                .PushData("0x3") // 7
                .Op(Instruction.GT) // 9
                .PushData("0x2") // 10
                .Op(Instruction.JUMPI) // 12
                .Op(Instruction.STOP) // 13
                .Done;

            int[] pcs = new[] { 0, 2, 3, 5, 6, 7, 9, 10, 12, 2, 3, 5, 6, 7, 9, 10, 12, 2, 3, 5, 6, 7, 9, 10, 12, 13 };

            GethLikeTxTrace trace = ExecuteAndTrace(code);
            Assert.AreEqual(pcs.Length, trace.Entries.Count);
            for (int i = 0; i < pcs.Length; i++)
            {
                Assert.AreEqual(pcs[i], trace.Entries[i].ProgramCounter);
            }
        }

        [Test]
        public void Can_trace_stack()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1)
                .PushData(HexZero)
                .Op(Instruction.POP)
                .Op(Instruction.STOP)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(code);

            Assert.AreEqual(0, trace.Entries[0].Stack.Count, "entry[0] length");

            Assert.AreEqual(1, trace.Entries[1].Stack.Count, "entry[1] length");
            Assert.AreEqual($"0x{SampleHexData1}", trace.Entries[1].Stack[0], "entry[1][0]");

            Assert.AreEqual(2, trace.Entries[2].Stack.Count, "entry[2] length");
            Assert.AreEqual($"0x{SampleHexData1}", trace.Entries[2].Stack[0], "entry[2][0]");
            Assert.AreEqual("0x0", trace.Entries[2].Stack[1], "entry[2][1]");

            Assert.AreEqual(1, trace.Entries[3].Stack.Count, "entry[3] length");
            Assert.AreEqual($"0x{SampleHexData1}", trace.Entries[3].Stack[0], "entry[3][0]");
        }

        [Test]
        public void Can_trace_memory()
        {
            byte[] code = Prepare.EvmCode
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(SampleHexData2.PadLeft(64, '0'))
                .PushData(32)
                .Op(Instruction.MSTORE)
                .Op(Instruction.STOP)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(code);

            /* note the curious Geth trace behaviour where memory grows now but is populated from the next trace entry */

            Assert.AreEqual(0, trace.Entries[0].Memory.Count, "entry[0] length");

            Assert.AreEqual(0, trace.Entries[1].Memory.Count, "entry[1] length");

            Assert.AreEqual(0, trace.Entries[2].Memory.Count, "entry[2] length");

            Assert.AreEqual(1, trace.Entries[3].Memory.Count, "entry[3] length");
            Assert.AreEqual(SampleHexData1.PadLeft(64, '0'), trace.Entries[3].Memory[0], "entry[3][0]");

            Assert.AreEqual(1, trace.Entries[4].Memory.Count, "entry[4] length");
            Assert.AreEqual(SampleHexData1.PadLeft(64, '0'), trace.Entries[4].Memory[0], "entry[4][0]");

            Assert.AreEqual(1, trace.Entries[5].Memory.Count, "entry[5] length");
            Assert.AreEqual(SampleHexData1.PadLeft(64, '0'), trace.Entries[5].Memory[0], "entry[5][0]");

            Assert.AreEqual(2, trace.Entries[6].Memory.Count, "entry[2] length");
            Assert.AreEqual(SampleHexData1.PadLeft(64, '0'), trace.Entries[6].Memory[0], "entry[6][0]");
            Assert.AreEqual(SampleHexData2.PadLeft(64, '0'), trace.Entries[6].Memory[1], "entry[6][1]");
        }
    }
}
