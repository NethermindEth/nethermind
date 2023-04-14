// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
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
            IsEip4200Enabled = true,
            IsEip4750Enabled = false
        });

        public static IEnumerable<TestCase> Eip4200TxTestCases
        {
            get
            {
                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMP(1)
                                .INVALID()
                                .JUMPDEST()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMP(-13)
                                .STOP()
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.Classical,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMP(1)
                                .INVALID()
                                .JUMPDEST()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMP(-13)
                                .STOP()
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "Static Jump Opcode RJUMP not valid in Non-Eof bytecodes")
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPI(1, new byte[] { 1 })
                                .INVALID()
                                .JUMPDEST()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMP(-13)
                                .STOP()
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.Classical,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPI(1, new byte[] { 1 })
                                .INVALID()
                                .JUMPDEST()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMP(-13)
                                .STOP()
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "Static Jump Opcode RJUMPI not valid in Non-Eof bytecodes")
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 1, 2, 4 }, 1)
                                .INVALID()
                                .INVALID()
                                .PushData(2)
                                .PushData(3)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.Classical,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 1, 2, 4 }, 1)
                                .INVALID()
                                .INVALID()
                                .PushData(2)
                                .PushData(3)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "Static Jump Opcode RJUMPV not valid in Non-Eof bytecodes")
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMP(11)
                                .INVALID()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMP(-13)
                                .STOP()
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMP(0)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPI(10, new byte[] { 1 })
                                .MSTORE8(0, new byte[] { 2 })
                                .RETURN(0, 1)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPI(10, new byte[] { 0 })
                                .MSTORE8(0, new byte[] { 2 })
                                .RETURN(0, 1)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMP(11)
                                .COINBASE()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMPI(-16, new byte[] { 1 })
                                .MSTORE8(0, new byte[] { 2 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMP(11)
                                .COINBASE()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMPI(-16, new byte[] { 0 })
                                .MSTORE8(0, new byte[] { 2 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPI(0, new byte[] { 0 })
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPI(0, new byte[] { 1 })
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1).Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 1, 6 }, 0)
                                .INVALID()
                                .ADD(2, 3)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 0, 5 }, 4)
                                .ADD(2, 3)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Success, null)
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPV(new short[] { }, 0)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "Scenario : Truncated Jumptable")
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 1 }, 0)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "Jumpv to Push Immediate")
                );


                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPI(1, new byte[] { 1 })
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "JumpI to Push Immediate")
                );


                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMP(1)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "Jump to Push Immediate")
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 1 }, 0)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "Jumpv cant be last Instruction")
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMP(0)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "Jump cant be last Instruction")
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPI(0, new byte[] { 0 })
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "JumpI cant be last Instruction")
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMPV(new short[] { 5 }, 0)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "Jumpv Destination Outside of Bounds")
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .RJUMP(100)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "Jump outside Code boundaries")
                );

                yield return ScenarioCase.CreateFromBytecode(
                    BytecodeTypes.EvmObjectFormat,
                    bytecodes: new[] {
                        Prepare.EvmCode
                                .Op(Instruction.RJUMP)
                                .Done
                    },
                    databytes: Array.Empty<byte>(),
                    expectedResults: (StatusCode.Failure, "Jump truncated bytecode")
                );
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
            var targetReleaseSpec = new OverridableReleaseSpec(Cancun.Instance);

            Instance.EOF_contract_header_parsing_tests(testcase, targetReleaseSpec);
        }

        [Test]
        public void Eip3670_contract_deployment_tests([ValueSource(nameof(Eip4200TxTestCases))] TestCase testcase)
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Cancun.Instance);

            Instance.EOF_contract_deployment_tests(testcase, TargetReleaseSpec);
        }
    }
}
