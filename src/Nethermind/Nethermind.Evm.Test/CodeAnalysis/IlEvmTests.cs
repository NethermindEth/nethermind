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
using static Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript.Log;

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

            for (int i = 0; i < IlAnalyzer.CompoundOpThreshold * 2; i++)
            {
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
                }
                catch (NotSupportedException nse)
                {
                    notYetImplemented.Add((instruction, nse));
                }
                catch (Exception)
                {
                }
            }

            string missingOpcodes = String.Join("; ", notYetImplemented.Select(op => op.Item1.ToString()));
            Assert.That(notYetImplemented.Count == 0, $"{notYetImplemented.Count} opcodes missing: [{missingOpcodes}]");
        }


        public static IEnumerable<(Instruction?, byte[])> GetBytecodes()
        {
            yield return (null, Prepare.EvmCode
                    .Done);
            yield return (Instruction.PUSH32, Prepare.EvmCode
                    .PushSingle(1)
                    .Done);
            yield return (Instruction.ISZERO, Prepare.EvmCode
                    .ISZERO(7)
                    .ISZERO(0)
                    .ISZERO(7)
                    .Done);
            yield return(Instruction.SUB, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .SUB()
                    .Done);

            yield return (Instruction.ADD, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .Done);

            yield return (Instruction.ADDMOD, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .PushSingle(5)
                    .ADDMOD()
                    .Done);

            yield return(Instruction.MUL, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .MUL()
                    .Done);

            yield return(Instruction.EXP, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .EXP()
                    .Done);

            yield return(Instruction.MOD, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .MOD()
                    .Done);

            yield return(Instruction.DIV, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .DIV()
                    .Done);

            yield return(Instruction.MSTORE, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .Done);

            yield return(Instruction.MLOAD, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .MLOAD(0)
                    .Done);

            yield return(Instruction.MCOPY, Prepare.EvmCode
                    .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                    .MCOPY(32, 0, 32)
                    .Done);

            yield return(Instruction.EQ, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .EQ()
                    .Done);

            yield return(Instruction.GT, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .GT()
                    .Done);

            yield return(Instruction.LT, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(7)
                    .LT()
                    .Done);

            yield return (Instruction.NOT, Prepare.EvmCode
                    .PushSingle(1)
                    .NOT()
                    .Done);

            yield return (Instruction.BLOBHASH, Prepare.EvmCode
                    .PushSingle(0)
                    .BLOBHASH()
                    .Done);

            yield return (Instruction.BLOCKHASH, Prepare.EvmCode
                    .BLOCKHASH(0)
                    .Done);

            yield return (Instruction.CALLDATACOPY, Prepare.EvmCode
                    .CALLDATACOPY(0, 0, 32)
                    .Done);

            yield return (Instruction.CALLDATALOAD, Prepare.EvmCode
                    .CALLDATALOAD(0)
                    .Done);

            yield return (Instruction.MSIZE, Prepare.EvmCode
                    .MSIZE()
                    .Done);

            yield return (Instruction.GASPRICE, Prepare.EvmCode
                    .GASPRICE()
                    .Done);

            yield return (Instruction.CODESIZE, Prepare.EvmCode
                    .CODESIZE()
                    .Done);

            yield return (Instruction.PC, Prepare.EvmCode
                    .PC()
                    .Done);

            yield return (Instruction.COINBASE, Prepare.EvmCode
                    .COINBASE()
                    .Done);

            yield return (Instruction.TIMESTAMP, Prepare.EvmCode
                    .TIMESTAMP()
                    .Done);

            yield return (Instruction.NUMBER, Prepare.EvmCode
                    .NUMBER()
                    .Done);

            yield return (Instruction.GASLIMIT, Prepare.EvmCode
                    .GASLIMIT()
                    .Done);

            yield return (Instruction.CALLER, Prepare.EvmCode
                    .CALLER()
                    .Done);

            yield return (Instruction.ADDRESS, Prepare.EvmCode
                    .ADDRESS()
                    .Done);

            yield return (Instruction.ORIGIN, Prepare.EvmCode
                    .ORIGIN()
                    .Done);

            yield return (Instruction.CALLVALUE, Prepare.EvmCode
                    .CALLVALUE()
                    .Done);

            yield return (Instruction.CHAINID, Prepare.EvmCode
                    .CHAINID()
                    .Done);

            yield return (Instruction.GAS,Prepare.EvmCode
                    .GAS()
                    .Done);

            yield return (Instruction.RETURNDATASIZE, Prepare.EvmCode
                    .RETURNDATASIZE()
                    .Done);

            yield return (Instruction.BASEFEE, Prepare.EvmCode
                    .BASEFEE()
                    .Done);

            yield return (Instruction.RETURN, Prepare.EvmCode
                    .RETURN(0, 32)
                    .Done);

            yield return (Instruction.REVERT, Prepare.EvmCode
                    .REVERT(0, 32)
                    .Done);

            yield return (Instruction.CALLDATASIZE, Prepare.EvmCode
                    .CALLDATASIZE()
                    .Done);

            yield return (Instruction.JUMPI, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .JUMPI(9)
                    .PushSingle(3)
                    .JUMPDEST()
                    .PushSingle(0)
                    .MUL()
                    .Done);

            yield return (Instruction.JUMP, Prepare.EvmCode
                    .PushSingle(23)
                    .JUMP(10)
                    .JUMPDEST()
                    .PushSingle(3)
                    .MUL()
                    .STOP()
                    .JUMPDEST()
                    .JUMP(5)
                    .Done);

            yield return (Instruction.SHL, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SHL()
                    .Done);

            yield return (Instruction.SHR, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SHR()
                    .Done);

            yield return (Instruction.SAR, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SAR()
                    .Done);

            yield return (Instruction.AND, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .AND()
                    .Done);

            yield return (Instruction.OR, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .OR()
                    .Done);

            yield return (Instruction.XOR, Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .XOR()
                    .Done);

            yield return (Instruction.SLT, Prepare.EvmCode
                    .PushData(23)
                    .PushSingle(4)
                    .SLT()
                    .Done);

            yield return (Instruction.SGT, Prepare.EvmCode
                    .PushData(23)
                    .PushData(1)
                    .SGT()
                    .Done);

            yield return (Instruction.BYTE, Prepare.EvmCode
                    .BYTE(16, UInt256.MaxValue.PaddedBytes(32))
                    .Done);

            yield return (Instruction.JUMP, Prepare.EvmCode
                .JUMP(31)
                .Done);
        }

        [Test, TestCaseSource(nameof(GetBytecodes))]
        public void Ensure_Evm_ILvm_Compatibility((Instruction? opcode, byte[] bytecode) testcase)
        {
            var blkExCtx = new BlockExecutionContext(BuildBlock(MainnetSpecProvider.CancunActivation, SenderRecipientAndMiner.Default).Header);
            var txExCtx = new TxExecutionContext(blkExCtx, TestItem.AddressA, 23, [TestItem.KeccakH.Bytes.ToArray()]);
            var envExCtx = new ExecutionEnvironment(new CodeInfo(testcase.bytecode), Recipient, Sender, Contract, new ReadOnlyMemory<byte>([1, 2, 3, 4, 5, 6, 7]), txExCtx, 23, 7);
            var stack = new byte[1024 * 32];
            var memory = new EvmPooledMemory();
            var inputBuffer = envExCtx.InputData;
            var returnBuffer = ReadOnlyMemory<byte>.Empty;
            ILEvmState iLEvmState = new ILEvmState(testcase.bytecode, ref envExCtx, ref txExCtx, ref blkExCtx, EvmExceptionType.None, 0, 100000, 0, stack, ref memory, ref inputBuffer, ref returnBuffer);
            var metadata = IlAnalyzer.StripByteCode(testcase.bytecode);
            var ctx = ILCompiler.CompileSegment("ILEVM_TEST", metadata.Item1, metadata.Item2);
            ctx.Method(ref iLEvmState, MainnetSpecProvider.Instance, _blockhashProvider, ctx.Data);
            Assert.IsTrue(iLEvmState.EvmException == EvmExceptionType.None);
        }


    }
}
