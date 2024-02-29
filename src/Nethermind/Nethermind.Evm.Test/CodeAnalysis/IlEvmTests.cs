// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Common.Utilities;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Evm.VirtualMachine;

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

            await IlAnalyzer.StartAnalysis(codeInfo);

            codeInfo.IlInfo.Chunks.Count.Should().Be(2);
        }

        [Test]
        public void Execution_Swap_Happens_When_Pattern_Occurs()
        {
            P01P01ADD pattern = (P01P01ADD)IlAnalyzer.GetPatternHandler(P01P01ADD.Pattern);

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

            for(int i = 0; i < IlAnalyzer.IlAnalyzerThreshold * 2; i++) {
                ExecuteBlock(new NullBlockTracer(), bytecode);
            }

            Assert.Greater(pattern.CallCount, 0);   
        }
    }
}
