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
using EofTestCase = Nethermind.Evm.Test.EOF3540Tests.TestCase;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EOF4750Tests : VirtualMachineTestsBase
    {
        public class FunctionCase
        {
            public int InputCount;
            public int OutputCount;
            public byte[] Body;
        }
        public class TestCase
        {
            public int Index;
            public byte[] Main;
            public FunctionCase[] Functions;
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
                OmitSection,
                MisplaceSection,
            }
            public IEnumerable<EofTestCase> GenerateScenarios()
            {
                byte[] GenerateCase(bool ommitTypeSection, bool misplaceTypeSection)
                {
                    byte[] bytes;
                    var bytecodeSize = 3;
                    bytecodeSize += ommitTypeSection ? 0 : 3 + 2 * (Functions.Length + 1 /*Main Code*/);
                    bytecodeSize += 3 + Main.Length + Functions.Aggregate(0, (acc, funcCase) => acc + funcCase.Body.Length);
                    bytecodeSize += (Data is not null && Data.Length > 0 ? 3 + Data.Length : 0);
                    bytes = new byte[bytecodeSize];

                    int i = 0;

                    // set magic
                    bytes[i++] = 0xEF; bytes[i++] = 0x00; bytes[i++] = 0x01;

                    // set type section
                    byte[] lenBytes;
                    if(!misplaceTypeSection)
                    {
                        if (!ommitTypeSection && Functions.Length > 0)
                        {
                            lenBytes = (Functions.Length * 2).ToByteArray();
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
                        if (!ommitTypeSection && Functions.Length > 0)
                        {
                            lenBytes = (Functions.Length * 2).ToByteArray();
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
                    bytes[i++] = (byte)0; bytes[i++] = (byte)0;
                    foreach (var functionDef in Functions)
                    {
                        bytes[i++] = (byte)functionDef.InputCount; bytes[i++] = (byte)functionDef.OutputCount;
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

                for(int i = 0; i < 4; i++)
                {
                    var scenario = (Scenario)i;
                    switch(scenario)
                    {
                        case Scenario.OmitSection:
                            yield return new EofTestCase
                            {
                                Code = GenerateCase(true, false),
                                ResultIfEOF = (StatusCode.Failure, "Missing Type Section")
                            };
                            break;
                        case Scenario.MisplaceSection:
                            yield return new EofTestCase
                            {
                                Code = GenerateCase(false, true),
                                ResultIfEOF = (StatusCode.Failure, "Misplaced Type Section")
                            };
                            break;
                        default:
                            yield return new EofTestCase
                            {
                                Code = GenerateCase(false, false),
                                ResultIfEOF = Result
                            };
                            break;
                    }
                }
            }
        }

        public static IEnumerable<TestCase> Eip4750TestCases
        {
            get
            {
                yield return new TestCase
                {
                    Main = Prepare.EvmCode
                        .PushData(23)
                        .PushData(3)
                        .MUL(1)
                        .STOP()
                        .Done,
                    Data = Bytes.FromHexString("deadbeef"),
                    Result = (StatusCode.Success, null),
                    
                };

                yield return new TestCase
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

                yield return new TestCase
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

                yield return new TestCase
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


                yield return new TestCase
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

                yield return new TestCase
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

                yield return new TestCase
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

                yield return new TestCase
                {
                    Main = Prepare.EvmCode
                        .STOP()
                        .Done,
                    Functions = Enumerable.Range(0, 1024).Select(_ => new FunctionCase
                    {
                        Body = Prepare.EvmCode
                                .INVALID()
                                .Done,
                        InputCount = 2,
                        OutputCount = 0
                    }).ToArray(),
                    Result = (StatusCode.Success, null),
                    
                };


                yield return new TestCase
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


                yield return new TestCase
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

        public static IEnumerable<IReleaseSpec> Specs
        {
            get
            {
                yield return GrayGlacier.Instance;
                yield return Shanghai.Instance;
            }
        }

        [Test]
        public void Eip4750_execution_tests([ValueSource(nameof(Eip4750TestCases))] TestCase testcase, [ValueSource(nameof(Specs))] IReleaseSpec spec)
        {
            foreach(var scenarioCase in testcase.GenerateScenarios()) {
                bool isShanghaiBlock = spec is Shanghai;
                long blockTestNumber = isShanghaiBlock ? BlockNumber : BlockNumber - 1;



                var TargetReleaseSpec = new OverridableReleaseSpec(isShanghaiBlock ? Shanghai.Instance : GrayGlacier.Instance)
                {
                    IsEip4200Enabled = false,
                    IsEip4750Enabled = false
                };

                ILogManager logManager = GetLogManager();
                var customSpecProvider = new TestSpecProvider(Frontier.Instance, TargetReleaseSpec);
                Machine = new VirtualMachine(blockhashProvider, customSpecProvider, logManager);
                _processor = new TransactionProcessor(customSpecProvider, TestState, Storage, Machine, LimboLogs.Instance);

                TestAllTracerWithOutput receipts = Execute(blockTestNumber, Int64.MaxValue, scenarioCase.Code, Int64.MaxValue);

                if (isShanghaiBlock)
                {
                    receipts.StatusCode.Should().Be(scenarioCase.ResultIfEOF.Status, testcase.Description);
                }

                if (!isShanghaiBlock)
                {
                    receipts.StatusCode.Should().Be(StatusCode.Failure, testcase.Description);
                }
            }
        }

        // valid code
        [TestCase("0xEF00010100010000", true, 1, 0, true)]
        [TestCase("0xEF0001010002006000", true, 2, 0, true)]
        [TestCase("0xEF0001010002020001006000AA", true, 2, 1, true)]
        [TestCase("0xEF0001010002020004006000AABBCCDD", true, 2, 4, true)]
        [TestCase("0xEF00010100040200020060006001AABB", true, 4, 2, true)]
        [TestCase("0xEF000101000602000400600060016002AABBCCDD", true, 6, 4, true)]
        // code with invalid magic
        [TestCase("0xEF", false, 0, 0, true, Description = "Incomplete Magic")]
        [TestCase("0xEF01", false, 0, 0, true, Description = "Incorrect Magic second byte")]
        [TestCase("0xEF0101010002020004006000AABBCCDD", false, 0, 0, true, Description = "Valid code with wrong magic second byte")]
        // code with valid magic but invalid body
        [TestCase("0xEF0000010002020004006000AABBCCDD", false, 0, 0, true, Description = "Invalid Version")]
        [TestCase("0xEF00010100", false, 0, 0, true, Description = "Code section missing")]
        [TestCase("0xEF0001010002006000DEADBEEF", false, 0, 0, true, Description = "Invalid total Size")]
        [TestCase("0xEF00010100020100020060006000", false, 0, 0, true, Description = "Multiple Code sections")]
        [TestCase("0xEF000101000002000200AABB", false, 0, 0, true, Description = "Empty code section")]
        [TestCase("0xEF000102000401000200AABBCCDD6000", false, 0, 0, true, Description = "Data section before code section")]
        [TestCase("0xEF000101000202", false, 0, 0, true, Description = "Data Section size Missing")]
        [TestCase("0xEF0001010002020004020004006000AABBCCDDAABBCCDD", false, 0, 0, true, Description = "Multiple Data sections")]
        [TestCase("0xEF0001010002030004006000AABBCCDD", false, 0, 0, true, Description = "Unknown Section")]
        // tests proposed on the eip paper
        [TestCase("0xEF", false, 0, 0, true, Description = "Incomplete magic")]
        [TestCase("0xEFFF0101000302000400600000AABBCCDD", false, 0, 0, true, Description = "Invalid magic")]
        [TestCase("0xEF00", false, 0, 0, true, Description = "No version")]
        [TestCase("0xEF000001000302000400600000AABBCCDD", false, 0, 0, true, Description = "Invalid version")]
        [TestCase("0xEF000201000302000400600000AABBCCDD", false, 0, 0, true, Description = "Invalid version")]
        [TestCase("0xEF00FF01000302000400600000AABBCCDD", false, 0, 0, true, Description = "Invalid version")]
        [TestCase("0xEF0001", false, 0, 0, true, Description = "No header")]
        [TestCase("0xEF000100", false, 0, 0, true, Description = "No code section")]
        [TestCase("0xEF000101", false, 0, 0, true, Description = "No code section size")]
        [TestCase("0xEF00010100", false, 0, 0, true, Description = "Code section size incomplete")]
        [TestCase("0xEF0001010003", false, 0, 0, true, Description = "No section terminator")]
        [TestCase("0xEF0001010003600000", false, 0, 0, true, Description = "No section terminator")]
        [TestCase("0xEF000101000200", false, 0, 0, true, Description = "No code section contents")]
        [TestCase("0xEF00010100020060", false, 0, 0, true, Description = "Code section contents incomplete")]
        [TestCase("0xEF000101000300600000DEADBEEF", false, 0, 0, true, Description = "Trailing bytes after code section")]
        [TestCase("0xEF000101000301000300600000600000", false, 0, 0, true, Description = "Multiple code sections")]
        [TestCase("0xEF000101000000", false, 0, 0, true, Description = "Empty code section")]
        [TestCase("0xEF000101000002000200AABB", false, 0, 0, true, Description = "Empty code section (with non-empty data section)")]
        [TestCase("0xEF000102000401000300AABBCCDD600000", false, 0, 0, true, Description = "Data section preceding code section")]
        [TestCase("0xEF000102000400AABBCCDD", false, 0, 0, true, Description = "Data section without code section")]
        [TestCase("0xEF000101000202", false, 0, 0, true, Description = "No data section size")]
        [TestCase("0xEF00010100020200", false, 0, 0, true, Description = "Data section size incomplete")]
        [TestCase("0xEF0001010003020004", false, 0, 0, true, Description = "No section terminator")]
        [TestCase("0xEF0001010003020004600000AABBCCDD", false, 0, 0, true, Description = "No section terminator")]
        [TestCase("0xEF000101000302000400600000", false, 0, 0, true, Description = "No data section contents")]
        [TestCase("0xEF000101000302000400600000AABBCC", false, 0, 0, true, Description = "Data section contents incomplete")]
        [TestCase("0xEF000101000302000400600000AABBCCDDEE", false, 0, 0, true, Description = "Trailing bytes after data section")]
        [TestCase("0xEF000101000302000402000400600000AABBCCDDAABBCCDD", false, 0, 0, true, Description = "Multiple data sections")]
        [TestCase("0xEF000101000101000102000102000100FEFEAABB", false, 0, 0, true, Description = "Multiple code and data sections")]
        [TestCase("0xEF000101000302000000600000", false, 0, 0, true, Description = "Empty data section")]
        [TestCase("0xEF0001010002030004006000AABBCCDD", false, 0, 0, true, Description = "Unknown section (id = 3)")]
        public void EIP4750_Compliant_formats_Test(string code, bool isCorrectFormated, int codeSize, int dataSize, bool isShanghaiFork)
        {
            var bytecode = Prepare.EvmCode
                .FromCode(code)
                .Done;

            var TargetReleaseSpec = isShanghaiFork ? Shanghai.Instance : GrayGlacier.Instance;

            var expectedHeader = codeSize == 0 && dataSize == 0
                ? null
                : new EofHeader
                {
                    CodeSize = new int[] { codeSize },
                    DataSize = dataSize,
                    Version = 1
                };
            var expectedJson = JsonSerializer.Serialize(expectedHeader);
            var checkResult = ValidateByteCode(bytecode, TargetReleaseSpec, out var header);
            var actualJson = JsonSerializer.Serialize(header);

            if (isShanghaiFork)
            {
                Assert.AreEqual(actualJson, expectedJson);
                checkResult.Should().Be(isCorrectFormated);
            }
            else
            {
                checkResult.Should().Be(isCorrectFormated);
            }
        }
    }
}
