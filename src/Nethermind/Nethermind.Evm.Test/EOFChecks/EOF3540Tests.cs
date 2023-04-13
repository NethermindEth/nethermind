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
    public class EOF3540Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);
        private IReleaseSpec CancunSpec = new OverridableReleaseSpec(Cancun.Instance) { IsEip3670Enabled = false };

        protected ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Cancun.Instance)
        {
            IsEip3670Enabled = false,
            IsEip4200Enabled = false,
            IsEip4750Enabled = false,
            IsEip5450Enabled = false,
        });
        // valid code
        public static IEnumerable<TestCase> Eip3540FmtTestCases
        {
            get
            {
                ScenarioCase baseCase = new(
                    Functions: new[] {
                        new FunctionCase(
                            InputCount : 0,
                            OutputCount : 0,
                            MaxStack : 2,
                            Body : Prepare.EvmCode
                                    .PushData(0x2a)
                                    .PushData(0x2b)
                                    .Op(Instruction.MSTORE8)
                                    .Op(Instruction.MSIZE)
                                    .PushData(0x0)
                                    .Op(Instruction.SSTORE)
                                    .Op(Instruction.STOP)
                                    .Done
                            )
                    },
                    Data: new byte[] { 0xbb, 0xee, 0xee, 0xff }
                );

                FormatScenario[] scenarios = Enum.GetValues<FormatScenario>();
                foreach (FormatScenario scenario in scenarios)
                {
                    yield return baseCase.GenerateFormatScenarios(scenario);
                }
            }
        }

        public static IEnumerable<TestCase> Eip3540TxTestCases
        {
            get
            {
                ScenarioCase baseCase = new(
                    Functions: new[] {
                        new FunctionCase(
                            InputCount : 0,
                            OutputCount : 0,
                            MaxStack : 2,
                            Body : Prepare.EvmCode
                                    .MUL(23, 3)
                                    .POP()
                                    .STOP()
                                    .Done
                            )
                    },
                    Data: new byte[] { 0xbb, 0xee, 0xee, 0xff }
                );


                DeploymentScenario[] scenarios = Enum.GetValues<DeploymentScenario>();
                DeploymentContext[] contexts = Enum.GetValues<DeploymentContext>();
                foreach (DeploymentContext context in contexts)
                {
                    for (int i = 1; i < 1 << (scenarios.Length + 1); i++)
                    {
                        DeploymentScenario scenario = (DeploymentScenario)i;
                        yield return baseCase.GenerateDeploymentScenarios(scenario, context);
                    }
                }
            }
        }

        [Test]
        public void EOF_execution_tests([ValueSource(nameof(Eip3540FmtTestCases))] TestCase testcase)
        {
            TestAllTracerWithOutput receipts = Instance.EOF_contract_execution_tests(testcase.Bytecode);
            receipts.StatusCode.Should().Be(testcase.Result.Status, $"{testcase.Result.Msg}");
        }

        [Test]
        public void EOF_parsing_tests([ValueSource(nameof(Eip3540FmtTestCases))] TestCase testcase)
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Cancun.Instance)
            {
                IsEip4200Enabled = false,
                IsEip3670Enabled = false
            };

            Instance.EOF_contract_header_parsing_tests(testcase, TargetReleaseSpec);
        }

        [Test]
        public void Eip3540_contract_deployment_tests([ValueSource(nameof(Eip3540TxTestCases))] TestCase testcase)
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Cancun.Instance)
            {
                IsEip3670Enabled = false,
                IsEip4200Enabled = false,
                IsEip4750Enabled = false,
                IsEip5450Enabled = false,
            };

            Instance.EOF_contract_deployment_tests(testcase, TargetReleaseSpec);
        }

    }
}
