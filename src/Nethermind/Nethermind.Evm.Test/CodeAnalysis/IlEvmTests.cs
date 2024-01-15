// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis
{
    public class P01P01ADD : InstructionChunk
    {
        public void Invoke<T>(EvmState vmState, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack) where T : struct, VirtualMachine.IIsTracing
        {
            UInt256 lhs = vmState.Env.CodeInfo.MachineCode[programCounter + 1];
            UInt256 rhs = vmState.Env.CodeInfo.MachineCode[programCounter + 3];
            stack.PushUInt256(lhs + rhs);
        }
    }

    [TestFixture]
    public class IlEvmTests
    {
        private const string AnalyzerField = "_analyzer";

        [SetUp]
        public void Setup()
        {
            Dictionary<byte[], InstructionChunk> patterns = new Dictionary<byte[], InstructionChunk>
            {
                { [96, 96, 01], new P01P01ADD() }
            };

            IlAnalyzer.Patterns = patterns.ToFrozenDictionary();
        }

        [Test]
        public async Task Il_Analyzer_Find_All_Instance_Of_Pattern()
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
    }
}
