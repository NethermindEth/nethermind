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
using static Nethermind.Evm.Test.EofTestsBase;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EOF4750Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);
        protected ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Shanghai.Instance)
        {
            IsEip5450Enabled = false,
        });

        public static IEnumerable<TestCase> Eip4750TxTestCases
        {
            get
            {
                yield return new TestCase(1)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 1024,
                                    Prepare.EvmCode
                                        .MUL(3, 23)
                                        .STOP()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null),
                };

                yield return new TestCase(2)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 1024,
                                    Prepare.EvmCode
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 1, 1024,
                                    Prepare.EvmCode
                                        .MUL()
                                        .ADD(54)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null),
                };

                yield return new TestCase(2)
                {
                    Bytecode = Prepare.EvmCode
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
                        .STOP()
                        .Done,
                    Result = (StatusCode.Failure, "RETF used in non-Eof context"),
                };

                yield return new TestCase(3)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 1024,
                                    Prepare.EvmCode
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .ADD()
                                        .POP()
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 1, 1024,
                                    Prepare.EvmCode
                                        .MUL()
                                        .PushData(23)
                                        .CALLF(2)
                                        .RETF()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 2, 1024,
                                    Prepare.EvmCode
                                        .ADD()
                                        .PushData(69)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null),
                };

                yield return new TestCase(4)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 1024,
                                    Prepare.EvmCode
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 0, 1024,
                                    Prepare.EvmCode
                                        .MUL()
                                        .ADD(54)
                                        .POP()
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null),
                };

                yield return new TestCase(5)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 1024,
                                    Prepare.EvmCode
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(2)
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 0, 1024,
                                    Prepare.EvmCode
                                        .MUL()
                                        .ADD(54)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Invalid Code Section call"),
                };

                yield return new TestCase(6)
                {
                    Bytecode = new ScenarioCase(
                            Functions: Enumerable.Range(0, 1024)
                                .Select(_ => new FunctionCase(
                                    0, 0, 1024,
                                    Prepare.EvmCode
                                        .STOP()
                                        .Done
                                    )
                                ).ToArray(),
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null),
                };

                yield return new TestCase(7)
                {
                    Bytecode = new ScenarioCase(
                            Functions: Enumerable.Range(0, 1025)
                                .Select(_ => new FunctionCase(
                                    0, 0, 1024,
                                    Prepare.EvmCode
                                        .STOP()
                                        .Done
                                    )
                                ).ToArray(),
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Code Section overflow (+1024)"),
                };

                yield return new TestCase(8)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 1024,
                                    Prepare.EvmCode.Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Invalid Code Section call"),
                };
            }
        }

        [Test]
        public void EOF_Opcode_Deprecation_checks()
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Shanghai.Instance);

            Instruction[] StaticRelativeJumpsOpcode =
            {
                Instruction.PC,
                Instruction.JUMP,
                Instruction.JUMPI,
            };

            foreach (Instruction opcode in StaticRelativeJumpsOpcode)
            {
                Assert.False(opcode.IsValid(TargetReleaseSpec));
            }
        }

        [Test]
        public void EOF_Static_jumps_activation_tests()
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Shanghai.Instance);

            Instruction[] StaticRelativeJumpsOpcode =
            {
                Instruction.CALLF,
                Instruction.RETF,
            };

            foreach (Instruction opcode in StaticRelativeJumpsOpcode)
            {
                Assert.True(opcode.IsValid(TargetReleaseSpec));
            }
        }

        [Test]
        public void EOF_execution_tests([ValueSource(nameof(Eip4750TxTestCases))] TestCase testcase)
        {
            TestAllTracerWithOutput receipts = Instance.EOF_contract_execution_tests(testcase.Bytecode);

            receipts.StatusCode.Should().Be(testcase.Result.Status, $"{testcase.Result.Msg}");
        }

        [Test]
        public void EOF_validation_tests([ValueSource(nameof(Eip4750TxTestCases))] TestCase testcase)
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Shanghai.Instance)
            {
                IsEip5450Enabled = false
            };

            Instance.EOF_contract_header_parsing_tests(testcase, TargetReleaseSpec);
        }
    }
}
