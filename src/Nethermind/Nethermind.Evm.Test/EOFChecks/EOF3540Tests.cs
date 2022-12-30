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

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using TestCase = Nethermind.Evm.Test.EofTestsBase.TestCase;
using static Nethermind.Evm.Test.EofTestsBase;

namespace Nethermind.Evm.Test.EOFChecks
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EOF3540Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);

        private ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Shanghai.Instance));

        // valid code
        public static IEnumerable<TestCase> Eip3540FmtTestCases
        {
            get
            {
                ScenarioCase basecase = new(
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
                    yield return basecase.GenerateFormatScenarios(scenario);
                }
            }
        }

        public static IEnumerable<TestCase> Eip3540TxTestCases
        {
            get
            {
                ScenarioCase basecase = new(
                    Functions: new[] {
                        new FunctionCase(
                            InputCount : 0,
                            OutputCount : 0,
                            MaxStack : 2,
                            Body : Prepare.EvmCode
                                    .MUL(23, 3)
                                    .Done
                            )
                    },
                    Data: new byte[] { 0xbb, 0xee, 0xee, 0xff }
                );


                DeploymentScenario[] scenarios = Enum.GetValues<DeploymentScenario>();
                DeploymentContext[] contexts = Enum.GetValues<DeploymentContext>();
                foreach (DeploymentContext context in contexts)
                {
                    for (int i = 2; i < 1 << (scenarios.Length + 1); i++)
                    {
                        DeploymentScenario scenario = (DeploymentScenario)i;
                        yield return basecase.GenerateDeploymentScenarios(scenario, context);
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
            Instance.EOF_contract_header_parsing_tests(testcase);
        }

        [Test]
        public void Eip3540_contract_deployment_tests([ValueSource(nameof(Eip3540TxTestCases))] TestCase testcase)
        {
            OverridableReleaseSpec targetReleaseSpec = new(Shanghai.Instance);
            Instance.EOF_contract_deployment_tests(testcase, targetReleaseSpec);
        }

    }
}
