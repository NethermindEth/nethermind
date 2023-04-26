// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public class EOF6206Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);
        protected ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Cancun.Instance));

        public static IEnumerable<TestCase> Eip6206TxTestCases
        {
            get
            {
                yield return new TestCase(2)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 2,
                                    Prepare.EvmCode
                                        .PushSingle(1)
                                        .PushSingle(01)
                                        .CALLF(1)
                                        .POP()
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    2, 1, 4,
                                    Prepare.EvmCode
                                        .DUPx(2)
                                        .ISZERO()
                                        .RJUMPI(0x0f - 0x05)
                                        .DUPx(2)
                                        .PushSingle(01)
                                        .SWAPx(1)
                                        .SUB()
                                        .SWAPx(2)
                                        .MUL()
                                        .JUMPF(1)
                                        .SWAPx(1)
                                        .POP()
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
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 0,
                                    Prepare.EvmCode
                                        .CALLF(1)
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    0, 0, 0,
                                    Prepare.EvmCode
                                        .JUMPF(23)
                                        .Done
                                )
                            },
                            Data: Bytes.FromHexString("deadbeef")
                        ).Bytecode,
                    Result = (StatusCode.Failure, "index out of bounds"),
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
                Instruction.JUMPF,
            };

            foreach (Instruction opcode in StaticRelativeJumpsOpcode)
            {
                Assert.True(opcode.IsValid(true));
            }
        }

        [Test]
        public void EOF_execution_tests([ValueSource(nameof(Eip6206TxTestCases))] TestCase testcase)
        {
            TestAllTracerWithOutput receipts = Instance.EOF_contract_execution_tests(testcase.Bytecode);

            receipts.StatusCode.Should().Be(testcase.Result.Status, $"{testcase.Result.Msg}");
        }

        [Test]
        public void EOF_validation_tests([ValueSource(nameof(Eip6206TxTestCases))] TestCase testcase)
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Cancun.Instance);

            Instance.EOF_contract_header_parsing_tests(testcase, TargetReleaseSpec);
        }
    }
}
