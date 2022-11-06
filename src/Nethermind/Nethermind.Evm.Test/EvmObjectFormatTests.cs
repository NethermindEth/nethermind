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

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EvmObjectFormatTests : VirtualMachineTestsBase
    {
        protected override ulong Timestamp => MainnetSpecProvider.ShanghaiBlockTimestamp;
        protected override long BlockNumber => MainnetSpecProvider.ShanghaiActivation.BlockNumber;
        static byte[] Classicalcode(byte[] bytecode, byte[] data = null)
        {
            var bytes = new byte[(data is not null && data.Length > 0 ? data.Length : 0) + bytecode.Length];

            Array.Copy(bytecode, 0, bytes, 0, bytecode.Length);
            if (data is not null && data.Length > 0)
            {
                Array.Copy(data, 0, bytes, bytecode.Length, data.Length);
            }

            return bytes;
        }
        static byte[] EofBytecode(byte[] bytecode, byte[] data = null)
        {
            var bytes = new byte[(data is not null && data.Length > 0 ? 10 + data.Length : 7) + bytecode.Length];

            int i = 0;

            // set magic
            bytes[i++] = 0xEF; bytes[i++] = 0x00; bytes[i++] = 0x01;

            // set code section
            var lenBytes = bytecode.Length.ToByteArray();
            bytes[i++] = 0x01; bytes[i++] = lenBytes[^2]; bytes[i++] = lenBytes[^1];

            // set PushData section
            if (data is not null && data.Length > 0)
            {
                lenBytes = data.Length.ToByteArray();
                bytes[i++] = 0x02; bytes[i++] = lenBytes[^2]; bytes[i++] = lenBytes[^1];
            }
            bytes[i++] = 0x00;

            // set the terminator byte
            Array.Copy(bytecode, 0, bytes, i, bytecode.Length);
            if (data is not null && data.Length > 0)
            {
                Array.Copy(data, 0, bytes, i + bytecode.Length, data.Length);
            }

            return bytes;
        }

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

            ReleaseSpec spec = (ReleaseSpec)(isShanghaiFork ? Shanghai.Instance : GrayGlacier.Instance);
            spec.IsEip3670Enabled = false;

            var expectedHeader = codeSize == 0 && dataSize == 0
                ? null
                : new EofHeader
                {
                    CodeSize = (ushort)codeSize,
                    DataSize = (ushort)dataSize
                };
            var checkResult = ValidateByteCode(bytecode, spec, out var header);

            if (isShanghaiFork)
            {
                header.Should().Be(expectedHeader);
                checkResult.Should().Be(isCorrectFormated);
            }
            else
            {
                checkResult.Should().Be(isCorrectFormated);
            }
        }

        public class TestCase
        {
            public int Index;
            public byte[] Code;
            public byte[] Data;
            public (byte Status, string error) ResultIfEOF;
            public (byte Status, string error) ResultIfNotEOF;
            public string Description;
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

        public static IEnumerable<IReleaseSpec> Specs
        {
            get
            {
                yield return GrayGlacier.Instance;
                yield return Shanghai.Instance;
            }
        }

        [Test]
        public void EOF_execution_tests([ValueSource(nameof(Eip3540TestCases))] TestCase testcase, [ValueSource(nameof(Specs))] IReleaseSpec spec)
        {
            bool isShanghaiBlock = spec is Shanghai;
            long blockTestNumber = isShanghaiBlock ? BlockNumber : BlockNumber - 1;

            var bytecode =
                isShanghaiBlock
                ? EofBytecode(testcase.Code, testcase.Data)
                : Classicalcode(testcase.Code, testcase.Data);

            TestAllTracerWithOutput receipts = Execute(blockTestNumber, Int64.MaxValue, bytecode, Int64.MaxValue);

            if (isShanghaiBlock)
            {
                receipts.StatusCode.Should().Be(testcase.ResultIfEOF.Status, testcase.Description);
                receipts.Error.Should().Be(testcase.ResultIfEOF.error, testcase.Description);
            }

            if (!isShanghaiBlock)
            {
                receipts.StatusCode.Should().Be(testcase.ResultIfNotEOF.Status, testcase.Description);
                receipts.Error.Should().Be(testcase.ResultIfNotEOF.error, testcase.Description);
            }
        }

        public static IEnumerable<TestCase> Eip3540TxTestCases
        {
            get
            {
                int idx = 0;
                byte[] salt = { 4, 5, 6 };
                var standardCode = Prepare.EvmCode.ADD(2, 3).Done;
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

                byte[] EmitBytecode(byte[] deployed, byte[] deployedData, bool hasEofContainer, bool hasEofInitCode, bool hasCorruptContainer, bool hasCorruptInitcode, int context)
                {
                    // if initcode should be EOF
                    if (hasEofInitCode)
                    {
                        deployed = EofBytecode(deployed, deployedData);
                    }
                    // if initcode should be Legacy
                    else
                    {
                        deployed = Classicalcode(deployed, deployedData);
                    }

                    // if initcode should be corrupt
                    if (hasCorruptInitcode)
                    {
                        deployed = corruptBytecode(hasEofInitCode, deployed);
                    }

                    // wrap initcode in container
                    byte[] result = context switch
                    {
                        1 => Prepare.EvmCode
                                .MSTORE(0, deployed)
                                .CREATEx(1, UInt256.Zero, (UInt256)(32 - deployed.Length), (UInt256)deployed.Length)
                                .Done,
                        2 => Prepare.EvmCode
                                .MSTORE(0, deployed)
                                .PUSHx(salt)
                                .CREATEx(2, UInt256.Zero, (UInt256)(32 - deployed.Length), (UInt256)deployed.Length)
                                .Done,
                        _ => Prepare.EvmCode
                                .MSTORE(0, deployed)
                                .RETURN((UInt256)(32 - deployed.Length), (UInt256)deployed.Length)
                                .Done,
                    };

                    // if container should be EOF
                    if (hasEofContainer)
                    {
                        result = EofBytecode(result);
                    }
                    // if initcode should be Legacy
                    else
                    {
                        result = Classicalcode(result);
                    }

                    // if container should be corrupt
                    if (hasCorruptContainer)
                    {
                        result = corruptBytecode(hasEofContainer, result);
                    }

                    return result;

                }
                for (int i = 0; i < 4; i++) // 00 01 10 11
                {
                    bool hasEofContainer = (i & 1) == 1;
                    bool hasEofInnitcode = (i & 2) == 2;
                    for (int j = 0; j < 3; j++)
                    {
                        bool useCreate1 = j == 1;
                        bool useCreate2 = j == 2;
                        for (int k = 0; k < 4; k++) // 00 01 10 11
                        {
                            bool corruptContainer = (k & 1) == 1;
                            bool corruptInnitcode = (k & 2) == 2;
                            yield return new TestCase
                            {
                                Index = idx++,
                                Code = EmitBytecode(standardCode, standardData, hasEofContainer, hasEofInnitcode, corruptContainer, corruptInnitcode, k),
                                ResultIfEOF = (corruptContainer ? StatusCode.Failure : StatusCode.Success, null),
                                Description = $"EOF1 execution : \nDeploy {(hasEofInnitcode ? String.Empty : "NON-")}EOF Bytecode with {(hasEofContainer ? String.Empty : "NON-")}EOF container,\nwith Instruction {(useCreate1 ? "CREATE" : useCreate2 ? "CREATE2" : "Initcode")}, \nwith {(corruptContainer ? String.Empty : "Not")} Corrupted CONTAINER and {(corruptInnitcode ? String.Empty : "Not")} Corrupted INITCODE"
                            };
                        }
                    }
                }
            }
        }



        [Test]
        public void EOF_contract_deployment_tests([ValueSource(nameof(Eip3540TxTestCases))] TestCase testcase)
        {
            TestState.CreateAccount(TestItem.AddressC, 200.Ether());
            byte[] createContract = testcase.Code;

            _processor = new TransactionProcessor(SpecProvider, TestState, Storage, Machine, LimboLogs.Instance);
            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, createContract);

            transaction.GasPrice = 100.GWei();
            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, block.Header, tracer);

            Assert.AreEqual(testcase.ResultIfEOF.Status, tracer.StatusCode, $"{testcase.Description}\nFailed with error {tracer.Error} \ncode : {testcase.Code.ToHexString(true)}");
        }

        // valid code
        [TestCase("0xEF000101000100FE", true, true)]
        [TestCase("0xEF00010100050060006000F3", true, true)]
        [TestCase("0xEF00010100050060006000FD", true, true)]
        [TestCase("0xEF0001010003006000FF", true, true)]
        [TestCase("0xEF0001010022007F000000000000000000000000000000000000000000000000000000000000000000", true, true)]
        [TestCase("0xEF0001010022007F0C0D0E0F1E1F2122232425262728292A2B2C2D2E2F494A4B4C4D4E4F5C5D5E5F00", true, true)]
        [TestCase("0xEF000101000102002000000C0D0E0F1E1F2122232425262728292A2B2C2D2E2F494A4B4C4D4E4F5C5D5E5F", true, true)]
        // code with invalid magic
        [TestCase("0xEF0001010001000C", false, true, Description = "Undefined instruction")]
        [TestCase("0xEF000101000100EF", false, true, Description = "Undefined instruction")]
        [TestCase("0xEF00010100010060", false, true, Description = "Missing terminating instruction")]
        [TestCase("0xEF00010100010030", false, true, Description = "Missing terminating instruction")]
        [TestCase("0xEF0001010020007F00000000000000000000000000000000000000000000000000000000000000", false, true, Description = "Missing terminating instruction")]
        [TestCase("EF0001010021007F0000000000000000000000000000000000000000000000000000000000000000", false, true, Description = "Missing terminating instruction")]
        public void EIP3670_Compliant_formats_Test(string code, bool isCorrectlyFormated, bool isShanghaiFork)
        {
            var bytecode = Prepare.EvmCode
                .FromCode(code)
                .Done;

            IReleaseSpec spec = isShanghaiFork ? Shanghai.Instance : GrayGlacier.Instance;

            bool checkResult = ValidateByteCode(bytecode, spec, out _);

            checkResult.Should().Be(isCorrectlyFormated);
        }
    }
}
