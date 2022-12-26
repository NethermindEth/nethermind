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
using static Nethermind.Evm.Test.EofTestsBase;

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
            IsEip4750Enabled = false,
            IsEip5450Enabled = false,
        });

        public static IEnumerable<TestCase> Eip3670BodyTestCases
        {
            get
            {
                var scenarios = Enum.GetValues<BodyScenario>();
                for (int i = 0; i < 1 << (scenarios.Length - 1); i++)
                {
                    BodyScenario scenario = (BodyScenario)i;
                    yield return ScenarioCase.CreateFromScenario(scenario);
                }
            }
        }

        public static IEnumerable<TestCase> Eip3670TxTestCases
        {
            get
            {
                var basecase = new ScenarioCase(
                    Functions: new FunctionCase[] {
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


                var scenarios = Enum.GetValues<DeploymentScenario>();
                var contexts = Enum.GetValues<DeploymentContext>();
                foreach (var context in contexts)
                {
                    for (int i = 1; i < 1 << (scenarios.Length + 1); i++)
                    {
                        DeploymentScenario scenario = (DeploymentScenario)i;
                        yield return basecase.GenerateDeploymentScenarios(scenario, context);
                    }
                }
            }
        }

        [Test]
        public void EOF_Opcode_Deprecation_checks()
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Shanghai.Instance);

            Instruction[] StaticRelativeJumpsOpcode =
            {
                Instruction.CALLCODE,
                Instruction.SELFDESTRUCT,
            };

            foreach (Instruction opcode in StaticRelativeJumpsOpcode)
            {
                Assert.False(opcode.IsValid(TargetReleaseSpec));
            }
        }

        [Test]
        public void EOF_execution_tests([ValueSource(nameof(Eip3670BodyTestCases))] TestCase testcase)
        {
            TestAllTracerWithOutput receipts = Instance.EOF_contract_execution_tests(testcase.Bytecode);

            receipts.StatusCode.Should().Be(testcase.Result.Status, $"{testcase.Result.Msg}");
        }

        [Test]
        public void EOF_validation_tests([ValueSource(nameof(Eip3670BodyTestCases))] TestCase testcase)
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Shanghai.Instance)
            {
                IsEip4200Enabled = false,
                IsEip4750Enabled = false,
                IsEip5450Enabled = false,
            };

            Instance.EOF_contract_header_parsing_tests(testcase, TargetReleaseSpec);
        }

        [Test]
        public void Eip3670_contract_deployment_tests([ValueSource(nameof(Eip3670TxTestCases))] TestCase testcase)
        {
            var TargetReleaseSpec = new OverridableReleaseSpec(Shanghai.Instance)
            {
                IsEip4200Enabled = false,
                IsEip4750Enabled = false,
                IsEip5450Enabled = false,
            };

            Instance.EOF_contract_deployment_tests(testcase, TargetReleaseSpec);
        }
    }
}
