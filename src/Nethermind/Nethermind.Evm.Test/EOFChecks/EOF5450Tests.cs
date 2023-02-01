// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using static Nethermind.Evm.Test.EofTestsBase;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EOF5450Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);
        protected ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Cancun.Instance));

        public static IEnumerable<TestCase> Eip5450TxTestCases
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
                                        .POP()
                                        .STOP()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, "Mono-Section bytecode"),
                };

                yield return new TestCase(1)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .MUL(3, 23)
                                        .POP()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "CodeSection ending in a non-terminating opcode"),
                };

                yield return new TestCase(1)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    1, 0, 2,
                                    Prepare.EvmCode
                                        .MUL(3)
                                        .POP()
                                        .STOP()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Mono-Section with incorrect input count"),
                };

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
                    Result = (StatusCode.Success, "Mono-Section with incorrect output count"),
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
                                        .POP()
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
                    Result = (StatusCode.Success, "Di-Section bytecode"),
                };


                yield return new TestCase(3)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                       .RJUMPV(new short[] { 6, 12 }, 3)
                                       .PushData(0)
                                       .PushData(1)
                                       .ADD().POP()
                                       .PushData(2)
                                       .PushData(3)
                                       .MUL().POP()
                                       .MSTORE8(0, new byte[] { 1 })
                                       .RETURN(0, 1)
                                       .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, "Use Jumpv jumptables with valid destinations"),
                };

                yield return new TestCase(4)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                       .RJUMPV(new short[] { 2, 5, 6 }, 0)
                                       .PushData(0)
                                       .PushData(1)
                                       .ADD().POP()
                                       .MSTORE8(0, new byte[] { 1 })
                                       .RETURN(0, 1)
                                       .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Use Jumpv jumptables with invalid destinations"),
                };

                yield return new TestCase(5)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .PushData(23)
                                        .CALLF(1)
                                        .POP()
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
                    Result = (StatusCode.Failure, "Function requires more than provided (stack underflow)"),
                };

                yield return new TestCase(6)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .PushData(20)
                                        .PushData(3)
                                        .CALLF(1)
                                        .POP()
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    1, 1, 1,
                                    Prepare.EvmCode
                                        .MUL()
                                        .ADD(54)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Invalid Input stack height counts"),
                };

                yield return new TestCase(6)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .PushData(20)
                                        .PushData(3)
                                        .CALLF(1)
                                        .POP()
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 0, 1,
                                    Prepare.EvmCode
                                        .MUL()
                                        .ADD(54)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Invalid Output stack height counts"),
                };

                yield return new TestCase(6)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .PushData(20)
                                        .PushData(3)
                                        .CALLF(1)
                                        .POP()
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 0, 2,
                                    Prepare.EvmCode
                                        .MUL()
                                        .PushSequence(0, 1)
                                        .POP().POP()
                                        .ADD(54)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Invalid Max Stack Height"),
                };

                yield return new TestCase(7)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .RJUMPI(8, new byte[] { 1 })
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .POP()
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
                    Result = (StatusCode.Success, "Conditional Static Jump with Valid destination"),
                };

                yield return new TestCase(8)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .RJUMPI(8, new byte[] { 0 })
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .POP()
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
                    Result = (StatusCode.Success, "Conditional Static Jump with Valid destination"),
                };

                yield return new TestCase(8)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .RJUMPI(8, new byte[] { 0 })
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .POP()
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
                    Result = (StatusCode.Success, "Conditional Static Jump with Valid destination"),
                };

                yield return new TestCase(9)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .RJUMP(8)
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .POP()
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
                    Result = (StatusCode.Failure, "Static Jump With Unreachable opcode"),
                };

                yield return new TestCase(9)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .RJUMP(0)
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .POP()
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
                    Result = (StatusCode.Success, "Static Jump with Valid destination"),
                };

                yield return new TestCase(10)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .RJUMPI(2, new byte[] { 1 })
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .POP()
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 1, 1,
                                    Prepare.EvmCode
                                        .MUL()
                                        .ADD(54)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Conditional Jump results in Stack Underflow"),
                };

                yield return new TestCase(11)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .RJUMPI(2, new byte[] { 0 })
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .POP()
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 1, 1,
                                    Prepare.EvmCode
                                        .MUL()
                                        .ADD(54)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Conditional Jump results in Stack Underflow"),
                };

                yield return new TestCase(12)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .RJUMP(2)
                                        .PushData(23)
                                        .PushData(3)
                                        .CALLF(1)
                                        .POP()
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 1, 1,
                                    Prepare.EvmCode
                                        .MUL()
                                        .ADD(54)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Jump results in Stack Underflow"),
                };

                yield return new TestCase(13)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .RJUMP(2)
                                        .PushSequence(
                                                Enumerable.Range(0, 3)
                                                          .Select(i => (UInt256?)i)
                                                          .ToArray()
                                        ).PushData(3)
                                         .CALLF(1)
                                         .POP()
                                         .RETF()
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
                    Result = (StatusCode.Failure, "Stack is not empty after main"),
                };

                yield return new TestCase(14)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .RJUMP(2)
                                        .PushSequence(
                                            Enumerable.Range(0, 1025)
                                                        .Select(i => (UInt256?)i)
                                                        .ToArray()
                                        )
                                        .PushData(3)
                                        .CALLF(1)
                                        .PutSequence(
                                            Enumerable.Range(0, 1023)
                                                        .Select(_ => Instruction.POP)
                                                        .ToArray()
                                        )
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
                    Result = (StatusCode.Failure, "Max items in stack is 1025 > 1023"),
                };

                yield return new TestCase(15)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .PushData(23)
                                        .PushData(3)
                                        .MUL()
                                        .POP()
                                        .RETF()
                                        .PushSequence(22, 1)
                                        .CALLF(1)
                                        .POP()
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 1, 1,
                                    Prepare.EvmCode
                                        .ADD()
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Contains Unreachable Code"),
                };
            }
        }

        [Test]
        public void EOF_execution_tests([ValueSource(nameof(Eip5450TxTestCases))] TestCase testcase)
        {
            TestAllTracerWithOutput receipts = Instance.EOF_contract_execution_tests(testcase.Bytecode);

            receipts.StatusCode.Should().Be(testcase.Result.Status, $"{testcase.Result.Msg}");
        }

        [Test]
        public void EOF_validation_tests([ValueSource(nameof(Eip5450TxTestCases))] TestCase testcase)
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Cancun.Instance);

            Instance.EOF_contract_header_parsing_tests(testcase, TargetReleaseSpec);
        }
    }
}
