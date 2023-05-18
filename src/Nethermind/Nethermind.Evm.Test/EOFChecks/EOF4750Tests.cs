// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using static Nethermind.Evm.Test.EofTestsBase;
using TestCase = Nethermind.Evm.Test.EofTestsBase.TestCase;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EOF4750Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);
        protected ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Cancun.Instance)
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
                                    0, 0, 2,
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
                    Result = (StatusCode.Failure, "Typesection Max Stack height must be < 1024"),
                };

                yield return new TestCase(1)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    1, 1, 2,
                                    Prepare.EvmCode
                                        .MUL(3, 23)
                                        .STOP()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Main Section must have 0 inputs and 0 outputs"),
                };

                yield return new TestCase(2)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 1, 2,
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
                                    0, 0, 2,
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
                                    2, 2, 2,
                                    Prepare.EvmCode
                                        .MUL()
                                        .PushData(23)
                                        .CALLF(2)
                                        .RETF()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 2, 2,
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
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 0, 2,
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
                                    0, 0, 0,
                                    Prepare.EvmCode
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(2)
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 0, 2,
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
                                .Select(idx => new FunctionCase(
                                    0, 0, 0,
                                    idx < 1023
                                        ? Prepare.EvmCode
                                            .CALLF((ushort)(idx + 1))
                                            .RETF()
                                            .Done
                                        : Prepare.EvmCode
                                            .RETF()
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
                                .Select(idx => new FunctionCase(
                                    0, 0, 0,
                                    idx < 1024
                                        ? Prepare.EvmCode
                                            .CALLF((ushort)(idx + 1))
                                            .RETF()
                                            .Done
                                        : Prepare.EvmCode
                                            .RETF()
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
                                    0, 0, 0,
                                    Prepare.EvmCode.Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Empty code section"),
                };
            }
        }

        [Test]
        public void EOF_Opcode_Deprecation_checks()
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Cancun.Instance);

            Instruction[] StaticRelativeJumpsOpcode =
            {
                Instruction.PC,
                Instruction.JUMP,
                Instruction.JUMPI,
            };

            foreach (Instruction opcode in StaticRelativeJumpsOpcode)
            {
                Assert.False(opcode.IsValid(true));
            }
        }

        [Test]
        public void EOF_Static_jumps_activation_tests()
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Cancun.Instance);

            Instruction[] StaticRelativeJumpsOpcode =
            {
                Instruction.CALLF,
                Instruction.RETF,
            };

            foreach (Instruction opcode in StaticRelativeJumpsOpcode)
            {
                Assert.True(opcode.IsValid(true));
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
            var TargetReleaseSpec = new OverridableReleaseSpec(Cancun.Instance)
            {
                IsEip5450Enabled = false
            };

            Instance.EOF_contract_header_parsing_tests(testcase, TargetReleaseSpec);
        }
    }
}
