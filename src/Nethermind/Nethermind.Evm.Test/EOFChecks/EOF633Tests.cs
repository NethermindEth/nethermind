// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
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
    public class EOF633Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);
        protected ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Cancun.Instance));

        public static IEnumerable<TestCase> Eip633TxTestCases
        {
            get
            {
                yield return new TestCase(1)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 23,
                                    Prepare.EvmCode
                                        .PushSequence(Enumerable.Range(0, 23).Select(i => (UInt256?)i).ToArray())
                                        .CALLF(1)
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    23, 23, 23,
                                    Prepare.EvmCode
                                        .SWAPn(22)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Array.Empty<byte>()
                        ).Bytecode,
                    Result = (StatusCode.Success, "correct swapn usage"),
                };

                yield return new TestCase(2)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 24,
                                    Prepare.EvmCode
                                        .PushSequence(Enumerable.Range(0, 23).Select(i => (UInt256?)i).ToArray())
                                        .CALLF(1)
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    23, 24, 24,
                                    Prepare.EvmCode
                                        .DUPx(22)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Array.Empty<byte>()
                        ).Bytecode,
                    Result = (StatusCode.Success, "correct dupn usage"),
                };

                yield return new TestCase(3)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 23,
                                    Prepare.EvmCode
                                        .PushSequence(Enumerable.Range(0, 18).Select(i => (UInt256?)i).ToArray())
                                        .CALLF(1)
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    0, 23, 24,
                                    Prepare.EvmCode
                                        .SWAPx(20)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Array.Empty<byte>()
                        ).Bytecode,
                    Result = (StatusCode.Failure, "incorrect swapn usage"),
                };

                yield return new TestCase(4)
                {
                    Bytecode = new ScenarioCase(
                            Functions: new[] {
                                new FunctionCase(
                                    0, 0, 23,
                                    Prepare.EvmCode
                                        .CALLF(1)
                                        .STOP()
                                        .Done
                                ),
                                new FunctionCase(
                                    0, 23, 24,
                                    Prepare.EvmCode
                                        .PushSequence(Enumerable.Range(0, 18).Select(i => (UInt256?)i).ToArray())
                                        .DUPx(20)
                                        .RETF()
                                        .Done
                                )
                            },
                            Data: Array.Empty<byte>()
                        ).Bytecode,
                    Result = (StatusCode.Failure, "incorrect dupn usage"),
                };
            }
        }

        [Test]
        public void EOF_execution_tests([ValueSource(nameof(Eip633TxTestCases))] TestCase testcase)
        {
            TestAllTracerWithOutput receipts = Instance.EOF_contract_execution_tests(testcase.Bytecode);

            receipts.StatusCode.Should().Be(testcase.Result.Status, $"{testcase.Result.Msg}");
        }

        [Test]
        public void EOF_validation_tests([ValueSource(nameof(Eip633TxTestCases))] TestCase testcase)
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Cancun.Instance);

            Instance.EOF_contract_header_parsing_tests(testcase, TargetReleaseSpec);
        }
    }
}
