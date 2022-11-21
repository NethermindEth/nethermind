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
using Castle.DynamicProxy.Contributors;
using System.Threading.Tasks;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EOF3670Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);

        protected ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Shanghai.Instance)
        {
            IsEip4200Enabled = false,
            IsEip4750Enabled = false
        });

        public static IEnumerable<TestCase> Eip3670TestCases
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
                    ResultIfEOF = (StatusCode.Failure, null),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "Last opcode is not a terminating opcode"
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
                    ResultIfEOF = (StatusCode.Failure, "Invalid Jump Destination"),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution with data section: Try to jump into data section"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(4)
                            .Op(Instruction.JUMP)
                            .Op(Instruction.CODESIZE)
                            .Done,
                    Data = Prepare.EvmCode
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Failure, "End Instruction is not a terminal opcode"),
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
                    ResultIfEOF = (StatusCode.Failure, "Invalid Jump Destination"),
                    ResultIfNotEOF = (StatusCode.Success, null),
                    Description = "EOF1 execution : Try to conditinally jump into data section"
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .PushData(1)
                            .PushData(6)
                            .Op(Instruction.JUMPI)
                            .Op(Instruction.CODESIZE)
                            .Done,
                    Data = Prepare.EvmCode
                            .Op(Instruction.JUMPDEST)
                            .PushData(1)
                            .PushData(0)
                            .Op(Instruction.MSTORE8)
                            .Return(1, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Failure, "Missing End Instruction"),
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


                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                            .Op(0xfb) // works as long as 0xfb is not defined yet
                            .PushData(new byte[] { 23, 69 })
                            .Return(2, 0)
                            .Done,
                    ResultIfEOF = (StatusCode.Failure, "Undefined Opcode"),
                    ResultIfNotEOF = (StatusCode.Failure, "Undefined Opcode"),
                    Description = "EOF1 execution : includes PUSHx Intructions"
                };
            }
        }


        // valid code
        [TestCase("0xEF000101000100FE", true)]
        [TestCase("0xEF00010100050060006000F3", true)]
        [TestCase("0xEF00010100050060006000FD", true)]
        [TestCase("0xEF0001010003006000FF", true)]
        [TestCase("0xEF0001010022007F000000000000000000000000000000000000000000000000000000000000000000", true)]
        [TestCase("0xEF0001010022007F0C0D0E0F1E1F2122232425262728292A2B2C2D2E2F494A4B4C4D4E4F5C5D5E5F00", true)]
        [TestCase("0xEF000101000102002000000C0D0E0F1E1F2122232425262728292A2B2C2D2E2F494A4B4C4D4E4F5C5D5E5F", true)]
        // code with invalid magic
        [TestCase("0xEF0001010001000C", false, Description = "Undefined instruction")]
        [TestCase("0xEF000101000100EF", false, Description = "Undefined instruction")]
        [TestCase("0xEF00010100010060", false, Description = "Missing terminating instruction")]
        [TestCase("0xEF00010100010030", false, Description = "Missing terminating instruction")]
        [TestCase("0xEF0001010020007F00000000000000000000000000000000000000000000000000000000000000", false, Description = "Missing terminating instruction")]
        [TestCase("0xEF0001010021007F0000000000000000000000000000000000000000000000000000000000000000", false, Description = "Missing terminating instruction")]
        public void EIP3670_Compliant_formats_Test(string code, bool isCorrectlyFormated)
        {
            var bytecode = Prepare.EvmCode
                .FromCode(code)
                .Done;

            var TargetReleaseSpec = new OverridableReleaseSpec(Shanghai.Instance)
            {
                IsEip4200Enabled = false,
                IsEip4750Enabled = false
            };


            bool checkResult = ValidateByteCode(bytecode, TargetReleaseSpec, out _);

            checkResult.Should().Be(isCorrectlyFormated);
        }

        [Test]
        public void Eip3670_execution_tests([ValueSource(nameof(Eip3670TestCases))] TestCase testcase)
        {
            var bytecode = testcase.GenerateCode(true);

            TestAllTracerWithOutput receipts = Instance.EOF_contract_execution_tests(bytecode);

            receipts.StatusCode.Should().Be(testcase.ResultIfEOF.Status, $"{testcase.Description}");
        }

        public static IEnumerable<TestCase> Eip3670TxTestCases
        {
            get
            {
                int idx = 0;
                byte[] salt = { 4, 5, 6 };
                var standardCode = Prepare.EvmCode
                    .MUL(23, 3)
                    .STOP() // for EIP-3670 End instruction condition
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
                        1 => Prepare.EvmCode.Create(initcode, UInt256.Zero).STOP().Done,
                        2 => Prepare.EvmCode.Create2(initcode, salt, UInt256.Zero).STOP().Done,
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
                            bool corruptDeploycode = (k & 2) == 4;
                            yield return new TestCase
                            {
                                Index = idx++,
                                Code = EmitBytecode(standardCode, standardData,
                                    hasEofContainer, hasEofInnitcode, hasEofDeployCode,
                                    corruptContainer, corruptInnitcode, corruptDeploycode,
                                    context: j),
                                ResultIfEOF = ((classicDep ? corruptInnitcode : corruptContainer) || (!hasEofInnitcode && corruptInnitcode) || corruptDeploycode ? StatusCode.Failure : StatusCode.Success, null),
                                Description = $"EOF1 execution : \nDeploy {(hasEofInnitcode ? String.Empty : "NON-")}EOF Bytecode with {(hasEofDeployCode ? String.Empty : "NON-")}EOF innercode with {(hasEofContainer ? String.Empty : "NON-")}EOF container,\nwith Instruction {(useCreate1 ? "CREATE" : useCreate2 ? "CREATE2" : "Initcode")}, \nwith {(corruptContainer ? String.Empty : "Not")} Corrupted CONTAINER and {(corruptInnitcode ? String.Empty : "Not")} Corrupted INITCODE and {(corruptDeploycode ? String.Empty : "Not")} Corrupted CODE"
                            };
                        }
                    }
                }
            }
        }

        [Test]
        public void Eip3670_contract_deployment_tests([ValueSource(nameof(Eip3670TxTestCases))] TestCase testcase)
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Shanghai.Instance)
            {
                IsEip4200Enabled = false,
                IsEip4750Enabled = false
            };

            Instance.EOF_contract_deployment_tests(testcase, TargetReleaseSpec);
        }

    }
}
