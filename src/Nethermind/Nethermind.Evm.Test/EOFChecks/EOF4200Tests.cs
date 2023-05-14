// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
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
    public class EOF4200Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);
        protected ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Cancun.Instance)
        {
            IsEip4750Enabled = false,
            IsEip5450Enabled = false,
        });

        public static IEnumerable<TestCase> Eip4200TxTestCases
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
                                        .RJUMP(0)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
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
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMP(1)
                                .NOP()
                                .JUMPDEST()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Static Jump Opcode RJUMP not valid in Non-Eof bytecodes")
                };

                yield return new TestCase(3)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode // p 1 rji 0 1 00 p 0 p 1 ms rj 00 13
                                .RJUMPI(1, new byte[] { 1 })
                                .NOP()
                                .NOP()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null)
                };

                yield return new TestCase(5)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 4,
                        Prepare.EvmCode
                                        .RJUMPV(new short[] { 0, 1, 2 }, 1)
                                        .NOP()
                                        .NOP()
                                .PushData(2)
                                .PushData(3)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null)
                };

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.Classical,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 1, 2, 4 }, 1)
                                .NOP()
                                .NOP()
                                .PushData(2)
                                .PushData(3)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "Static Jump Opcode RJUMPV not valid in Non-Eof bytecodes")
                );


                yield return new TestCase(7)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase( // rj 06 00 ps 02 ps 1 ms ms rj -5 
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMP(1)
                                .STOP()
                                .MSTORE8(0, new byte[] { 1 })
                                .RJUMP(-9)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null)
                };

                yield return new TestCase(8)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMP(0)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null)
                };


                yield return new TestCase(9)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMPI(10, new byte[] { 1 })
                                .MSTORE8(0, new byte[] { 2 })
                                .RETURN(0, 1)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null)
                };

                yield return new TestCase(10)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMPI(10, new byte[] { 0 })
                                .MSTORE8(0, new byte[] { 2 })
                                .RETURN(0, 1)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null)
                };

                yield return new TestCase(11)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 3,
                        Prepare.EvmCode
                                .RJUMP(11)
                                .COINBASE()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMPI(-16, new byte[] { 1 })
                                .MSTORE8(0, new byte[] { 2 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null)
                };

                yield return new TestCase(12)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 3,
                        Prepare.EvmCode
                                .RJUMP(11)
                                .COINBASE()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMPI(-16, new byte[] { 0 })
                                .MSTORE8(0, new byte[] { 2 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null)
                };

                yield return new TestCase(13)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMPI(0, new byte[] { 0 })
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null)
                };

                yield return new TestCase(14)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMPI(0, new byte[] { 1 })
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1).Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null)
                };

                yield return new TestCase(15)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 1, 7 }, 0)
                                .NOP()
                                .ADD(2, 3)
                                .POP()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Success, null)
                };

                yield return new TestCase(16)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 0, 5 }, 4)
                                .ADD(2, 3)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Jumpv with Jumptable ")
                };

                yield return new TestCase(17)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMPV(new short[] { }, 0)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Jumpv with Empty Jumptable")
                };

                yield return new TestCase(18)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 1 }, 0)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Jumpv to Push Immediate")
                };

                yield return new TestCase(19)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMPI(1, new byte[] { 1 })
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "JumpI to Push Immediate")
                };

                yield return new TestCase(20)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                        Prepare.EvmCode
                                .RJUMP(1)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Jump to Push Immediate")
                };

                yield return new TestCase(21)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 1,
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 1 }, 0)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Jumpv cant be last Instruction")
                };

                yield return new TestCase(22)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 1,
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 5 }, 0)
                                .STOP()
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Jumpv Destination Outside of Bounds")
                };


                yield return new TestCase(23)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 0,
                        Prepare.EvmCode
                                .RJUMP(100)
                                .STOP()
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Jump Destination Outside of Bounds")
                };


                yield return new TestCase(24)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 0,
                        Prepare.EvmCode
                                .Op(Instruction.RJUMP)
                                .Done
                                )
                    },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "Jump truncated bytecode")
                };
            }
        }

        [Test]
        public void EOF_Static_jumps_activation_tests()
        {
            var targetReleaseSpec = new OverridableReleaseSpec(Cancun.Instance);

            Instruction[] StaticRelativeJumpsOpcode =
            {
                Instruction.RJUMP,
                Instruction.RJUMPI,
                Instruction.RJUMPV,
            };

            foreach (Instruction opcode in StaticRelativeJumpsOpcode)
            {
                Assert.True(opcode.IsValid(true));
            }
        }

        [Test]
        public void EOF_execution_tests([ValueSource(nameof(Eip4200TxTestCases))] TestCase testcase)
        {
            TestAllTracerWithOutput receipts = Instance.EOF_contract_execution_tests(testcase.Bytecode);

            receipts.StatusCode.Should().Be(testcase.Result.Status, $"{testcase.Result.Msg}");
        }

        [Test]
        public void EOF_validation_tests([ValueSource(nameof(Eip4200TxTestCases))] TestCase testcase)
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Cancun.Instance)
            {
                IsEip4750Enabled = false,
                IsEip5450Enabled = false,
            };

            Instance.EOF_contract_header_parsing_tests(testcase, TargetReleaseSpec);
        }
    }
}
