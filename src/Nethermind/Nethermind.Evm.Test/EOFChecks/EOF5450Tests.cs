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
using TestCase2 = Nethermind.Evm.Test.EOF4750Tests.TestCase2;
using FunctionCase = Nethermind.Evm.Test.EOF4750Tests.FunctionCase;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EOF5450Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);
        protected ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Shanghai.Instance)
        {
            IsEip4200Enabled = true,
            IsEip4750Enabled = true,
            IsEip5450Enabled = true,
        });


        public static IEnumerable<TestCase2> Eip5450TestCases
        {
            get
            {
                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .MUL(3, 23)
                        .POP()
                        .STOP()
                        .Done,
                    Data = Bytes.FromHexString("deadbeef"),
                    Result = (StatusCode.Success, null),
                    Description = "One code section"
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
                        .POP()
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[]
                    {
                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .MUL()
                                .ADD(54)
                                .RETF()
                            .Done,
                            InputCount = 2,
                            OutputCount = 1
                        }
                    },
                    Result = (StatusCode.Success, null),
                    Description = "two code sections with correct in/out type section"
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .PushData(23)
                        .CALLF(1)
                        .POP()
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[]
                    {
                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .MUL()
                                .ADD(54)
                                .RETF()
                            .Done,
                            InputCount = 2,
                            OutputCount = 1
                        }
                    },
                    Result = (StatusCode.Failure, null),
                    Description = "Stack underflow : Function needs 2 args but only 1 provided"
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .PushData(20)
                        .PushData(3)
                        .CALLF(1)
                        .POP()
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[]
                    {
                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .MUL()
                                .ADD(54)
                                .RETF()
                            .Done,
                            InputCount = 1,
                            OutputCount = 1
                        }
                    },
                    Data = Bytes.FromHexString("deadbeef"),
                    Result = (StatusCode.Failure, null),
                    Description = "two code sections with incorrect in/out type section"
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .PushData(1)
                        .RJUMPI(8)
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
                        .POP()
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[]
                    {
                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .MUL()
                                .ADD(54)
                                .RETF()
                            .Done,
                            InputCount = 2,
                            OutputCount = 1
                        }
                    },
                    Data = Bytes.FromHexString("deadbeef"),
                    Result = (StatusCode.Success, null),
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .PushData(0)
                        .RJUMPI(8)
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
                        .POP()
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[]
                    {
                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .MUL()
                                .ADD(54)
                                .RETF()
                            .Done,
                            InputCount = 2,
                            OutputCount = 1
                        }
                    },
                    Data = Bytes.FromHexString("deadbeef"),
                    Result = (StatusCode.Success, null),
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .RJUMP(8)
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
                        .POP()
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[]
                    {
                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .MUL()
                                .ADD(54)
                                .RETF()
                            .Done,
                            InputCount = 2,
                            OutputCount = 1
                        }
                    },
                    Data = Bytes.FromHexString("deadbeef"),
                    Result = (StatusCode.Success, null),
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .PushData(0)
                        .RJUMPI(2)
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
                        .POP()
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[]
                    {
                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .MUL()
                                .ADD(54)
                                .RETF()
                            .Done,
                            InputCount = 2,
                            OutputCount = 1
                        }
                    },
                    Data = Bytes.FromHexString("deadbeef"),
                    Result = (StatusCode.Failure, null),
                    Description = "jump results in stack underflow"
                };


                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .PushData(1)
                        .RJUMPI(2)
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
                        .POP()
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[]
                    {
                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .MUL()
                                .ADD(54)
                                .RETF()
                            .Done,
                            InputCount = 2,
                            OutputCount = 1
                        }
                    },
                    Data = Bytes.FromHexString("deadbeef"),
                    Result = (StatusCode.Failure, null),
                    Description = "jump results in stack underflow"
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .RJUMP(2)
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
                        .POP()
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[]
                    {
                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .MUL()
                                .ADD(54)
                                .RETF()
                            .Done,
                            InputCount = 2,
                            OutputCount = 1
                        }
                    },
                    Data = Bytes.FromHexString("deadbeef"),
                    Result = (StatusCode.Failure, null),
                    Description = "jump results in stack underflow"
                };
            }
        }

        public static IEnumerable<TestCase> Eip5450cenarios
        {
            get
            {
                foreach (var testCase in Eip5450TestCases)
                {
                    foreach (var scenarioCase in testCase.GenerateScenarios())
                    {
                        yield return scenarioCase;
                    }
                }
            }
        }

        [Test]
        public void Eip5450_execution_tests([ValueSource(nameof(Eip5450cenarios))] TestCase testCase)
        {
            TestAllTracerWithOutput receipts = Instance.EOF_contract_execution_tests(testCase.Code);
            receipts.StatusCode.Should().Be(testCase.ResultIfEOF.Status, receipts.Error);
        }
    }
}
