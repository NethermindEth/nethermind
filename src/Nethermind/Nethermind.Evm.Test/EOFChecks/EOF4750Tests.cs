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

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EOF4750Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);
        protected ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Shanghai.Instance)
        {
            IsEip4200Enabled = true,
            IsEip4750Enabled = true
        });

        public class FunctionCase
        {
            public int InputCount;
            public int OutputCount;
            public byte[] Body;
        }
        public class TestCase2
        {
            public int Index;
            public byte[] Main;
            public FunctionCase[] Functions = Array.Empty<FunctionCase>();
            public byte[] Data;
            public byte[] Type;
            public (byte Status, string error) Result;
            public string Description;
            public IEnumerable<byte[]> Routines
            {
                get
                {
                    yield return Main;
                    foreach(var functionDef in Functions)
                    {
                        yield return functionDef.Body;
                    }
                }
            }

            private enum Scenario
            {
                OmitSection = 1,
                MisplaceSection = 2,
            }
            public IEnumerable<TestCase> GenerateScenarios()
            {
                byte[] GenerateCase(bool ommitTypeSection, bool misplaceTypeSection)
                {
                    byte[] bytes;
                    var bytecodeSize = 3;
                    bytecodeSize += ommitTypeSection ? 0 : 3; // typesectionHeader
                    bytecodeSize += ommitTypeSection ? 0 : 2 * (Functions.Length + 1 /*Main Code*/); // typesection
                    bytecodeSize += 1 + 2 * (Functions.Length + 1 /*Main Code*/); // codesectionHeader 
                    bytecodeSize += Main.Length + Functions.Aggregate(0, (acc, funcCase) => acc + funcCase.Body.Length); // codesection 
                    bytecodeSize += (Data is not null && Data.Length > 0 ? 3 : 0); // datasectionHeader 
                    bytecodeSize += (Data is not null && Data.Length > 0 ? Data.Length : 0); // datasection
                    bytecodeSize += 1; // terminator
                    bytes = new byte[bytecodeSize];

                    int i = 0;

                    // set magic
                    bytes[i++] = 0xEF; bytes[i++] = 0x00; bytes[i++] = 0x01;

                    // set type section
                    byte[] lenBytes;
                    if(!misplaceTypeSection)
                    {
                        if (!ommitTypeSection)
                        {
                            lenBytes = ((1 + Functions.Length) * 2).ToByteArray();
                            bytes[i++] = 0x03; bytes[i++] = lenBytes[^2]; bytes[i++] = lenBytes[^1];
                        }
                    }
                    
                    // set code section
                    bytes[i++] = 0x01;

                    lenBytes = Main.Length.ToByteArray();
                    bytes[i++] = lenBytes[^2]; bytes[i++] = lenBytes[^1];

                    foreach (var functionCase in Functions)
                    {
                        lenBytes = functionCase.Body.Length.ToByteArray();
                        bytes[i++] = lenBytes[^2]; bytes[i++] = lenBytes[^1];
                    }

                    if (misplaceTypeSection)
                    {
                        if (!ommitTypeSection)
                        {
                            lenBytes = ((1 + Functions.Length) * 2).ToByteArray();
                            bytes[i++] = 0x03; bytes[i++] = lenBytes[^2]; bytes[i++] = lenBytes[^1];
                        }
                    }

                    // set Data section
                    if (Data is not null && Data.Length > 0)
                    {
                        lenBytes = Data.Length.ToByteArray();
                        bytes[i++] = 0x02; bytes[i++] = lenBytes[^2]; bytes[i++] = lenBytes[^1];
                    }

                    // set the terminator byte
                    bytes[i++] = 0x00;

                    //set typesection
                    if(!ommitTypeSection)
                    {
                        bytes[i++] = (byte)0; bytes[i++] = (byte)0;
                        foreach (var functionDef in Functions)
                        {
                            bytes[i++] = (byte)functionDef.InputCount; bytes[i++] = (byte)functionDef.OutputCount;
                        }
                    }

                    foreach (var bytecode in Routines)
                    {
                        Array.Copy(bytecode, 0, bytes, i, bytecode.Length);
                        i += bytecode.Length;
                    }

                    if (Data is not null && Data.Length > 0)
                    {
                        Array.Copy(Data, 0, bytes, i, Data.Length);
                    }

                    return bytes;
                }

                for(int i = 3; i <= 3; i++)
                {
                    var scenario = (Scenario)i;
                    switch(scenario)
                    {
                        case Scenario.OmitSection:
                            yield return new TestCase
                            {
                                Code = GenerateCase(true, false),
                                ResultIfEOF = (StatusCode.Failure, "Missing Type Section")
                            };
                            break;
                        case Scenario.MisplaceSection:
                            yield return new TestCase
                            {
                                Code = GenerateCase(false, true),
                                ResultIfEOF = (StatusCode.Failure, "Misplaced Type Section")
                            };
                            break;
                        default:
                            yield return new TestCase
                            {
                                Code = GenerateCase(false, false),
                                ResultIfEOF = Result
                            };
                            break;
                    }
                }
            }
        }

        public static IEnumerable<TestCase2> Eip4750TestCases
        {
            get
            {
                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .MUL(3, 23)
                        .STOP()
                        .Done,
                    Data = Bytes.FromHexString("deadbeef"),
                    Result = (StatusCode.Success, null),
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
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
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
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
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
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
                    Result = (StatusCode.Failure, "Code ending in non terminating opcode"),
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[]
                    {
                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .MUL()
                                .ADD(54)
                                .Done,
                            InputCount = 2,
                            OutputCount = 1
                        }
                    },
                    Result = (StatusCode.Failure, "Code ending in non terminating opcode"),
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .PushData(23)
                        .PushData(3)
                        .CALLF(2)
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[]
                    {
                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .MUL()
                                .ADD(54)
                                .Done,
                            InputCount = 2,
                            OutputCount = 0
                        }
                    },
                    Result = (StatusCode.Failure, "Invalid Code Section call"),
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .PushData(23)
                        .PushData(3)
                        .CALLF(1)
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[]
                    {
                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .MUL()
                                .CALLF(2)
                                .RETF()
                                .Done,
                            InputCount = 2,
                            OutputCount = 0
                        },

                        new FunctionCase{
                            Body = Prepare.EvmCode
                                .ADD(54)
                                .RJUMP(1)
                                .INVALID()
                                .RETF()
                                .Done,
                            InputCount = 1,
                            OutputCount = 0
                        }
                    },
                    Result = (StatusCode.Failure, "Invalid Code Section call"),
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .STOP()
                        .Done,
                    Functions = Enumerable.Range(0, 1023).Select(_ => new FunctionCase
                    {
                        Body = Prepare.EvmCode
                                .INVALID()
                                .Done,
                        InputCount = 0,
                        OutputCount = 0
                    }).ToArray(),
                    Result = (StatusCode.Success, null),
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .STOP()
                        .Done,
                    Functions = new FunctionCase[] {
                        new FunctionCase
                        {
                            Body = Prepare.EvmCode
                                    .Done,
                            InputCount = 2,
                            OutputCount = 0
                        }
                    },
                    Result = (StatusCode.Failure, null),
                };

                yield return new TestCase2
                {
                    Main = Prepare.EvmCode
                        .STOP()
                        .Done,
                    Functions = Enumerable.Range(0, 1025).Select(_ => new FunctionCase
                    {
                        Body = Prepare.EvmCode
                                .INVALID()
                                .Done,
                        InputCount = 2,
                        OutputCount = 0
                    }).ToArray(),
                    Result = (StatusCode.Failure, "Code Section overflow (+1024)"),
                };
            }
        }

        public static IEnumerable<TestCase> Eip4750Scenarios
        {
            get
            {
                foreach (var testCase in Eip4750TestCases)
                {
                    foreach (var scenarioCase in testCase.GenerateScenarios())
                    {
                        yield return scenarioCase;
                    }
                }
            }
        }

        [Test]
        public void Eip4750_execution_tests([ValueSource(nameof(Eip4750Scenarios))] TestCase testCase)
        {
            TestAllTracerWithOutput receipts = Instance.EOF_contract_execution_tests(testCase.Code);
            receipts.StatusCode.Should().Be(testCase.ResultIfEOF.Status, receipts.Error);
        }

        // valid code
        [TestCase("0xEF000101000100FE", true)]
        [TestCase("0xEF00010100050060006000F3", true)]
        [TestCase("0xEF00010100050060006000FD", true)]
        [TestCase("0xEF0001010003006000FF", true)]
        [TestCase("0xEF0001010022007F000000000000000000000000000000000000000000000000000000000000000000", true)]
        [TestCase("0xEF0001010022007F0C0D0E0F1E1F2122232425262728292A2B2C2D2E2F494A4B4C4D4E4F5C5D5E5F00", true)]
        [TestCase("0xEF000101000102002000000C0D0E0F1E1F2122232425262728292A2B2C2D2E2F494A4B4C4D4E4F5C5D5E5F", true)]
        // code with invalid magic
        [TestCase("0xEF0001010001000C", false, Description = "Undefined instruction")]
        [TestCase("0xEF000101000100EF", false, Description = "Undefined instruction")]
        [TestCase("0xEF00010100010060", false, Description = "Missing terminating instruction")]
        [TestCase("0xEF00010100010030", false, Description = "Missing terminating instruction")]
        [TestCase("0xEF0001010020007F00000000000000000000000000000000000000000000000000000000000000", false, Description = "Missing terminating instruction")]
        [TestCase("0xEF0001010021007F0000000000000000000000000000000000000000000000000000000000000000", false, Description = "Missing terminating instruction")]
        public void EIP4570_Compliant_formats_Test(string code, bool isCorrectlyFormated)
        {
            var bytecode = Prepare.EvmCode
                .FromCode(code)
                .Done;

            var TargetReleaseSpec = new OverridableReleaseSpec(Shanghai.Instance)
            {
                IsEip4750Enabled = true
            };


            bool checkResult = ValidateByteCode(bytecode, TargetReleaseSpec, out _);

            checkResult.Should().Be(isCorrectlyFormated);
        }
    }
}
