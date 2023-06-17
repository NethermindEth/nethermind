// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.EOF;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EofTestsBase : VirtualMachineTestsBase
    {
        [Flags]
        public enum BytecodeTypes
        {
            Classical, EvmObjectFormat
        }

        [Flags]
        public enum FormatScenario
        {
            None = 1,

            OmitTypeSectionHeader = 1 << 1,
            OmitCodeSectionsHeader = 1 << 2,
            OmitDataSectionHeader = 1 << 3,

            IncompleteTypeSectionHeader = 1 << 4,
            IncompleteCodeSectionsHeader = 1 << 5,
            IncompleteDataSectionHeader = 1 << 6,

            IncorrectTypeSectionHeader = 1 << 7,
            IncorrectCodeSectionsHeader = 1 << 8,
            IncorrectDataSectionHeader = 1 << 9,

            MultipleTypeSectionHeaders = 1 << 10,
            MultipleCodeSectionsHeaders = 1 << 11,
            MultipleDataSectionHeaders = 1 << 12,

            MisplaceSectionHeaders = 1 << 13,

            IncompleteBody = 1 << 14,

            InvalidVersion = 1 << 15,
            MissingTerminator = 1 << 16,
            IncorrectSectionKind = 1 << 17,
            InvalidMagic = 1 << 18,

            TrailingBytes = 1 << 19,
        }

        [Flags]
        public enum BodyScenario
        {
            None = 0,
            UseUndefinedOpcode = 1 << 0,
            UseDeprecatedOpcode = 1 << 1,
            EndWithTruncatedPush = 1 << 2,
            WithEmptyCodeSection = 1 << 3,

            WithDataSection = 1 << 4,
        }

        [Flags]
        public enum DeploymentScenario
        {
            None = 1,
            EofInitCode = 1 << 1,
            EofDeployedCode = 1 << 2,

            CorruptInitCode = 1 << 3,
            CorruptDeployedCode = 1 << 4,
        }

        [Flags]
        public enum DeploymentContext
        {
            UseCreate = 1 << 1,
            UseCreate2 = 1 << 2,
            UseCreateTx = 1 << 3,
        }

        public static EofTestsBase Instance(ISpecProvider provider)
        {
            EofTestsBase instance = new() { SpecProvider = provider };
            instance.Setup();
            return instance;
        }

        public void EOF_contract_header_parsing_tests(TestCase testcase, IReleaseSpec spec)
        {
            if (!testcase.isEofCode) return;

            var result = EvmObjectFormat.IsValidEof(
                testcase.Bytecode, out EofHeader? header
            );

            Assert.That(result, Is.EqualTo(testcase.Result.Status == StatusCode.Success), $"Scenario : {testcase.Result.Msg}");
            if (result == false)
            {
                Assert.IsNull(header);
            }
            else
            {
                Assert.IsNotNull(header);
            }
        }
        public TestAllTracerWithOutput EOF_contract_execution_tests(byte[] testcase) =>
            Execute(BlockNumber, Timestamp, testcase);

        public void EOF_contract_deployment_tests(TestCase testcase, IReleaseSpec spec)
        {
            TestState.CreateAccount(TestItem.AddressC, 200.Ether());
            byte[] createContract = testcase.Bytecode;

            ILogManager logManager = GetLogManager();
            TestSpecProvider customSpecProvider = new(Frontier.Instance, spec);
            Machine = new VirtualMachine(TestBlockhashProvider.Instance, customSpecProvider, logManager);
            _processor = new TransactionProcessor(customSpecProvider, TestState, Machine, LimboLogs.Instance);
            (Block block, Transaction transaction) = testcase.UseCreateTx
                ? PrepareInitTx(BlockNumber, 100000, createContract)
                : PrepareTx(BlockNumber, 100000, createContract);

            var txTracer = new TestAllTracerWithOutput();
            _processor.Execute(transaction, block.Header, txTracer);
            Assert.That(txTracer.ReportedActionErrors.Any(x => x is EvmExceptionType.InvalidCode or EvmExceptionType.InvalidEofCode) ? StatusCode.Failure : StatusCode.Success, Is.EqualTo(testcase.Result.Status), testcase.Result.Msg);

        }

        public record TestCase(int Index)
        {
            public byte[] Bytecode;
            public bool isEofCode = true;
            public DeploymentContext Ctx;
            public bool UseCreateTx = true;
            public (byte Status, string Msg) Result;
        }

        public record FunctionCase(int InputCount, int OutputCount, int MaxStack, byte[] Body);
        public record ScenarioCase(FunctionCase[] Functions, byte[] Data)
        {
            public static TestCase CreateFromBytecode(BytecodeTypes bytecodeType, byte[][] bytecodes, byte[] databytes, int idx = -1, (byte Status, String Message)? expectedResults = null)
            {
                var caseBytecode = bytecodeType switch
                {
                    BytecodeTypes.EvmObjectFormat => new ScenarioCase(bytecodes.Select(sectionCode => new FunctionCase(0, 0, 1023, sectionCode)).ToArray(), databytes).Bytecode,
                    BytecodeTypes.Classical => bytecodes[0],
                    _ => throw new UnreachableException()
                };

                var resultMessage = expectedResults is null ? ((byte)0, String.Empty) : (expectedResults.Value.Status, $"EOF1 validation : \nbytecode {caseBytecode.ToHexString(true)} : \nScenario : {expectedResults.Value.Message}");

                return new TestCase(idx)
                {
                    Bytecode = caseBytecode,
                    Result = resultMessage
                };

            }
            public static TestCase CreateFromScenario(BodyScenario scenario)
            {
                Prepare prepare = Prepare.EvmCode;
                int stackHeight = 0;
                if (!scenario.HasFlag(BodyScenario.WithEmptyCodeSection))
                {
                    if (scenario.HasFlag(BodyScenario.UseDeprecatedOpcode))
                    {
                        prepare = prepare.CALLCODE();
                    }

                    if (scenario.HasFlag(BodyScenario.UseUndefinedOpcode))
                    {
                        byte opcode = 0x00;
                        while (Enum.IsDefined(typeof(Instruction), opcode))
                        {
                            opcode++;
                        }
                        prepare = prepare.Op(opcode);
                    }

                    if (scenario.HasFlag(BodyScenario.EndWithTruncatedPush))
                    {
                        prepare.Op(Instruction.PUSH32)
                            .Data(Enumerable.Range(0, 23).Select(i => (byte)i).ToArray());
                        stackHeight++;
                    }
                    else
                    {
                        prepare.Op(Instruction.PUSH32)
                            .Data(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
                        stackHeight++;
                    }
                    prepare.STOP();
                }

                byte[] bytecode = prepare.Done;
                byte[] databytes = scenario.HasFlag(BodyScenario.WithDataSection) ? new byte[] { 0xde, 0xad, 0xbe, 0xef } : Array.Empty<byte>();
                var resultCase = new ScenarioCase(new[] { new FunctionCase(0, 0, stackHeight, bytecode) }, databytes);
                bytecode = resultCase.Bytecode;
                bool validCase = scenario is BodyScenario.None || scenario is BodyScenario.WithDataSection;
                return new TestCase(scenario.ToInt32())
                {
                    Bytecode = bytecode,
                    Result = (validCase ? StatusCode.Success : StatusCode.Failure, $"EOF1 validation : \nbytecode {bytecode.ToHexString(true)} : \nScenario : {scenario.FastToString()}"),
                };
            }
            public byte[] Bytecode => GenerateFormatScenarios(FormatScenario.None).Bytecode;

            public TestCase GenerateFormatScenarios(FormatScenario scenarios)
            {
                int eofPrefixSize =
                    2 + 1 /*EF00 + version */;
                int typeSectionHeaderSize =
                    1 + 2 /*Type section kind+header*/;
                int codeSectionHeaderSize =
                    1 + 2 + 2 * Functions.Length /*Code section kind+count+header*/;
                int dataSectionHeaderSize =
                    1 + 2 /*Type section kind+header*/;
                int terminatorSectionSize =
                    1 /*terminator*/;

                int bytecodeHeaderLength =
                    eofPrefixSize +
                    typeSectionHeaderSize +
                    codeSectionHeaderSize +
                    dataSectionHeaderSize +
                    terminatorSectionSize;

                int bytecodeBodyLength =
                    4 * Functions.Length /*typesection */ +
                    Functions.Sum(s => s.Body.Length) /*codesection*/ +
                    Data.Length /*terminator*/;

                int garbage = scenarios.HasFlag(FormatScenario.TrailingBytes) ? 1 : 0;

                var bytes = new byte[bytecodeHeaderLength + bytecodeBodyLength + garbage];

                (byte[] bytecode, int headerSize) InjectHeader(byte[] container)
                {
                    (byte[], int) InjectMagicPrefix((byte[], int) containerState)
                    {
                        (byte[] container, int i) = (containerState);

                        if (scenarios.HasFlag(FormatScenario.InvalidMagic))
                        {
                            container[i++] = 0xEF;
                            container[i++] = 0xFF; // incorrect magic
                        }
                        else
                        {
                            container[i++] = 0xEF;
                            container[i++] = 0x00; // correct magic
                        }

                        // set version
                        container[i++] = scenarios.HasFlag(FormatScenario.InvalidVersion) ? (byte)0x00 : (byte)0x01;
                        return (container, i);
                    }

                    (byte[], int) InjectHeaderSuffix((byte[], int) containerState)
                    {
                        (byte[] container, int i) = (containerState);

                        if (scenarios.HasFlag(FormatScenario.IncorrectSectionKind))
                        {
                            container[i++] = 0x09;
                        }
                        else if (scenarios.HasFlag(FormatScenario.MissingTerminator))
                        {
                            return containerState;
                        }
                        else
                        {
                            container[i++] = 0x00;
                        }

                        return (container, i);
                    }

                    (byte[], int) InjectTypeSectionHeader((byte[], int) containerState)
                    {
                        (byte[] container, int i) = (containerState);
                        byte[] typeSectionSize = // set typesection size
                            scenarios.HasFlag(FormatScenario.IncorrectTypeSectionHeader)
                                ? 0.ToByteArray()
                                : (Functions.Length * 4).ToByteArray();

                        container[i++] = 0x01;
                        if (!scenarios.HasFlag(FormatScenario.IncompleteTypeSectionHeader))
                        {
                            container[i++] = typeSectionSize[^2];
                            container[i++] = typeSectionSize[^1];
                        }

                        return (container, i);
                    }

                    (byte[], int) InjectCodeSectionHeader((byte[], int) containerState)
                    {
                        (byte[] container, int i) = (containerState);
                        byte[] functionsCount = scenarios.HasFlag(FormatScenario.IncorrectCodeSectionsHeader)
                            ? 0.ToByteArray()
                            : Functions.Length.ToByteArray();

                        container[i++] = 0x02;
                        if (!scenarios.HasFlag(FormatScenario.IncompleteCodeSectionsHeader))
                        {
                            container[i++] = functionsCount[^2];
                            container[i++] = functionsCount[^1];
                            // set code sections sizes
                            for (int j = 0; j < Functions.Length; j++)
                            {
                                var codeSectionCount = Functions[j].Body.Length.ToByteArray();
                                container[i++] = codeSectionCount[^2];
                                container[i++] = codeSectionCount[^1];
                            }
                        }

                        return (container, i);
                    }

                    (byte[], int) InjectDataSectionHeader((byte[], int) containerState)
                    {
                        (byte[] container, int i) = (containerState);
                        byte[] dataSectionCount = scenarios.HasFlag(FormatScenario.IncorrectDataSectionHeader)
                            ? (Data.Length - 1).ToByteArray()
                            : Data.Length.ToByteArray();

                        container[i++] = 0x03;
                        if (!scenarios.HasFlag(FormatScenario.IncompleteDataSectionHeader))
                        {
                            container[i++] = dataSectionCount[^2];
                            container[i++] = dataSectionCount[^1];
                        }

                        return (container, i);
                    }

                    List<Func<(byte[], int), (byte[], int)>> headerInjectionSequence = new() {
                        InjectTypeSectionHeader,
                        InjectCodeSectionHeader,
                        InjectDataSectionHeader
                    };

                    if (scenarios.HasFlag(FormatScenario.MisplaceSectionHeaders))
                    {
                        headerInjectionSequence.Reverse();
                    }
                    else
                    {
                        // Note(Ayman) :  Add Multiple section cases, handle dynamic bytecode size
                        if (scenarios.HasFlag(FormatScenario.OmitTypeSectionHeader))
                        {
                            headerInjectionSequence.RemoveAt(0);
                        }
                        else if (scenarios.HasFlag(FormatScenario.MultipleTypeSectionHeaders))
                        {
                            headerInjectionSequence.Insert(0, InjectTypeSectionHeader);
                            Array.Resize(ref container, container.Length + typeSectionHeaderSize);
                        }

                        if (scenarios.HasFlag(FormatScenario.OmitCodeSectionsHeader))
                        {
                            headerInjectionSequence.RemoveAt(1);
                        }
                        else if (scenarios.HasFlag(FormatScenario.MultipleCodeSectionsHeaders))
                        {
                            headerInjectionSequence.Insert(headerInjectionSequence.Count - 1, InjectCodeSectionHeader);
                            Array.Resize(ref container, container.Length + codeSectionHeaderSize);
                        }

                        if (scenarios.HasFlag(FormatScenario.OmitDataSectionHeader))
                        {
                            headerInjectionSequence.RemoveAt(headerInjectionSequence.Count - 1);
                        }
                        else if (scenarios.HasFlag(FormatScenario.MultipleDataSectionHeaders))
                        {
                            headerInjectionSequence.Insert(headerInjectionSequence.Count - 1, InjectDataSectionHeader);
                            Array.Resize(ref container, container.Length + dataSectionHeaderSize);
                        }
                    }

                    (byte[], int) result = InjectMagicPrefix((container, 0));
                    foreach (var transformation in headerInjectionSequence)
                    {
                        result = transformation(result);
                    }

                    return InjectHeaderSuffix(result);
                }

                byte[] InjectBody(byte[] container, int headerSize)
                {
                    (byte[], int) InjectTypeSection((byte[], int) containerState)
                    {
                        var (container, i) = (containerState);
                        foreach (var functionDef in Functions)
                        {
                            var MaxStackHeightBytes = functionDef.MaxStack.ToByteArray();
                            container[i++] = (byte)functionDef.InputCount;
                            container[i++] = (byte)functionDef.OutputCount;
                            container[i++] = MaxStackHeightBytes[^2];
                            container[i++] = MaxStackHeightBytes[^1];
                        }

                        return (container, i);
                    }

                    (byte[], int) InjectCodeSections((byte[], int) containerState)
                    {
                        var (destinationArray, i) = (containerState);
                        foreach (var section in Functions)
                        {
                            Array.Copy(section.Body, 0, destinationArray, i, section.Body.Length);
                            i += section.Body.Length;
                        }

                        return (destinationArray, i);
                    }

                    (byte[], int) InjectDataSection((byte[], int) containerState)
                    {
                        var (destinationArray, i) = (containerState);
                        Array.Copy(Data, 0, destinationArray, i, Data.Length);
                        i += Data.Length;
                        return (destinationArray, i);
                    }

                    (byte[], int) result = InjectTypeSection((container, headerSize));
                    if (!scenarios.HasFlag(FormatScenario.IncompleteBody))
                    {
                        result = InjectCodeSections(result);
                    }
                    (byte[] bytecode, int end) = InjectDataSection(result);
                    return bytecode[..end];
                }

                (byte[] bytecode, int idx) = InjectHeader(bytes);
                bytecode = InjectBody(bytecode, idx);
                if (scenarios.HasFlag(FormatScenario.TrailingBytes))
                {
                    Array.Resize(ref bytecode, bytecode.Length + 1);
                }

                return new TestCase(scenarios.ToInt32())
                {
                    Bytecode = bytecode,
                    Result = (scenarios is FormatScenario.None ? StatusCode.Success : StatusCode.Failure, $"EOF1 deploy : \nbytecode {bytecode.ToHexString(true)} : \nScenario : {scenarios.FastToString()}"),
                };
            }
            public TestCase GenerateDeploymentScenarios(DeploymentScenario scenarios, DeploymentContext ctx)
            {
                byte[] salt = { 4, 5, 6 };

                byte[] CorruptBytecode(bool isEof, byte[] arg)
                {
                    if (isEof)
                    {
                        // corrupt EOF : wrong magic
                        arg[1] = 0xFF;
                        return arg;
                    }
                    else
                    {
                        // corrupt Legacy : starts with 0xef
                        var result = new List<byte>();
                        result.Add(0xEF);
                        result.AddRange(arg);
                        return result.ToArray();
                    }
                }

                byte[] EmitBytecode()
                {
                    byte[] deployed;

                    // if initcode should be EOF
                    var hasEofCode = scenarios.HasFlag(DeploymentScenario.EofDeployedCode);
                    if (hasEofCode)
                    {
                        deployed = Bytecode;
                    }
                    // if initcode should be Legacy
                    else
                    {
                        deployed = Functions[0].Body;
                    }

                    // if initcode should be corrupt
                    if (scenarios.HasFlag(DeploymentScenario.CorruptDeployedCode))
                    {
                        deployed = CorruptBytecode(hasEofCode, deployed);
                    }

                    byte[] initcode = Prepare.EvmCode
                        .ForInitOf(deployed)
                        .Done;

                    bool hasEofInitCode = scenarios.HasFlag(DeploymentScenario.EofInitCode);
                    if (hasEofInitCode)
                    {
                        initcode = new ScenarioCase(
                            new[] { new FunctionCase(0, 0, 2, initcode) },
                            Array.Empty<byte>()
                        ).Bytecode;
                    }

                    // if initcode should be corrupt
                    if (scenarios.HasFlag(DeploymentScenario.CorruptInitCode))
                    {
                        initcode = CorruptBytecode(hasEofInitCode, initcode);
                    }

                    // wrap initcode in container
                    byte[] result = Array.Empty<byte>();
                    switch (ctx)
                    {
                        case DeploymentContext.UseCreate or DeploymentContext.UseCreate2:
                            {
                                var prep = Prepare.EvmCode;

                                Address contractAddress = ctx is DeploymentContext.UseCreate
                                    ? ContractAddress.From(Sender, 0)
                                    : ContractAddress.From(Sender, salt, initcode);

                                byte[] returnCode =
                                    ctx is DeploymentContext.UseCreate
                                        ? Prepare.EvmCode.Create(initcode, UInt256.Zero).STOP().Done
                                        : Prepare.EvmCode.Create2(initcode, salt, UInt256.Zero).STOP().Done;
                                result = prep
                                    .Data(returnCode)
                                    .StaticCall(contractAddress, 99 * 1000)
                                    .STOP()
                                    .Done;
                                break;
                            }
                        case DeploymentContext.UseCreateTx:
                            result = initcode;
                            break;
                    }
                    return result;
                }

                bool isValid = ctx is DeploymentContext.UseCreateTx
                    ? scenarios.HasFlag(DeploymentScenario.EofInitCode)
                        ? scenarios.HasFlag(DeploymentScenario.CorruptInitCode) ?
                            true : scenarios.HasFlag(DeploymentScenario.EofDeployedCode) && !scenarios.HasFlag(DeploymentScenario.CorruptDeployedCode)
                        : scenarios.HasFlag(DeploymentScenario.CorruptInitCode) ?
                            true : !scenarios.HasFlag(DeploymentScenario.CorruptDeployedCode)
                    : scenarios.HasFlag(DeploymentScenario.EofInitCode)
                        ? scenarios.HasFlag(DeploymentScenario.CorruptInitCode) ?
                            true : scenarios.HasFlag(DeploymentScenario.EofDeployedCode) && !scenarios.HasFlag(DeploymentScenario.CorruptDeployedCode)
                        : scenarios.HasFlag(DeploymentScenario.CorruptInitCode) ?
                            true : !scenarios.HasFlag(DeploymentScenario.CorruptDeployedCode);


                byte[] bytecode = EmitBytecode();
                string message = $"EOF1 execution : \nbytecode {bytecode.ToHexString(true)} : \nScenario : {EnumToDetailedString(scenarios)}, \nContext : {ctx.FastToString()}";
                return new TestCase(scenarios.ToInt32())
                {
                    UseCreateTx = ctx.HasFlag(DeploymentContext.UseCreateTx),
                    Bytecode = bytecode,
                    Ctx = ctx,
                    Result = (isValid ? StatusCode.Success : StatusCode.Failure, message),
                };
            }
        }

        static string EnumToDetailedString<TEnum>(TEnum value) where TEnum : struct, Enum
        {
            List<String> sb = new();
            var fieldsInTEnum = FastEnum.GetValues<TEnum>();
            foreach (var field in fieldsInTEnum)
            {
                if (value.HasFlag(field))
                {
                    sb.Add(field.ToString());
                }
            }
            return String.Join(", ", sb);
        }

        public static IEnumerable<IReleaseSpec> Specs
        {
            get
            {
                yield return GrayGlacier.Instance;
                yield return Cancun.Instance;
            }
        }

    }
}
