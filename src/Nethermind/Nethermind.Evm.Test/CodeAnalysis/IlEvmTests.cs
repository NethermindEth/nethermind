// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Evm.Test.CodeAnalysis
{
    public class P01P01ADD : InstructionChunk
    {
        public static byte[] Pattern => [96, 96, 01];
        public byte CallCount { get; set; } = 0;

        public void Invoke<T>(EvmState vmState, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack) where T : struct, VirtualMachine.IIsTracing
        {
            CallCount++;
            UInt256 lhs = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 1];
            UInt256 rhs = vmState.Env.CodeInfo.MachineCode.Span[programCounter + 3];
            stack.PushUInt256(lhs + rhs);
        }
    }

    [TestFixture]
    public class IlEvmTests : VirtualMachineTestsBase
    {
        private const string AnalyzerField = "_analyzer";

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            IlAnalyzer.AddPattern(P01P01ADD.Pattern, new P01P01ADD());
        }

        [Test]
        public async Task Pattern_Analyzer_Find_All_Instance_Of_Pattern()
        {
            byte[] bytecode =
                Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .Done;

            CodeInfo codeInfo = new CodeInfo(bytecode);

            await IlAnalyzer.StartAnalysis(codeInfo, IlInfo.ILMode.PatternMatching);

            codeInfo.IlInfo.Chunks.Count.Should().Be(2);
        }

        [Test]
        public void Execution_Swap_Happens_When_Pattern_Occurs()
        {
            P01P01ADD pattern = IlAnalyzer.GetPatternHandler<P01P01ADD>(P01P01ADD.Pattern);

            byte[] bytecode =
                Prepare.EvmCode
                    .JUMPDEST()
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .JUMP(0)
                    .Done;

            /*
            byte[] initcode =
                Prepare.EvmCode
                    .StoreDataInMemory(0, bytecode)
                    .Return(bytecode.Length, 0)
                    .Done;

            byte[] code =
                Prepare.EvmCode
                    .PushData(0)
                    .PushData(0)
                    .PushData(0)
                    .PushData(0)
                    .PushData(0)
                    .Create(initcode, 1)
                    .PushData(1000)
                    .CALL()
                    .Done;
                    var address = receipts.TxReceipts[0].ContractAddress;
            */

            for(int i = 0; i < IlAnalyzer.CompoundOpThreshold * 2; i++) {
                ExecuteBlock(new NullBlockTracer(), bytecode);
            }

            Assert.Greater(pattern.CallCount, 0);   
        }

        [Test]
        public void Pure_Opcode_Emition_Coveraga()
        {
            Instruction[] instructions =
                System.Enum.GetValues<Instruction>()
                .Where(opcode => !opcode.IsStateful())
                .ToArray();


            List<(Instruction, Exception)> notYetImplemented = [];
            foreach (var instruction in instructions)
            {
                string name = $"ILEVM_TEST_{instruction}";
                OpcodeInfo opcode = new OpcodeInfo(0, instruction, null);
                try
                {
                    ILCompiler.CompileSegment(name, [opcode], []);
                } catch (NotSupportedException nse) {
                    notYetImplemented.Add((instruction, nse));
                }
                catch (Exception)
                {
                }
            }

            Assert.That(notYetImplemented.Count, Is.EqualTo(0));
        }


        public static IEnumerable<(int,byte[])> GetBytecodes()
        {
            yield return (-1, Prepare.EvmCode
                    .Done);
            yield return (0, Prepare.EvmCode
                    .PushSingle(1)
                    .PushSingle(2)
                    .PushSingle(3)
                    .PushSingle(4)
                    .Done);
            yield return (1, Prepare.EvmCode
                    .ISZERO(7)
                    .ISZERO(0)
                    .ISZERO(7)
                    .Done);
            yield return (2, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .SUB()
                    .Done);

            yield return (3, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .Done);

            yield return (4, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .MUL()
                    .Done);

            yield return (5, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .EXP()
                    .Done);

            yield return (6, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .MOD()
                    .Done);

            yield return (7, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .DIV()
                    .Done);

            yield return (8, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .Done);

            yield return (9, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .MLOAD(0)
                    .Done);

            yield return (10, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .MCOPY(32, 0, 32)
                    .Done);

            yield return (11, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .EQ()
                    .Done);

            yield return (12, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .GT()
                    .Done);

            yield return (13, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .LT()
                    .Done);

            yield return (14, Prepare.EvmCode
                    .PushSingle(1)
                    .NOT()
                    .Done);
        }

        [Test, TestCaseSource(nameof(GetBytecodes))]
        public void Ensure_Evm_ILvm_Compatibility((int index, byte[] bytecode) testcase)
        {
            ILEvmState iLEvmState = new ILEvmState
            {
                Stack = new byte[1024 * 32],
                Header = BuildBlock(MainnetSpecProvider.CancunActivation, SenderRecipientAndMiner.Default).Header,
                GasAvailable = 1000000,
                ProgramCounter = 0,
                EvmException = EvmExceptionType.None,
                StopExecution = false,
                StackHead = 0
            };
            var memory = new EvmPooledMemory();
            var metadata = IlAnalyzer.StripByteCode(testcase.bytecode);
            var ctx = ILCompiler.CompileSegment("ILEVM_TEST", metadata.Item1, metadata.Item2);
            ctx.Method(ref iLEvmState, ref memory, ctx.Data);
            Assert.IsTrue(iLEvmState.EvmException == EvmExceptionType.None);
        }


    }
}
