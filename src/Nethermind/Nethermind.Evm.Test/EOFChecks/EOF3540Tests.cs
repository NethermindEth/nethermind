//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using Nethermind.Specs.Forks;
using NSubstitute;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Blockchain;
using System;
using static Nethermind.Evm.CodeAnalysis.ByteCodeValidator;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.Test;
using System.Text.Json;
using TestCase = Nethermind.Evm.Test.EofTestsBase.TestCase;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EOF3540Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);

        protected ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Shanghai.Instance));
        // valid code
        [TestCase("0xEF00010100010000", true, 1, 0, true)]
        [TestCase("0xEF0001010002006000", true, 2, 0, true)]
        [TestCase("0xEF0001010002020001006000AA", true, 2, 1, true)]
        [TestCase("0xEF0001010002020004006000AABBCCDD", true, 2, 4, true)]
        [TestCase("0xEF00010100040200020060006001AABB", true, 4, 2, true)]
        [TestCase("0xEF000101000602000400600060016002AABBCCDD", true, 6, 4, true)]
        // code with invalid magic
        [TestCase("0xEF", false, 0, 0, true, Description = "Incomplete Magic")]
        [TestCase("0xEF01", false, 0, 0, true, Description = "Incorrect Magic second byte")]
        [TestCase("0xEF0101010002020004006000AABBCCDD", false, 0, 0, true, Description = "Valid code with wrong magic second byte")]
        // code with valid magic but invalid body
        [TestCase("0xEF0000010002020004006000AABBCCDD", false, 0, 0, true, Description = "Invalid Version")]
        [TestCase("0xEF00010100", false, 0, 0, true, Description = "Code section missing")]
        [TestCase("0xEF0001010002006000DEADBEEF", false, 0, 0, true, Description = "Invalid total Size")]
        [TestCase("0xEF00010100020100020060006000", false, 0, 0, true, Description = "Multiple Code sections")]
        [TestCase("0xEF000101000002000200AABB", false, 0, 0, true, Description = "Empty code section")]
        [TestCase("0xEF000102000401000200AABBCCDD6000", false, 0, 0, true, Description = "Data section before code section")]
        [TestCase("0xEF000101000202", false, 0, 0, true, Description = "Data Section size Missing")]
        [TestCase("0xEF0001010002020004020004006000AABBCCDDAABBCCDD", false, 0, 0, true, Description = "Multiple Data sections")]
        [TestCase("0xEF0001010002030004006000AABBCCDD", false, 0, 0, true, Description = "Unknown Section")]
        // tests proposed on the eip paper
        [TestCase("0xEF", false, 0, 0, true, Description = "Incomplete magic")]
        [TestCase("0xEFFF0101000302000400600000AABBCCDD", false, 0, 0, true, Description = "Invalid magic")]
        [TestCase("0xEF00", false, 0, 0, true, Description = "No version")]
        [TestCase("0xEF000001000302000400600000AABBCCDD", false, 0, 0, true, Description = "Invalid version")]
        [TestCase("0xEF000201000302000400600000AABBCCDD", false, 0, 0, true, Description = "Invalid version")]
        [TestCase("0xEF00FF01000302000400600000AABBCCDD", false, 0, 0, true, Description = "Invalid version")]
        [TestCase("0xEF0001", false, 0, 0, true, Description = "No header")]
        [TestCase("0xEF000100", false, 0, 0, true, Description = "No code section")]
        [TestCase("0xEF000101", false, 0, 0, true, Description = "No code section size")]
        [TestCase("0xEF00010100", false, 0, 0, true, Description = "Code section size incomplete")]
        [TestCase("0xEF0001010003", false, 0, 0, true, Description = "No section terminator")]
        [TestCase("0xEF0001010003600000", false, 0, 0, true, Description = "No section terminator")]
        [TestCase("0xEF000101000200", false, 0, 0, true, Description = "No code section contents")]
        [TestCase("0xEF00010100020060", false, 0, 0, true, Description = "Code section contents incomplete")]
        [TestCase("0xEF000101000300600000DEADBEEF", false, 0, 0, true, Description = "Trailing bytes after code section")]
        [TestCase("0xEF000101000301000300600000600000", false, 0, 0, true, Description = "Multiple code sections")]
        [TestCase("0xEF000101000000", false, 0, 0, true, Description = "Empty code section")]
        [TestCase("0xEF000101000002000200AABB", false, 0, 0, true, Description = "Empty code section (with non-empty data section)")]
        [TestCase("0xEF000102000401000300AABBCCDD600000", false, 0, 0, true, Description = "Data section preceding code section")]
        [TestCase("0xEF000102000400AABBCCDD", false, 0, 0, true, Description = "Data section without code section")]
        [TestCase("0xEF000101000202", false, 0, 0, true, Description = "No data section size")]
        [TestCase("0xEF00010100020200", false, 0, 0, true, Description = "Data section size incomplete")]
        [TestCase("0xEF0001010003020004", false, 0, 0, true, Description = "No section terminator")]
        [TestCase("0xEF0001010003020004600000AABBCCDD", false, 0, 0, true, Description = "No section terminator")]
        [TestCase("0xEF000101000302000400600000", false, 0, 0, true, Description = "No data section contents")]
        [TestCase("0xEF000101000302000400600000AABBCC", false, 0, 0, true, Description = "Data section contents incomplete")]
        [TestCase("0xEF000101000302000400600000AABBCCDDEE", false, 0, 0, true, Description = "Trailing bytes after data section")]
        [TestCase("0xEF000101000302000402000400600000AABBCCDDAABBCCDD", false, 0, 0, true, Description = "Multiple data sections")]
        [TestCase("0xEF000101000101000102000102000100FEFEAABB", false, 0, 0, true, Description = "Multiple code and data sections")]
        [TestCase("0xEF000101000302000000600000", false, 0, 0, true, Description = "Empty data section")]
        [TestCase("0xEF0001010002030004006000AABBCCDD", false, 0, 0, true, Description = "Unknown section (id = 3)")]
        public void EOF_Compliant_formats_Test(string code, bool isCorrectFormated, int codeSize, int dataSize, bool isShanghaiFork)
        {
            var bytecode = Prepare.EvmCode
                .FromCode(code)
                .Done;

            var TargetReleaseSpec = new OverridableReleaseSpec(isShanghaiFork ? Shanghai.Instance : GrayGlacier.Instance);


            var expectedHeader = codeSize == 0 && dataSize == 0
                ? null
                : new EofHeader
                {
                    CodeSize = codeSize,
                    DataSize = dataSize,
                    Version = 1
                };
            var expectedJson = JsonSerializer.Serialize(expectedHeader);
            var checkResult = ByteCodeValidator.Instance.ValidateBytecode(bytecode, TargetReleaseSpec, out var header);
            var actualJson = JsonSerializer.Serialize(header);

            if (isShanghaiFork)
            {
                Assert.AreEqual(actualJson, expectedJson);
                checkResult.Should().Be(isCorrectFormated);
            }
            else
            {
                checkResult.Should().Be(isCorrectFormated);
            }
        }

        public static IEnumerable<TestCase> Eip3540TestCases
        {
            get
            {
                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(0x2a)
                            .PushData(0x2b)
                            .Op(Instruction.MSTORE8)
                            .Op(Instruction.MSIZE)
                            .PushData(0x0)
                            .Op(Instruction.SSTORE)
                            .Op(Instruction.STOP)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .Op(Instruction.STOP)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Op(Instruction.STOP)
                            .Done,
                    Data = Prepare.EvmCode
                            .Return(0, 1)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution with data section"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .Op(Instruction.PC)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution : Include PC instruction"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .Op(Instruction.JUMPDEST)
                            .Op(Instruction.JUMPDEST)
                            .Op(Instruction.JUMPDEST)
                            .Op(Instruction.JUMPDEST)
                            .Op(Instruction.PC)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution : Include PC instruction"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(4)
                            .Op(Instruction.JUMP)
                            .Op(Instruction.INVALID)
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution : Include JUMP instruction"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(4)
                            .Op(Instruction.JUMP)
                            .Op(Instruction.INVALID)
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution with data section: Include JUMP instruction"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(1)
                            .PushData(6)
                            .Op(Instruction.JUMPI)
                            .Op(Instruction.INVALID)
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution : Include JUMPI instruction"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(1)
                            .PushData(6)
                            .Op(Instruction.JUMPI)
                            .Op(Instruction.INVALID)
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution with data section: Include JUMPI instruction"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(4)
                            .Op(Instruction.JUMP)
                            .Op(Instruction.STOP)
                            .Done,
                    Data = Prepare.EvmCode
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Failure, "InvalidJumpDestination"),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution with data section: Try to jump into data section"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(1)
                            .PushData(6)
                            .Op(Instruction.JUMPI)
                            .Op(Instruction.STOP)
                            .Done,
                    Data = Prepare.EvmCode
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Failure, "InvalidJumpDestination"),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution : Try to conditinally jump into data section"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(4)
                            .Op(Instruction.JUMP)
                            .Op(Instruction.INVALID)
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data(new byte[(int)Instruction.PUSH3])
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section: Push in header (Data count is 0x62 which is PUSH3))"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .Op(Instruction.CODESIZE)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution : Include CODESIZE"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .Op(Instruction.CODESIZE)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section: Includes CODESIZE"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(19)
                            .PushData(0)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(19, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution : Includes CODECOPY, copies full code"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(26)
                            .PushData(0)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(26, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section : Includes CODECOPY, copies full code"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(10)
                            .PushData(0)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(10, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section : Includes CODECOPY, copies header"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(7)
                            .PushData(0)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(7, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution : Includes CODECOPY, copies header"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(12)
                            .PushData(7)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(12, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution : Includes CODECOPY, copies code section"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(12)
                            .PushData(10)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(12, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section: Includes CODECOPY copies code section"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(4)
                            .PushData(22)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(4, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section: Includes CODECOPY copies Data section"
                };



                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(30)
                            .PushData(0)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(23, 0)
                            .Done,
                    Data = Prepare.EvmCode
                            .Data("0xdeadbeef")
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution with Data section: copies Data out of bound (result is 0 padded)"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(23)
                            .PushData(0)
                            .PushData(0)
                            .Op(Instruction.CODECOPY)
                            .Return(23, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution : copies Data out of bound (result is 0 padded)"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(new byte[] { 23, 69 })
                            .Return(2, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution : includes PUSHx Intructions"
                };
            }
        }

        [Test]
        public void EOF_execution_tests([ValueSource(nameof(Eip3540TestCases))] TestCase testcase)
        {
            var bytecode = testcase.GenerateCode(true);

            TestAllTracerWithOutput receipts = Instance.EOF_contract_execution_tests(bytecode);

            receipts.StatusCode.Should().Be(testcase.ResultIfEOF.Status, $"{testcase.Description}");
        }

        public static IEnumerable<TestCase> Eip3540TxTestCases
        {
            get
            {
                int idx = 0;
                byte[] salt = { 4, 5, 6 };
                var standardCode = Prepare.EvmCode
                    .MUL(23, 3)
                    .Done;

                var standardData = new byte[] { 0xaa };

                byte[] corruptBytecode(bool isEof, byte[] arg)
                {
                    if (isEof)
                    {
                        // corrupt EOF : wrong version
                        arg[2] = 0;
                        return arg;
                    }
                    else
                    {
                        // corrupt Legacy : starts with 0xef
                        var result = new List<byte>();
                        result.Add(0xEF);
                        result.AddRange(arg);
                        return result.ToArray();
                    }
                }

                byte[] EmitBytecode(byte[] deployed, byte[] deployedData,
                    bool hasEofContainer, bool hasEofInitCode, bool hasEofCode,
                    bool hasCorruptContainer, bool hasCorruptInitcode, bool hasCorruptCode,
                    int context)
                {
                    // if initcode should be EOF
                    if (hasEofCode)
                    {
                        deployed = TestCase.EofBytecode(deployed, deployedData);
                    }
                    // if initcode should be Legacy
                    else
                    {
                        deployed = TestCase.Classicalcode(deployed, deployedData);
                    }

                    // if initcode should be corrupt
                    if (hasCorruptCode)
                    {
                        deployed = corruptBytecode(hasEofCode, deployed);
                    }

                    var initcode = Prepare.EvmCode
                        .StoreDataInMemory(0, deployed)
                        .RETURN(0, (UInt256)deployed.Length)
                        .Done;

                    if (hasEofInitCode)
                    {
                        initcode = TestCase.EofBytecode(initcode);
                    }
                    else
                    {
                        initcode = TestCase.Classicalcode(initcode);
                    }

                    // if initcode should be corrupt
                    if (hasCorruptInitcode)
                    {
                        initcode = corruptBytecode(hasEofInitCode, initcode);
                    }

                    // wrap initcode in container
                    byte[] result = context switch
                    {
                        1 => Prepare.EvmCode.Create(initcode, UInt256.Zero).Done,
                        2 => Prepare.EvmCode.Create2(initcode, salt, UInt256.Zero).Done,
                        _ => initcode,
                    };

                    if (context == 0)
                    {
                        return result;
                    }
                    // if container should be EOF
                    if (hasEofContainer)
                    {
                        result = TestCase.EofBytecode(result);
                    }
                    // if initcode should be Legacy
                    else
                    {
                        result = TestCase.Classicalcode(result);
                    }

                    // if container should be corrupt
                    if (hasCorruptContainer)
                    {
                        result = corruptBytecode(hasEofContainer, result);
                    }
                    return result;
                }
                for (int i = 0; i < 8; i++) // 00 01 10 11
                {
                    bool hasEofContainer = (i & 1) == 1;
                    bool hasEofInnitcode = (i & 2) == 2;
                    bool hasEofDeployCode = (i & 4) == 4;
                    for (int j = 0; j < 3; j++)
                    {
                        bool classicDep = j == 0;
                        bool useCreate1 = j == 1;
                        bool useCreate2 = j == 2;
                        for (int k = 0; k < 8; k++) // 00 01 10 11
                        {
                            bool corruptContainer = (k & 1) == 1;
                            bool corruptInnitcode = (k & 2) == 2;
                            bool corruptDeploycode = (k & 4) == 4;
                            bool isInvalid = classicDep
                                ? corruptInnitcode || (
                                    !hasEofInnitcode && !hasEofDeployCode && corruptDeploycode
                                )
                                : corruptContainer || (!hasEofContainer &&
                                    (
                                        !hasEofInnitcode && (
                                            corruptInnitcode || !hasEofDeployCode && corruptDeploycode
                                        )
                                    )
                                );
                            yield return new TestCase
                            {
                                Index = idx++,
                                Code = EmitBytecode(standardCode, standardData,
                                    hasEofContainer, hasEofInnitcode, hasEofDeployCode,
                                    corruptContainer, corruptInnitcode, corruptDeploycode,
                                    context: j),
                                ResultIfEOF = (isInvalid ? StatusCode.Failure : StatusCode.Success, null),
                                Description = $"EOF1 execution : \nDeploy {(hasEofContainer ? String.Empty : "NON-")}EOF CONTAINER with {(hasEofInnitcode ? String.Empty : "NON-")}EOF INNITCODE with {(hasEofDeployCode ? String.Empty : "NON-")}EOF CODE,\nwith Instruction {(useCreate1 ? "CREATE" : useCreate2 ? "CREATE2" : "Initcode")}, \nwith {(corruptContainer ? String.Empty : "Not")} Corrupted CONTAINER and {(corruptInnitcode ? String.Empty : "Not")} Corrupted INITCODE and {(corruptDeploycode ? String.Empty : "Not")} Corrupted CODE"
                            };
                        }
                    }
                }
            }
        }

        [Test]
        public void Eip3540_contract_deployment_tests([ValueSource(nameof(Eip3540TxTestCases))] TestCase testcase)
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Shanghai.Instance);

            Instance.EOF_contract_deployment_tests(testcase, TargetReleaseSpec);
        }

    }
}
