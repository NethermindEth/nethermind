// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.PureWordOpcodeExtractor;

internal static class PureWordOpcodeProfile
{
    internal const int SchemaVersion = 2;
    internal const string ExtractorVersion = "2.1.0";
    internal const string IrFileName = "PureWordOpcodeKernel.ir.json";
    internal const string ManifestFileName = "PureWordOpcodeKernel.source-manifest.json";
    internal const string DefaultLeanRelativePath = "tools/Evm/Lean/Eip803x/Generated/PureWordOpcodeKernel.lean";
    internal const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";
    private const string Root = "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode";
    private const string ExpectedBuildTargetsSha256 = "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly DispatchTableBinding[] ExpectedDispatchTables =
    [
        new("NoTrace", "OffFlag", "OffFlag"),
        new("NoTraceCancelable", "OffFlag", "OnFlag"),
        new("Traced", "OnFlag", "OffFlag"),
        new("TracedCancelable", "OnFlag", "OnFlag"),
    ];

    private static readonly string[] SourceRelativePaths =
    [
        "src/Nethermind/Nethermind.Core/GasCostOf.cs",
        "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs",
        "src/Nethermind/Nethermind.Core/TypeFlags.cs",
        "src/Nethermind/Nethermind.Evm/EvmStack.cs",
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
        "src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs",
        "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs",
        "src/Nethermind/Nethermind.Evm/Instruction.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Bitwise.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math1Param.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math2Param.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math3Param.cs",
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Shifts.cs",
        "src/Nethermind/Nethermind.Evm/SpecFlags.std.cs",
        "src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs",
        "src/Nethermind/Nethermind.Evm/VirtualMachine.cs",
        "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs",
        "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs",
        "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs",
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs",
        "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs",
        "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs",
        "src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs",
        "src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs",
        "src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs",
        "src/Nethermind/Nethermind.Specs/Forks/08_ConstantinopleFix.cs",
        "src/Nethermind/Nethermind.Specs/Forks/09_Istanbul.cs",
        "src/Nethermind/Nethermind.Specs/Forks/10_MuirGlacier.cs",
        "src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs",
        "src/Nethermind/Nethermind.Specs/Forks/12_London.cs",
        "src/Nethermind/Nethermind.Specs/Forks/13_ArrowGlacier.cs",
        "src/Nethermind/Nethermind.Specs/Forks/14_GrayGlacier.cs",
        "src/Nethermind/Nethermind.Specs/Forks/15_Paris.cs",
        "src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs",
        "src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs",
        "src/Nethermind/Nethermind.Specs/Forks/18_Prague.cs",
        "src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs",
        "src/Nethermind/Nethermind.Specs/Forks/20_BPO1.cs",
        "src/Nethermind/Nethermind.Specs/Forks/21_BPO2.cs",
        "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
    ];

    private static readonly string[] AllSourceRelativePaths = [.. SourceRelativePaths, BuildTargetsPath];

    // Complete, non-layout Roslyn token/trivia fingerprints for the pinned source closure.
    // Updating generated artifacts cannot update this admission boundary.
    private static readonly string[] ExpectedSyntaxFingerprints =
    [
        "7d27b9dc5a0e6147728e223f70c622a372f6bed02807e92d152d0d2ab17e5d77",
        "c40231659926a981f2581169228c2e2f52dcdb004d62275d62d99bd13a91075b",
        "9abf25883dbfd2e15817c9d40dbeda3c4986b703c9ba51387aa1fd7ff9d40d22",
        "6ed1fd434ad64ddabe71357c556f550378b2951c128f52e5fe6c24c6ac0c0bc5",
        "3290e1f07ba7c79c92318f05fc038a8f619ae13073417c3f7534868c8e7b67af",
        "7c1e8199e15c9340c037ffb9f7872b08513870343c2beaf8fb4d04ed1b9ff371",
        "365d32ce62f66ca940e7f942f01ba441c44a3a9c2bd62f0f10a813a381a71169",
        "bf11507bb4bed851c7e8de33fcca9ee0e6e4a19653c6ed063de15d088b49deaf",
        "6268b4443d03ce3d03955a878401b790213aa3e74713eb600fc2dbd8f4da1a95",
        "517759ef68085132ab64836be3659bd6ed7fb393247392e95140dac7f64bec95",
        "5ef887345d4574dee1633c840ffe98e7dd782f83b00d93cab0fd5879cf612e00",
        "d40b27a000493d8c3b955e546045c71029928efc9ed6fb418d387197713f7151",
        "0bd51df56f52938830f77dc92d2510b14bede423fb543f71eb8866fc0869f96b",
        "f16ea40ad58b85d1c1f6395dfa5f9a9c96ce89a4f490d99623e174ed178410a6",
        "41cd7d362579ae46423a8b2d205a7bf801aac50041e500df0d2732c9378b7ce3",
        "748c9db91d57507c3cc6f6f0d2f8c219bc477f4f902ce6fc7b5ae6dfab11cad6",
        "d7eacfe0ff664afef87eef2676fc9616803c1ab5f9d7607295abbf564ce8fa7e",
        "e376ddfceff6fd782f28ad7ea85c7dc1b16fc2951774a224a754845f5ef9ea7a",
        "faad4662e424ee26a4d8edfa55e96dd14df031fee10dfeef30086ad38e04f38e",
        "139cb5eb00216901101e2ad5fa75c6c23909a80f61a66b798a5203e6ff24cad9",
        "02383832e5f7f5fbde31a0089a48aba8e52cc90c94fb7a1e0be9f5d170e86cf8",
        "db7066789869bcb03a9054ce7a120b810569c7708910d383cf3f7d3f412569de",
        "00aadcd6818be7c56f5fb359955cc415d963b49ca4a8fc90af0111e1002982e5",
        "69ac53dfafde2635f8f5db8422d43fac8c12406502354766cf5af2867fc6833d",
        "b5c1a2fb30a97a55f14c37b719d00d64d141bb4ed15b63d2247b595b1b4a934b",
        "dd517bc5b6f4cceaa6cf7bf9cc140bfe3782eb5750f410ea2b7222e988a11017",
        "556d242d27be90d635876330b8446ecb1d05a3cf93c77e5ddc4249ac1c1bf838",
        "3bdbe49c78e94e5bd93f7dfb6bf473e78f268cb4e553b8f99aac4c5e5640ea86",
        "5836caec1782021154e344010919ae4bb38ef3d0af14aecf302bad5a64016e15",
        "5e63f561bbdf91548c1424a3d8a586152dfe7b0cf9ea596d884c2693577a21d6",
        "3bca338ddca2cf2b59fbc8ee0b3db571841545e7e470bfa0bcec861e68f2ea78",
        "5d37a5da0a406f49b269fecbd49991ec3a9fdc4d0cea9596b667c270e4c38132",
        "8c1645477de58b9e5028875df169fdd29de19d820d0626fdf2534dba83edf961",
        "08948d819b69e603f7cf2876c619fc368f6dd8fd40665ee9dd88fda87593433b",
        "6e7f0191d93c4a4e2d9765fb443a8a8f8cdfc2cafb8a84e86d6978f40086577e",
        "391ad36bb924d6cf30114290a21d6a0db04120d83e5354b61d443ed43d391f85",
        "3aae4913b9e3b05d6d11efa60b7a048fc7f4a66e2d9e331c79be9c23ddad38ee",
        "dea543b0c4c47000bc6995464a503d39f8c72da727181e98767c930a11a5bab9",
        "ecc05ae12b0d7fafd37dbe6477d954519a248a577823be0909939737e896f2df",
        "6a3a34e72a5af6bfbb9f0bfca33df3e40d33a8cba0011a3c4238be730d1c571b",
    ];

    private static readonly DescriptorSeed[] Seeds =
    [
        new("add", "ADD", "Math2Opcode<EvmInstructions.OpAdd,TTracingInst>", "math2", "OpAdd", "add", "veryLow", 2, "unconditional"),
        new("mul", "MUL", "Math2Opcode<EvmInstructions.OpMul,TTracingInst>", "math2", "OpMul", "mul", "low", 2, "unconditional"),
        new("sub", "SUB", "Math2Opcode<EvmInstructions.OpSub,TTracingInst>", "math2", "OpSub", "sub", "veryLow", 2, "unconditional"),
        new("div", "DIV", "Math2Opcode<EvmInstructions.OpDiv,TTracingInst>", "math2", "OpDiv", "udiv", "low", 2, "unconditional"),
        new("sdiv", "SDIV", "Math2Opcode<EvmInstructions.OpSDiv,TTracingInst>", "math2", "OpSDiv", "sdiv", "low", 2, "unconditional"),
        new("mod", "MOD", "Math2Opcode<EvmInstructions.OpMod,TTracingInst>", "math2", "OpMod", "umod", "low", 2, "unconditional"),
        new("smod", "SMOD", "Math2Opcode<EvmInstructions.OpSMod,TTracingInst>", "math2", "OpSMod", "smod", "low", 2, "unconditional"),
        new("addmod", "ADDMOD", "Math3Opcode<EvmInstructions.OpAddMod,TTracingInst>", "math3", "OpAddMod", "addmod", "mid", 3, "unconditional"),
        new("mulmod", "MULMOD", "Math3Opcode<EvmInstructions.OpMulMod,TTracingInst>", "math3", "OpMulMod", "mulmod", "mid", 3, "unconditional"),
        new("exp", "EXP", "ExpOpcode<TTracingInst,OnFlag>", "exp", "InstructionExp", "exp", "exp", 2, "Amsterdam inherits EIP-160"),
        new("signextend", "SIGNEXTEND", "SignExtendOpcode<TTracingInst>", "signextend", "InstructionSignExtend", "signextend", "low", 2, "unconditional"),
        new("lt", "LT", "Math2Opcode<EvmInstructions.OpLt,TTracingInst>", "math2", "OpLt", "unsignedLt", "veryLow", 2, "unconditional"),
        new("gt", "GT", "Math2Opcode<EvmInstructions.OpGt,TTracingInst>", "math2", "OpGt", "unsignedGt", "veryLow", 2, "unconditional"),
        new("slt", "SLT", "Math2Opcode<EvmInstructions.OpSLt,TTracingInst>", "math2", "OpSLt", "signedLt", "veryLow", 2, "unconditional"),
        new("sgt", "SGT", "Math2Opcode<EvmInstructions.OpSGt,TTracingInst>", "math2", "OpSGt", "signedGt", "veryLow", 2, "unconditional"),
        new("eq", "EQ", "BitwiseOpcode<EvmInstructions.OpBitwiseEq,TTracingInst>", "bitwise", "OpBitwiseEq", "equal", "veryLow", 2, "unconditional"),
        new("iszero", "ISZERO", "Math1Opcode<EvmInstructions.OpIsZero,TTracingInst>", "math1", "OpIsZero", "isZero", "veryLow", 1, "unconditional"),
        new("and", "AND", "BitwiseOpcode<EvmInstructions.OpBitwiseAnd,TTracingInst>", "bitwise", "OpBitwiseAnd", "bitwiseAnd", "veryLow", 2, "unconditional"),
        new("or", "OR", "BitwiseOpcode<EvmInstructions.OpBitwiseOr,TTracingInst>", "bitwise", "OpBitwiseOr", "bitwiseOr", "veryLow", 2, "unconditional"),
        new("xor", "XOR", "BitwiseOpcode<EvmInstructions.OpBitwiseXor,TTracingInst>", "bitwise", "OpBitwiseXor", "bitwiseXor", "veryLow", 2, "unconditional"),
        new("not", "NOT", "Math1Opcode<EvmInstructions.OpNot,TTracingInst>", "math1", "OpNot", "bitwiseNot", "veryLow", 1, "unconditional"),
        new("byte", "BYTE", "ByteOpcode<TTracingInst>", "byte", "InstructionByte", "byte", "veryLow", 2, "unconditional"),
        new("shl", "SHL", "ShiftOpcode<EvmInstructions.OpShl,TTracingInst>", "shift", "OpShl", "shl", "veryLow", 2, "Amsterdam inherits EIP-145"),
        new("shr", "SHR", "ShiftOpcode<EvmInstructions.OpShr,TTracingInst>", "shift", "OpShr", "shr", "veryLow", 2, "Amsterdam inherits EIP-145"),
        new("sar", "SAR", "SarOpcode<TTracingInst>", "sar", "InstructionSar", "sar", "veryLow", 2, "Amsterdam inherits EIP-145"),
        new("clz", "CLZ", "CountLeadingZerosOpcode<TTracingInst>", "clz", "CountLeadingZerosCore", "clz", "low", 1, "Amsterdam inherits EIP-7939"),
    ];

    private static readonly int[] ExpectedOpcodeBytes =
    [
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a,
        0x1b, 0x1c, 0x1d, 0x1e,
    ];

    private static readonly string[] ExpectedOpenExtractionObligations =
    [
        "Roslyn syntax profiling does not prove C# or CLR semantics.",
        "UInt256, Int256, byte-order, unsafe stack-slot, and SIMD/scalar implementation equivalence remain proof obligations.",
        "JIT specialization, function-pointer dispatch, opcode-count/tracing/cancellation side effects, and tail-call behavior remain proof obligations.",
    ];

    public static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string canonicalRoot = Path.GetFullPath(repoRoot);
        Dictionary<string, SourceFile> sources = LoadSources(canonicalRoot);
        RawSourceFile buildTargets = LoadRawSource(canonicalRoot, BuildTargetsPath);
        ValidateStandardBuildSelection(buildTargets);
        IReadOnlyList<DispatchTableBinding> dispatchTables = ValidateAndExtractLiveDispatchTables(sources);
        IReadOnlyList<OpcodeDescriptor> opcodes = ValidateAndBuildOpcodes(sources);
        IReadOnlyList<OpcodeSpecialization> specializations = BuildSpecializations(opcodes, dispatchTables);
        ValidatePinnedSyntaxFingerprints(sources);

        IrDocument ir = new(
            SchemaVersion,
            ExtractorVersion,
            Root,
            "Nethermind.Evm.GasPolicy.EthereumGasPolicy",
            "Nethermind.Specs.Forks.Amsterdam",
            50,
            opcodes,
            specializations,
            ExpectedOpenExtractionObligations);

        Directory.CreateDirectory(outputDirectory);
        string irPath = Path.Combine(outputDirectory, IrFileName);
        byte[] irBytes = Serialize(ir);
        File.WriteAllBytes(irPath, irBytes);

        IrDocument roundTripped = DeserializeIr(irBytes);

        SourceManifest manifest = BuildManifest(sources, buildTargets);
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        byte[] manifestBytes = Serialize(manifest);
        File.WriteAllBytes(manifestPath, manifestBytes);

        string leanPath = leanOutputPath ?? Path.Combine(canonicalRoot, DefaultLeanRelativePath);
        string? leanDirectory = Path.GetDirectoryName(leanPath);
        if (leanDirectory is not null)
            Directory.CreateDirectory(leanDirectory);
        byte[] leanBytes = PureWordOpcodeLeanEmitter.Emit(
            roundTripped,
            typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
            manifest.CombinedSha256,
            Hash(irBytes));
        File.WriteAllBytes(leanPath, leanBytes);

        return new ExtractionResult(irPath, manifestPath, leanPath, opcodes.Count, specializations.Count);
    }

    internal static IReadOnlyList<string> SourcePaths => AllSourceRelativePaths;

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        if (bytes is null)
            throw new ExtractionException("The serialized pure-word opcode IR input was null.");
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized pure-word opcode IR was empty.");
            ValidateIr(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized pure-word opcode IR is malformed or contains unknown members: {exception.Message}");
        }
    }

    internal static void ValidateIr(IrDocument document)
    {
        if (document is null || document.Opcodes is null || document.Specializations is null ||
            document.OpenExtractionObligations is null ||
            document.Opcodes.Any(static opcode => opcode is null || opcode.Name is null ||
                opcode.Instruction is null || opcode.BodyType is null || opcode.InstructionFamily is null ||
                opcode.OperationType is null || opcode.WordSemantics is null || opcode.FixedGasFamily is null ||
                opcode.ActivationEvidence is null) ||
            document.Specializations.Any(static specialization => specialization is null ||
                specialization.Opcode is null || specialization.DispatchTable is null ||
                specialization.TracingFlag is null || specialization.CancelableFlag is null ||
                specialization.ContinuableFlag is null || specialization.ClosedRoot is null) ||
            document.OpenExtractionObligations.Any(static obligation => obligation is null))
            throw new ExtractionException("The round-tripped pure-word opcode IR contains null collections, entries, or fields.");

        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.Root != Root || document.GasPolicy != "Nethermind.Evm.GasPolicy.EthereumGasPolicy" ||
            document.Fork != "Nethermind.Specs.Forks.Amsterdam" || document.ExpByteGas != 50)
            throw new ExtractionException("The serialized pure-word opcode IR header, root, fork, or gas constants changed.");

        OpcodeDescriptor[] expectedOpcodes = Seeds.Select((seed, index) => new OpcodeDescriptor(
            seed.Name, seed.Instruction, ExpectedOpcodeBytes[index], seed.BodyType, seed.InstructionFamily,
            seed.OperationType, seed.WordSemantics, seed.FixedGasFamily, ExpectedFixedGas(seed.FixedGasFamily),
            seed.StackInputs, 0, seed.ActivationEvidence)).ToArray();
        if (!document.Opcodes.SequenceEqual(expectedOpcodes))
            throw new ExtractionException("The serialized pure-word opcode descriptors changed.");

        ValidateSpecializations(document.Opcodes, document.Specializations);
        OpcodeSpecialization[] expectedSpecializations = expectedOpcodes
            .SelectMany(static opcode => ExpectedDispatchTables.Select(table => ExpectedSpecialization(opcode, table)))
            .ToArray();
        if (!document.Specializations.SequenceEqual(expectedSpecializations))
            throw new ExtractionException("The serialized pure-word specialization order or roots changed.");
        if (!document.OpenExtractionObligations.SequenceEqual(ExpectedOpenExtractionObligations, StringComparer.Ordinal))
            throw new ExtractionException("The serialized pure-word extraction obligations changed.");
    }

    private static ulong ExpectedFixedGas(string gasFamily) => gasFamily switch
    {
        "veryLow" => 3,
        "low" => 5,
        "mid" => 8,
        "exp" => 10,
        _ => throw new ExtractionException($"Unknown gas family '{gasFamily}'."),
    };

    private static IReadOnlyList<OpcodeDescriptor> ValidateAndBuildOpcodes(Dictionary<string, SourceFile> sources)
    {
        ValidateFlagsAndFork(sources);
        ValidateProductionRoot(sources);
        ValidateStackAndGas(sources);
        ValidateInstructionSemantics(sources);

        SourceFile instructionSource = Get(sources, "src/Nethermind/Nethermind.Evm/Instruction.cs");
        Dictionary<string, int> instructionValues = ReadInstructionValues(instructionSource.Syntax);
        Dictionary<string, ulong> gasCosts = ReadGasCosts(Get(sources, "src/Nethermind/Nethermind.Core/GasCostOf.cs").Syntax);
        ValidateGasCosts(gasCosts);

        SourceFile handlers = Get(sources, "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs");
        IReadOnlyDictionary<string, string> dispatchBodies = ExtractFinalDispatchBodies(handlers.Syntax);
        ValidateWrappers(handlers.Syntax);

        List<OpcodeDescriptor> result = new(Seeds.Length);
        HashSet<int> usedOpcodeBytes = [];
        for (int index = 0; index < Seeds.Length; index++)
        {
            DescriptorSeed seed = Seeds[index];
            if (!instructionValues.TryGetValue(seed.Instruction, out int opcodeByte))
                throw new ExtractionException($"Instruction.{seed.Instruction} is missing from the production enum.");
            if (opcodeByte is < byte.MinValue or > byte.MaxValue)
                throw new ExtractionException($"Instruction.{seed.Instruction} byte {opcodeByte} is outside the EVM byte range.");
            if (!usedOpcodeBytes.Add(opcodeByte))
                throw new ExtractionException($"Instruction.{seed.Instruction} duplicates pure-word opcode byte {opcodeByte}.");
            if (!dispatchBodies.TryGetValue(seed.Instruction, out string? bodyType))
                throw new ExtractionException($"Instruction.{seed.Instruction} has no admitted final live dispatch assignment.");
            if (!string.Equals(bodyType, seed.BodyType, StringComparison.Ordinal))
                throw new ExtractionException(
                    $"Production dispatch body for {seed.Instruction} changed from '{seed.BodyType}' to '{bodyType}'.");

            ulong fixedGas = seed.FixedGasFamily switch
            {
                "veryLow" => gasCosts["VeryLow"],
                "low" => gasCosts["Low"],
                "mid" => gasCosts["Mid"],
                "exp" => gasCosts["Exp"],
                _ => throw new ExtractionException($"Unknown gas family '{seed.FixedGasFamily}'."),
            };
            result.Add(new OpcodeDescriptor(
                seed.Name,
                seed.Instruction,
                opcodeByte,
                bodyType,
                seed.InstructionFamily,
                seed.OperationType,
                seed.WordSemantics,
                seed.FixedGasFamily,
                fixedGas,
                seed.StackInputs,
                0,
                seed.ActivationEvidence));
        }

        if (result.Count != 26)
            throw new ExtractionException($"Expected 26 Amsterdam pure-word opcodes but found {result.Count}.");
        return result;
    }

    private static void ValidateFlagsAndFork(Dictionary<string, SourceFile> sources)
    {
        string flags = Get(sources, "src/Nethermind/Nethermind.Core/TypeFlags.cs").CompactText;
        Require(flags, "publicstructOffFlag:IFlag{publicstaticboolIsActive=>false;}", "OffFlag must remain statically false");
        Require(flags, "publicstructOnFlag:IFlag{publicstaticboolIsActive=>true;}", "OnFlag must remain statically true");

        string dispatchFlags = Get(sources, "src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs").CompactText;
        Require(dispatchFlags, "publicconstboolConstTracing=true;", "standard builds must expose tracing");
        Require(dispatchFlags, "publicstaticboolTracing(boolisTracing)=>isTracing;", "standard tracing flag must be an identity");
        Require(dispatchFlags, "publicstaticboolCancelable(booltracerIsCancelable)=>tracerIsCancelable;", "standard cancellation flag must be an identity");

        string specFlags = Get(sources, "src/Nethermind/Nethermind.Evm/SpecFlags.std.cs").CompactText;
        Require(specFlags, "publicstaticboolEip160(IReleaseSpecspec)=>spec.UseExpDDosProtection;", "EIP-160 routing must read the execution spec");
        string extensions = Get(sources, "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs").CompactText;
        Require(extensions, "publicboolUseExpDDosProtection=>spec.IsEip160Enabled;", "EIP-160 extension must preserve its flag");
        Require(extensions, "publicboolShiftOpcodesEnabled=>spec.IsEip145Enabled;", "EIP-145 extension must preserve its flag");
        Require(extensions, "publicboolCLZEnabled=>spec.IsEip7939Enabled;", "EIP-7939 extension must preserve its flag");

        Require(Get(sources, "src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs").CompactText,
            "spec.IsEip160Enabled=true;", "Spurious Dragon must enable EIP-160");
        Require(Get(sources, "src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs").CompactText,
            "spec.IsEip145Enabled=true;", "Constantinople must enable EIP-145");
        Require(Get(sources, "src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs").CompactText,
            "spec.IsEip7939Enabled=true;", "Osaka must enable EIP-7939");

        string namedReleaseSpec = Get(sources, "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs").CompactText;
        Require(namedReleaseSpec,
            "protectedNamedReleaseSpec(NamedReleaseSpec?parent){Parent=parent;ReplayAncestors(this);}",
            "named forks must replay their ancestor chain during construction");
        Require(namedReleaseSpec,
            "privatevoidReplayAncestors(NamedReleaseSpec?fork){if(forkisnull)return;ReplayAncestors(fork.Parent);fork.Apply(this);}",
            "named-fork replay must apply every ancestor from root to leaf");
        Require(namedReleaseSpec,
            "publicstaticNamedReleaseSpecInstance{get;}=newTSelf();",
            "named-fork generic singleton must construct the selected fork type");

        (string Path, string Fork, string Parent)[] forkChain =
        [
            ("src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs", "SpuriousDragon", "TangerineWhistle"),
            ("src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs", "Byzantium", "SpuriousDragon"),
            ("src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs", "Constantinople", "Byzantium"),
            ("src/Nethermind/Nethermind.Specs/Forks/08_ConstantinopleFix.cs", "ConstantinopleFix", "Constantinople"),
            ("src/Nethermind/Nethermind.Specs/Forks/09_Istanbul.cs", "Istanbul", "ConstantinopleFix"),
            ("src/Nethermind/Nethermind.Specs/Forks/10_MuirGlacier.cs", "MuirGlacier", "Istanbul"),
            ("src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs", "Berlin", "MuirGlacier"),
            ("src/Nethermind/Nethermind.Specs/Forks/12_London.cs", "London", "Berlin"),
            ("src/Nethermind/Nethermind.Specs/Forks/13_ArrowGlacier.cs", "ArrowGlacier", "London"),
            ("src/Nethermind/Nethermind.Specs/Forks/14_GrayGlacier.cs", "GrayGlacier", "ArrowGlacier"),
            ("src/Nethermind/Nethermind.Specs/Forks/15_Paris.cs", "Paris", "GrayGlacier"),
            ("src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs", "Shanghai", "Paris"),
            ("src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs", "Cancun", "Shanghai"),
            ("src/Nethermind/Nethermind.Specs/Forks/18_Prague.cs", "Prague", "Cancun"),
            ("src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs", "Osaka", "Prague"),
            ("src/Nethermind/Nethermind.Specs/Forks/20_BPO1.cs", "BPO1", "Osaka"),
            ("src/Nethermind/Nethermind.Specs/Forks/21_BPO2.cs", "BPO2", "BPO1"),
            ("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs", "Amsterdam", "BPO2"),
        ];
        foreach ((string path, string fork, string parent) in forkChain)
        {
            Require(Get(sources, path).CompactText,
                $"publicclass{fork}():NamedReleaseSpec<{fork}>({parent}.Instance)",
                $"{fork} must inherit {parent}");
        }
    }

    private static void ValidateProductionRoot(Dictionary<string, SourceFile> sources)
    {
        string vm = Get(sources, "src/Nethermind/Nethermind.Evm/VirtualMachine.cs").CompactText;
        Require(vm,
            "publicsealedclassEthereumVirtualMachine(",
            "the standard concrete virtual machine must exist");
        Require(vm,
            "):VirtualMachine<EthereumGasPolicy>",
            "the standard concrete virtual machine must close VirtualMachine over EthereumGasPolicy");
        string module = Get(sources, "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs").CompactText;
        Require(module, ".AddScoped<IVirtualMachine,EthereumVirtualMachine>()", "mainnet block processing must register EthereumVirtualMachine");

        SourceFile standardVm = Get(sources, "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs");
        string getOpcodeTable = Compact(FindMethod(standardVm.Syntax, "GetOpcodeTable", 0));
        const string expectedGetOpcodeTable =
            "[MethodImpl(MethodImplOptions.AggressiveInlining)]privateOpcodeTableGetOpcodeTable()=>_opcodeTablesBySpec.GetValue(Spec,static_=>newOpcodeTable());";
        if (!string.Equals(getOpcodeTable, expectedGetOpcodeTable, StringComparison.Ordinal))
            throw new ExtractionException("Standard GetOpcodeTable must return the per-spec initially empty OpcodeTable without a competing table path.");

        string shouldRefreshOpcodes = Compact(FindMethod(standardVm.Syntax, "ShouldRefreshOpcodes", 0));
        const string expectedShouldRefreshOpcodes =
            "privatepartialboolShouldRefreshOpcodes(){if(_txCount>=OpcodeRefreshLimit||Interlocked.Increment(ref_txCount)%OpcodeRefreshInterval!=0)returnfalse;if(_logger.IsDebug)_logger.Debug(\"Refreshing EVM instruction cache\");returntrue;}";
        if (!string.Equals(shouldRefreshOpcodes, expectedShouldRefreshOpcodes, StringComparison.Ordinal))
            throw new ExtractionException("Standard ShouldRefreshOpcodes must only select the admitted non-traced table refresh cadence.");

        CompilationUnitSyntax handlers = Get(sources,
            "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs").Syntax;
        string opcodeHandler = Compact(FindMethod(handlers, "OpcodeHandler", 3));
        Require(opcodeHandler,
            "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OnFlag>",
            "ordinary opcode handlers must select the continuable ExecuteOpcode root");

        SourceFile dispatch = Get(sources, "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs");
        MethodDeclarationSyntax execute = FindMethod(dispatch.Syntax, "ExecuteOpcode", 4);
        string root = Compact(execute);
        Require(root, "whereTOpcode:struct,IOpcodeBody", "ExecuteOpcode must remain closed over IOpcodeBody structs");
        Require(root, "whereTTracingInst:struct,IFlag", "ExecuteOpcode must specialize the tracing flag");
        Require(root, "whereTCancelable:struct,IFlag", "ExecuteOpcode must specialize the cancellation flag");
        Require(root, "whereTContinuable:struct,IFlag", "ExecuteOpcode must specialize the continuation flag");
        RequireOrdered(root,
            "if(TTracingInst.IsActive)",
            "pc++;",
            "opCodeCount++;",
            "if(TOpcode.HasCheckedBody)",
            "if(!TOpcode.TryConsumeGas(refgas))",
            "if(TOpcode.StackInputs!=0&&!stack.EnsureDepth(TOpcode.StackInputs))",
            "EvmExceptionTypecheckedResult=TOpcode.Execute(");
        Require(root, "else{exceptionType=TOpcode.Execute(refstack,refgas,state.Vm,refpc);}",
            "unchecked opcode bodies must execute through the real production body");

        MethodDeclarationSyntax exit = FindMethod(dispatch.Syntax, "ExitCheckedOpcode", 0);
        string exitBody = Compact(exit);
        RequireOrdered(exitBody, "state.OpCodeCount=opCodeCount;", "state.FinalProgramCounter=pc;", "returnexceptionType;");
    }

    private static void ValidateStandardBuildSelection(RawSourceFile buildTargets)
    {
        string text = Encoding.UTF8.GetString(buildTargets.Bytes);
        if (!text.Contains("<ItemGroup Condition=\"'$(EnableZkEvm)' == 'true'\">", StringComparison.Ordinal) ||
            !text.Contains("<Compile Remove=\"**/*.std.cs\" />", StringComparison.Ordinal) ||
            !text.Contains("<None Include=\"**/*.std.cs\" />", StringComparison.Ordinal) ||
            !text.Contains("<ItemGroup Condition=\"'$(EnableZkEvm)' != 'true'\">", StringComparison.Ordinal) ||
            !text.Contains("<Compile Remove=\"**/*.zkevm.cs\" />", StringComparison.Ordinal) ||
            !text.Contains("<None Include=\"**/*.zkevm.cs\" />", StringComparison.Ordinal))
        {
            throw new ExtractionException(
                "Directory.Build.targets must select .std.cs and exclude .zkevm.cs for the standard build.");
        }

        string hash = Hash(buildTargets.Bytes);
        if (!string.Equals(hash, ExpectedBuildTargetsSha256, StringComparison.Ordinal))
            throw new ExtractionException("Pinned raw Directory.Build.targets fingerprint rejected the production build selection.");
    }

    private static IReadOnlyList<DispatchTableBinding> ValidateAndExtractLiveDispatchTables(
        Dictionary<string, SourceFile> sources)
    {
        SourceFile dispatch = Get(sources, "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs");
        TypeDeclarationSyntax opcodeTable = FindType(dispatch.Syntax, "OpcodeTable");
        HashSet<string> tableFields = new(StringComparer.Ordinal);
        foreach (FieldDeclarationSyntax field in opcodeTable.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                tableFields.Add(variable.Identifier.ValueText);
        }
        string[] expectedFields = ["NoTrace", "NoTraceCancelable", "Traced", "TracedCancelable"];
        for (int index = 0; index < expectedFields.Length; index++)
        {
            if (!tableFields.Contains(expectedFields[index]))
                throw new ExtractionException($"Live opcode-table field '{expectedFields[index]}' is missing.");
        }

        string getHandlers = Compact(FindMethod(opcodeTable, "GetHandlers", 2));
        Require(getHandlers,
            "refTTracingInst.IsActive?ref(TCancelable.IsActive?refTracedCancelable:refTraced):ref(TCancelable.IsActive?refNoTraceCancelable:refNoTrace)",
            "four live tracing/cancellation table bindings");
        Require(getHandlers,
            "returntable??=GenerateOpcodeHandlers<TTracingInst,TCancelable>(spec);",
            "live tables must be populated by GenerateOpcodeHandlers");

        string refresh = Compact(FindMethod(opcodeTable, "RefreshNonTraced", 0));
        RequireOrdered(refresh,
            "NoTrace=GenerateOpcodeHandlers<OffFlag,OffFlag>(spec);",
            "NoTraceCancelable=GenerateOpcodeHandlers<OffFlag,OnFlag>(spec);");

        string prepareCancelable = Compact(FindMethod(dispatch.Syntax, "PrepareOpcodes", 1));
        Require(prepareCancelable,
            "if(DispatchFlags.Cancelable(_isCancelableCached))PrepareOpcodes<TTracingInst,OnFlag>();elsePrepareOpcodes<TTracingInst,OffFlag>();",
            "both cancellation table specializations must be reachable");
        string prepareClosed = Compact(FindMethod(dispatch.Syntax, "PrepareOpcodes", 2));
        Require(prepareClosed,
            "_opcodeHandlers=table.GetHandlers<TTracingInst,TCancelable>(spec);",
            "prepared table must become the live opcode-handler table");

        SourceFile vm = Get(sources, "src/Nethermind/Nethermind.Evm/VirtualMachine.cs");
        string genericExecution = Compact(FindMethod(vm.Syntax, "ExecuteTransaction", 1));
        Require(genericExecution, "PrepareOpcodes<TTracingInst>();", "transaction execution must prepare its matching table");
        string ordinaryExecution = Compact(FindMethod(vm.Syntax, "ExecuteTransaction", 0));
        Require(ordinaryExecution,
            "ExecuteTransaction<OffFlag>(vmState,worldState,txTracer)",
            "ordinary execution must make the untraced table reachable");
        MethodDeclarationSyntax dispatchEntry = FindMethodContaining(vm.Syntax,
            "RunByteCode<TTracingInst,OffFlag>(refstack,refgas)");
        string dispatchEntryText = Compact(dispatchEntry);
        Require(dispatchEntryText,
            "false=>RunByteCode<TTracingInst,OffFlag>(refstack,refgas),true=>RunByteCode<TTracingInst,OnFlag>(refstack,refgas)",
            "both cancellation loop specializations must be live");
        string runByteCode = Compact(FindMethod(vm.Syntax, "RunByteCode", 2));
        Require(runByteCode,
            "RunDispatchLoop<TTracingInst,TCancelable>(refstack,refgas,refprogramCounter)",
            "bytecode execution must enter the matching dispatch loop");
        string runDispatchLoop = Compact(FindMethod(dispatch.Syntax, "RunDispatchLoop", 2));
        Require(runDispatchLoop,
            "opcodeHandlers[opcode](refstack,refgas,refstate,programCounter,0)",
            "non-cancelable loop must invoke its live function pointer");
        Require(runDispatchLoop,
            "opcodeHandlers[opcode](refstack,refgas,refcancelableState,pc,opCodeCount)",
            "cancelable loop must invoke its live function pointer");

        string transactionProcessor = Get(sources,
            "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs").CompactText;
        Require(transactionProcessor,
            "substate=!DispatchFlags.Tracing(TTracingInst.IsActive)?VirtualMachine.ExecuteTransaction(state,WorldState,tracer):VirtualMachine.ExecuteTransaction<OnFlag>(state,WorldState,tracer);",
            "mainnet transaction processing must make both tracing specializations reachable");

        return
        [
            new("NoTrace", "OffFlag", "OffFlag"),
            new("NoTraceCancelable", "OffFlag", "OnFlag"),
            new("Traced", "OnFlag", "OffFlag"),
            new("TracedCancelable", "OnFlag", "OnFlag"),
        ];
    }

    private static IReadOnlyDictionary<string, string> ExtractFinalDispatchBodies(CompilationUnitSyntax syntax)
    {
        MethodDeclarationSyntax generate = FindMethod(syntax, "GenerateOpcodeHandlers", 2);
        Dictionary<string, List<AssignmentExpressionSyntax>> assignments = new(StringComparer.Ordinal);
        for (int index = 0; index < Seeds.Length; index++)
            assignments.Add(Seeds[index].Instruction, []);

        foreach (AssignmentExpressionSyntax assignment in generate.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (TryReadLookupInstruction(assignment.Left, out string? instruction) &&
                assignments.TryGetValue(instruction, out List<AssignmentExpressionSyntax>? matches))
                matches.Add(assignment);
        }

        Dictionary<string, string> bodies = new(StringComparer.Ordinal);
        for (int index = 0; index < Seeds.Length; index++)
        {
            DescriptorSeed seed = Seeds[index];
            List<AssignmentExpressionSyntax> matches = assignments[seed.Instruction];
            if (matches.Count != 1)
                throw new ExtractionException(
                    $"Instruction.{seed.Instruction} must have exactly one final live assignment; found {matches.Count}.");
            AssignmentExpressionSyntax assignment = matches[0];
            ValidateLiveAssignmentContext(generate, seed.Instruction, assignment);
            bodies.Add(seed.Instruction, ExtractOpcodeBody(seed.Instruction, assignment.Right));
        }
        return bodies;
    }

    private static bool TryReadLookupInstruction(ExpressionSyntax left, out string instruction)
    {
        instruction = string.Empty;
        if (left is not ElementAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: "lookup" },
                ArgumentList.Arguments.Count: 1,
            } access ||
            access.ArgumentList.Arguments[0].Expression is not CastExpressionSyntax
            {
                Type: PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.IntKeyword },
                Expression: MemberAccessExpressionSyntax member,
            } ||
            member.Expression is not IdentifierNameSyntax { Identifier.ValueText: "Instruction" })
            return false;
        instruction = member.Name.Identifier.ValueText;
        return true;
    }

    private static void ValidateLiveAssignmentContext(
        MethodDeclarationSyntax generate,
        string instruction,
        AssignmentExpressionSyntax assignment)
    {
        if (assignment.Parent is not ExpressionStatementSyntax statement)
            throw new ExtractionException($"Instruction.{instruction} dispatch is not a complete assignment statement.");

        string? requiredGate = instruction switch
        {
            "SHL" or "SHR" or "SAR" => "spec.ShiftOpcodesEnabled",
            "CLZ" => "spec.CLZEnabled",
            _ => null,
        };
        if (requiredGate is null)
        {
            if (!SameNode(statement.Parent, generate.Body))
                throw new ExtractionException($"Instruction.{instruction} dispatch is hidden in a branch or nested scope.");
            return;
        }

        IfStatementSyntax? gate = statement.Parent switch
        {
            IfStatementSyntax direct => direct,
            BlockSyntax block => block.Parent as IfStatementSyntax,
            _ => null,
        };
        if (gate is null || !SameNode(gate.Parent, generate.Body) ||
            !string.Equals(Compact(gate.Condition), requiredGate, StringComparison.Ordinal))
            throw new ExtractionException($"Instruction.{instruction} is not directly guarded by '{requiredGate}'.");
    }

    private static bool SameNode(SyntaxNode? left, SyntaxNode? right) =>
        left is not null && right is not null && left.RawKind == right.RawKind && left.Span == right.Span;

    private static string ExtractOpcodeBody(string instruction, ExpressionSyntax right)
    {
        if (instruction == "EXP")
        {
            if (right is not ConditionalExpressionSyntax conditional ||
                !string.Equals(Compact(conditional.Condition), "SpecFlags.Eip160(spec)", StringComparison.Ordinal))
                throw new ExtractionException("Instruction.EXP must be selected directly by the EIP-160 condition.");
            string enabled = ExtractOpcodeHandlerInvocation(conditional.WhenTrue);
            string disabled = ExtractOpcodeHandlerInvocation(conditional.WhenFalse);
            if (!string.Equals(disabled, "ExpOpcode<TTracingInst,OffFlag>", StringComparison.Ordinal))
                throw new ExtractionException("Instruction.EXP disabled branch no longer closes EIP-160 with OffFlag.");
            return enabled;
        }
        return ExtractOpcodeHandlerInvocation(right);
    }

    private static string ExtractOpcodeHandlerInvocation(ExpressionSyntax expression)
    {
        if (expression is not InvocationExpressionSyntax
            {
                Expression: GenericNameSyntax
                {
                    Identifier.ValueText: "OpcodeHandler",
                    TypeArgumentList.Arguments.Count: 3,
                } handler,
                ArgumentList.Arguments.Count: 0,
            })
            throw new ExtractionException($"Dispatch expression '{Compact(expression)}' is not a direct OpcodeHandler root.");
        if (!string.Equals(Compact(handler.TypeArgumentList.Arguments[1]), "TTracingInst", StringComparison.Ordinal) ||
            !string.Equals(Compact(handler.TypeArgumentList.Arguments[2]), "TCancelable", StringComparison.Ordinal))
            throw new ExtractionException("OpcodeHandler dispatch flags no longer flow from the live table specialization.");
        return Compact(handler.TypeArgumentList.Arguments[0]);
    }

    private static void ValidateWrappers(CompilationUnitSyntax syntax)
    {
        ValidateWrapper(syntax, "Math2Opcode", 2, "TGasPolicy.UpdateGas<TOpMath>(refgas)", "Math2ParamCore<TOpMath,TTracingInst,OffFlag>");
        ValidateWrapper(syntax, "Math3Opcode", 3, "TGasPolicy.UpdateGas<TOpMath>(refgas)", "Math3ParamCore<TOpMath,TTracingInst>");
        ValidateWrapper(syntax, "Math1Opcode", 1, "TGasPolicy.UpdateGas<TOpMath>(refgas)", "Math1ParamCore<TOpMath,TTracingInst,OffFlag>");
        ValidateWrapper(syntax, "BitwiseOpcode", 2, "TGasPolicy.UpdateGas<TOpBitwise>(refgas)", "BitwiseCore<TOpBitwise,TTracingInst,OffFlag>");
        ValidateWrapper(syntax, "ByteOpcode", 2, "TGasPolicy.UpdateGas<GasPolicy.VeryLowGasCost>(refgas)", "ByteCore<TTracingInst,OffFlag>");
        ValidateWrapper(syntax, "ShiftOpcode", 2, "TGasPolicy.UpdateGas<TOpShift>(refgas)", "ShiftCore<TOpShift,TTracingInst,OffFlag>");
        ValidateWrapper(syntax, "SarOpcode", 2, "TGasPolicy.UpdateGas<GasPolicy.VeryLowGasCost>(refgas)", "SarCore<TTracingInst,OffFlag>");
        ValidateWrapper(syntax, "CountLeadingZerosOpcode", 1, "TGasPolicy.UpdateGas<GasPolicy.LowGasCost>(refgas)", "CountLeadingZerosCore<TTracingInst,OffFlag>");

        string exp = Compact(FindType(syntax, "ExpOpcode"));
        Require(exp, "EvmInstructions.InstructionExp<TGasPolicy,TTracingInst,Eip160>(refstack,refgas,vm)", "EXP wrapper");
        string signExtend = Compact(FindType(syntax, "SignExtendOpcode"));
        Require(signExtend, "EvmInstructions.InstructionSignExtend<TGasPolicy,TTracingInst>(refstack,refgas,vm)", "SIGNEXTEND wrapper");
    }

    private static void ValidateWrapper(
        CompilationUnitSyntax syntax,
        string name,
        int stackInputs,
        string gasFragment,
        string coreFragment)
    {
        string wrapper = Compact(FindType(syntax, name));
        Require(wrapper, gasFragment, $"{name} gas charge");
        Require(wrapper, $"publicstaticintStackInputs=>{stackInputs.ToString(CultureInfo.InvariantCulture)};", $"{name} stack arity");
        Require(wrapper, coreFragment, $"{name} core route");
    }

    private static void ValidateStackAndGas(Dictionary<string, SourceFile> sources)
    {
        SourceFile stack = Get(sources, "src/Nethermind/Nethermind.Evm/EvmStack.cs");
        Require(stack.CompactText, "publicconstintMaxStackSize=1025;", "EvmStack sentinel-backed maximum");
        Require(stack.CompactText, "publicreadonlyboolEnsureDepth(intdepth)=>Head>=depth;", "EvmStack depth predicate");
        Require(stack.CompactText, "Head=(nint)(head-1);returnrefUnsafe.Add(ref_stack,(nint)((head-2)*WordSize));",
            "binary in-place pop/peek layout");
        Require(stack.CompactText, "Head=(nint)(head-2);returnrefUnsafe.Add(ref_stack,(nint)((head-3)*WordSize));",
            "ternary in-place pop/peek layout");

        string contract = Get(sources, "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs").CompactText;
        Require(contract,
            "staticvirtualboolUpdateGas<TCost>(refTSelfgas)whereTCost:struct,IGasCost=>TSelf.UpdateGas(refgas,TCost.GasCost);",
            "generic fixed-cost policy route");
        string gasPolicy = Get(sources, "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs").CompactText;
        Require(gasPolicy,
            "if(GetRemainingGas(ingas)<gasCost){gas.Value=0;returnfalse;}ConsumeRaw(refgas,gasCost);returntrue;",
            "execution OOG must clear gas and successful charges must subtract once");
        Require(gasPolicy,
            "UpdateGas(refgas,(Eip160.IsActive?GasCostOf.ExpByteEip160:GasCostOf.ExpByte)*exponentByteSize)",
            "EXP byte pricing must select EIP-160 through the closed flag");

        string gasTags = Get(sources, "src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs").CompactText;
        Require(gasTags, "publicreadonlystructVeryLowGasCost:IGasCost{publicstaticulongGasCost=>GasCostOf.VeryLow;}", "VeryLowGasCost tag");
        Require(gasTags, "publicreadonlystructLowGasCost:IGasCost{publicstaticulongGasCost=>GasCostOf.Low;}", "LowGasCost tag");
        Require(gasTags, "publicreadonlystructMidGasCost:IGasCost{publicstaticulongGasCost=>GasCostOf.Mid;}", "MidGasCost tag");
        Require(gasTags, "publicreadonlystructExpGasCost:IGasCost{publicstaticulongGasCost=>GasCostOf.Exp;}", "ExpGasCost tag");
    }

    private static void ValidateInstructionSemantics(Dictionary<string, SourceFile> sources)
    {
        string math1 = Get(sources, "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math1Param.cs").CompactText;
        Require(math1, "publicstaticEvmWordOperation(EvmWordvalue)=>Vector256.OnesComplement(value);", "NOT operation");
        Require(math1, "publicstaticEvmWordOperation(EvmWordvalue)=>value==default?OpBitwiseEq.One:default;", "ISZERO operation");
        Require(math1, "WriteSmallWordToSlot(refslot,EvmStack.IsSlotZero(refslot)?1UL:0UL);", "scalar ISZERO operation");
        Require(math1, "value=~value;Add(refvalue,1)=~Add(refvalue,1);Add(refvalue,2)=~Add(refvalue,2);Add(refvalue,3)=~Add(refvalue,3);", "scalar NOT operation");
        Require(math1, "ulongcount=(ulong)Bytes.CountLeadingZeroBits(refslot);WriteSmallWordToSlot(refslot,count);", "CLZ operation");
        Require(math1, "nintindex=(nint)(positionLow>>56);", "BYTE big-endian index");
        Require(math1, "index<EvmStack.WordSize?Add(reftopRef,index):(byte)0;", "BYTE operation");
        Require(math1, "if(!TGasPolicy.UpdateGas<LowGasCost>(refgas))returnEvmExceptionType.OutOfGas;", "SIGNEXTEND fixed charge");
        Require(math1, "if((index|Add(refindex,1)|Add(refindex,2)|(indexLow&0x00FF_FFFF_FFFF_FFFFUL))!=0||selector>=EvmStack.WordSize)", "SIGNEXTEND identity boundary");
        Require(math1, "intposition=31-(int)selector;", "SIGNEXTEND byte convention");

        string math2 = Get(sources, "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math2Param.cs").CompactText;
        Require(math2, "UInt256.Add(ina,inb,outresult)", "ADD operation");
        Require(math2, "UInt256.Subtract(ina,inb,outresult)", "SUB operation");
        Require(math2, "UInt256.Multiply(ina,inb,outresult)", "MUL operation");
        Require(math2, "if(b.IsZero){result=default;}else{UInt256.Divide(ina,inb,outresult);}", "DIV zero and quotient semantics");
        Require(math2, "if(b.IsZero){result=default;}elseif(As<UInt256,Int256>(refAsRef(inb))==Int256.MinusOne&&a==P255){result=P255;}", "SDIV exceptional operands");
        Require(math2, "Int256.Divide(", "SDIV signed operation");
        Require(math2, "if(b.IsZeroOrOne){result=default;}else{UInt256.Mod(ina,inb,outresult);}", "MOD operation");
        Require(math2, ".Mod(inAs<UInt256,Int256>(refAsRef(inb)),outAs<UInt256,Int256>(refresult));", "SMOD operation");
        Require(math2, "result=a<b?UInt256.One:default;", "LT operation");
        Require(math2, "result=a>b?UInt256.One:default;", "GT operation");
        Require(math2, ".CompareTo(As<UInt256,Int256>(refAsRef(inb)))<0?UInt256.One:default;", "SLT operation");
        Require(math2, ".CompareTo(As<UInt256,Int256>(refAsRef(inb)))>0?UInt256.One:default;", "SGT operation");
        Require(math2, "if(typeof(TOpMath)==typeof(OpAdd))", "optimized ADD branch");
        Require(math2, "if(typeof(TOpMath)==typeof(OpSub))", "optimized SUB branch");
        Require(math2, "boolsigned=typeof(TOpMath)==typeof(OpSLt)||typeof(TOpMath)==typeof(OpSGt);", "optimized signed comparison branch");
        RequireOrdered(math2,
            "if(!TGasPolicy.UpdateGas<ExpGasCost>(refgas))",
            "if(!stack.PopUInt256(outUInt256a,outUInt256exponent))",
            "intleadingZeros=exponent.CountLeadingZeros()>>3;",
            "if(!TGasPolicy.TryConsumeExpBytes<Eip160>(refgas,vm.Spec,expSize))",
            "UInt256.Exp(ina,inexponent,outUInt256expResult);");

        string math3 = Get(sources, "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math3Param.cs").CompactText;
        Require(math3, "UInt256.AddMod(ina,inb,inc,outresult)", "ADDMOD operation");
        Require(math3, "UInt256.MultiplyMod(ina,inb,inc,outresult)", "MULMOD operation");
        Require(math3, "if(!EvmStack.IsSlotZero(reftopRef))", "modular zero-modulus branch");

        string bitwise = Get(sources, "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Bitwise.cs").CompactText;
        Require(bitwise, "Vector256.BitwiseAnd(a,b)", "AND vector operation");
        Require(bitwise, "Vector256.BitwiseOr(a,b)", "OR vector operation");
        Require(bitwise, "Vector256.Xor(a,b)", "XOR vector operation");
        Require(bitwise, "publicstaticEvmWordOperation(inEvmWorda,inEvmWordb)=>a==b?One:default;", "EQ vector operation");
        Require(bitwise, "equal=difference==default;", "EQ Vector128 operation");
        Require(bitwise, "equal=((a^b)|", "EQ scalar operation");
        Require(bitwise, "if(typeof(TOpBitwise)==typeof(OpBitwiseAnd)){b&=a;", "AND scalar operation");
        Require(bitwise, "elseif(typeof(TOpBitwise)==typeof(OpBitwiseOr)){b|=a;", "OR scalar operation");
        Require(bitwise, "else{b^=a;", "XOR scalar operation");

        string shifts = Get(sources, "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Shifts.cs").CompactText;
        Require(shifts, "=>result=b<<(int)a.u0;", "SHL typed operation");
        Require(shifts, "=>result=b>>(int)a.u0;", "SHR typed operation");
        Require(shifts, "if(!shift.IsUint64||shift.u0>=256)", "shift large-amount boundary");
        Require(shifts, "if(typeof(TOpShift)==typeof(OpShl))", "scalar SHL selection");
        Require(shifts, "result=As<UInt256,Int256>(refvalue).Sign<0?UInt256.MaxValue:UInt256.Zero;", "SAR large-amount sign fill");
        Require(shifts, ".RightShift((int)shift,outInt256shifted);", "SAR signed operation");
        Require(shifts, "ulongfill=As<byte,sbyte>(reftopRef)<0?ulong.MaxValue:0;", "scalar SAR sign fill");
    }

    private static IReadOnlyList<OpcodeSpecialization> BuildSpecializations(
        IReadOnlyList<OpcodeDescriptor> opcodes,
        IReadOnlyList<DispatchTableBinding> dispatchTables)
    {
        if (dispatchTables.Count != 4)
            throw new ExtractionException($"Expected four live dispatch tables but found {dispatchTables.Count}.");
        List<OpcodeSpecialization> result = new(opcodes.Count * dispatchTables.Count);
        for (int opcodeIndex = 0; opcodeIndex < opcodes.Count; opcodeIndex++)
        {
            OpcodeDescriptor opcode = opcodes[opcodeIndex];
            for (int tableIndex = 0; tableIndex < dispatchTables.Count; tableIndex++)
            {
                DispatchTableBinding table = dispatchTables[tableIndex];
                result.Add(ExpectedSpecialization(opcode, table));
            }
        }

        if (result.Count != 104)
            throw new ExtractionException($"Expected 104 closed roots but produced {result.Count}.");
        ValidateSpecializations(opcodes, result);
        return result;
    }

    private static OpcodeSpecialization ExpectedSpecialization(
        OpcodeDescriptor opcode,
        DispatchTableBinding table)
    {
        string body = opcode.BodyType.Replace("TTracingInst", table.TracingFlag, StringComparison.Ordinal);
        string closedRoot =
            $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{body},{table.TracingFlag},{table.CancelableFlag},OnFlag>";
        if (closedRoot.Contains("TTracingInst", StringComparison.Ordinal) ||
            closedRoot.Contains("TGasPolicy", StringComparison.Ordinal))
            throw new ExtractionException($"Specialization {opcode.Instruction}/{table.Name} is not fully closed.");
        return new(opcode.Instruction, table.Name, table.TracingFlag, table.CancelableFlag, "OnFlag", closedRoot);
    }

    internal static void ValidateSpecializations(
        IReadOnlyList<OpcodeDescriptor> opcodes,
        IReadOnlyList<OpcodeSpecialization> specializations)
    {
        Dictionary<string, OpcodeDescriptor> descriptors = new(StringComparer.Ordinal);
        foreach (OpcodeDescriptor opcode in opcodes)
            if (!descriptors.TryAdd(opcode.Instruction, opcode))
                throw new ExtractionException($"Duplicate pure-word opcode descriptor {opcode.Instruction}.");
        Dictionary<string, DispatchTableBinding> tables = ExpectedDispatchTables
            .ToDictionary(static table => table.Name, StringComparer.Ordinal);
        HashSet<(string Opcode, string Table)> keys = [];
        foreach (OpcodeSpecialization specialization in specializations)
        {
            if (!keys.Add((specialization.Opcode, specialization.DispatchTable)))
                throw new ExtractionException($"Duplicate specialization {specialization.Opcode}/{specialization.DispatchTable}.");
            if (!descriptors.TryGetValue(specialization.Opcode, out OpcodeDescriptor? opcode) ||
                !tables.TryGetValue(specialization.DispatchTable, out DispatchTableBinding? table))
                throw new ExtractionException($"Unknown specialization key {specialization.Opcode}/{specialization.DispatchTable}.");
            if (specialization != ExpectedSpecialization(opcode, table))
                throw new ExtractionException(
                    $"Specialization {specialization.Opcode}/{specialization.DispatchTable} does not match its exact closed production root.");
        }

        int expectedCount = opcodes.Count * ExpectedDispatchTables.Length;
        if (specializations.Count != expectedCount || keys.Count != expectedCount)
            throw new ExtractionException($"Expected {expectedCount} exact closed roots but found {specializations.Count}.");
    }

    private static Dictionary<string, SourceFile> LoadSources(string repoRoot)
    {
        Dictionary<string, SourceFile> result = new(StringComparer.Ordinal);
        for (int index = 0; index < SourceRelativePaths.Length; index++)
        {
            string relativePath = SourceRelativePaths[index];
            string fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                throw new ExtractionException($"Required production source is missing: {relativePath}");
            byte[] bytes = File.ReadAllBytes(fullPath);
            string text = Encoding.UTF8.GetString(bytes);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview), fullPath);
            IEnumerable<Diagnostic> diagnostics = tree.GetDiagnostics();
            foreach (Diagnostic diagnostic in diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                    throw new ExtractionException($"Roslyn could not parse {relativePath}: {diagnostic}");
            }

            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
            result.Add(relativePath, new SourceFile(relativePath, bytes, root, Compact(root), SyntaxFingerprint(root)));
        }
        return result;
    }

    private static RawSourceFile LoadRawSource(string repoRoot, string relativePath)
    {
        string fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            throw new ExtractionException($"Required production source is missing: {relativePath}");
        return new RawSourceFile(relativePath, File.ReadAllBytes(fullPath));
    }

    private static void ValidatePinnedSyntaxFingerprints(Dictionary<string, SourceFile> sources)
    {
        if (ExpectedSyntaxFingerprints.Length != SourceRelativePaths.Length)
            throw new ExtractionException("Pinned syntax-fingerprint table does not match the source closure.");

        StringBuilder mismatches = new();
        for (int index = 0; index < SourceRelativePaths.Length; index++)
        {
            SourceFile source = Get(sources, SourceRelativePaths[index]);
            if (!string.Equals(ExpectedSyntaxFingerprints[index], source.SyntaxFingerprint, StringComparison.Ordinal))
            {
                mismatches.Append(source.RelativePath)
                    .Append(" = \"")
                    .Append(source.SyntaxFingerprint)
                    .AppendLine("\"");
            }
        }
        if (mismatches.Length != 0)
            throw new ExtractionException(
                "Pinned Roslyn token/trivia fingerprint rejected the production source closure:\n" + mismatches);
    }

    private static Dictionary<string, int> ReadInstructionValues(CompilationUnitSyntax syntax)
    {
        EnumDeclarationSyntax? instruction = null;
        foreach (EnumDeclarationSyntax candidate in syntax.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            if (candidate.Identifier.ValueText == "Instruction")
            {
                instruction = candidate;
                break;
            }
        }
        if (instruction is null)
            throw new ExtractionException("Production Instruction enum was not found.");

        Dictionary<string, int> values = new(StringComparer.Ordinal);
        int next = 0;
        foreach (EnumMemberDeclarationSyntax member in instruction.Members)
        {
            if (member.EqualsValue is not null)
                next = ParseInteger(member.EqualsValue.Value.ToString());
            values.Add(member.Identifier.ValueText, next);
            next++;
        }
        return values;
    }

    private static Dictionary<string, ulong> ReadGasCosts(CompilationUnitSyntax syntax)
    {
        TypeDeclarationSyntax gasCostOf = FindType(syntax, "GasCostOf");
        Dictionary<string, ulong> result = new(StringComparer.Ordinal);
        foreach (FieldDeclarationSyntax field in gasCostOf.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
            {
                if (variable.Identifier.ValueText is not ("VeryLow" or "Low" or "Mid" or "Exp" or "ExpByteEip160"))
                    continue;
                if (variable.Initializer is null)
                    throw new ExtractionException($"GasCostOf.{variable.Identifier.ValueText} has no initializer.");
                result.Add(variable.Identifier.ValueText, ParseUnsignedInteger(variable.Initializer.Value.ToString()));
            }
        }
        return result;
    }

    private static void ValidateGasCosts(Dictionary<string, ulong> values)
    {
        (string Name, ulong Value)[] expected =
        [
            ("VeryLow", 3),
            ("Low", 5),
            ("Mid", 8),
            ("Exp", 10),
            ("ExpByteEip160", 50),
        ];
        for (int index = 0; index < expected.Length; index++)
        {
            (string name, ulong value) = expected[index];
            if (!values.TryGetValue(name, out ulong actual) || actual != value)
                throw new ExtractionException($"GasCostOf.{name} must be {value} for the pinned Amsterdam schedule.");
        }
    }

    private static SourceManifest BuildManifest(Dictionary<string, SourceFile> sources, RawSourceFile buildTargets)
    {
        List<SourceEntry> entries = new(AllSourceRelativePaths.Length);
        StringBuilder combined = new();
        for (int index = 0; index < SourceRelativePaths.Length; index++)
        {
            SourceFile source = Get(sources, SourceRelativePaths[index]);
            string hash = Hash(source.Bytes);
            entries.Add(new SourceEntry(source.RelativePath, hash, source.SyntaxFingerprint));
            combined.Append(source.RelativePath).Append('\n')
                .Append(hash).Append('\n')
                .Append(source.SyntaxFingerprint).Append('\n');
        }
        string buildTargetsHash = Hash(buildTargets.Bytes);
        entries.Add(new SourceEntry(buildTargets.RelativePath, buildTargetsHash, $"raw:{buildTargetsHash}"));
        combined.Append(buildTargets.RelativePath).Append('\n')
            .Append(buildTargetsHash).Append('\n')
            .Append("raw:").Append(buildTargetsHash).Append('\n');
        return new SourceManifest(SchemaVersion, ExtractorVersion, Root, Hash(Utf8WithoutBom.GetBytes(combined.ToString())), entries);
    }

    private static byte[] Serialize<T>(T value)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        return Utf8WithoutBom.GetBytes(json);
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static SourceFile Get(Dictionary<string, SourceFile> sources, string path) =>
        sources.TryGetValue(path, out SourceFile? source)
            ? source
            : throw new ExtractionException($"Required source was not loaded: {path}");

    private static TypeDeclarationSyntax FindType(CompilationUnitSyntax syntax, string name)
    {
        foreach (TypeDeclarationSyntax declaration in syntax.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (declaration.Identifier.ValueText == name)
                return declaration;
        }
        throw new ExtractionException($"Production type '{name}' was not found.");
    }

    private static MethodDeclarationSyntax FindMethod(CompilationUnitSyntax syntax, string name, int typeParameterCount)
    {
        foreach (MethodDeclarationSyntax method in syntax.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Identifier.ValueText == name && (method.TypeParameterList?.Parameters.Count ?? 0) == typeParameterCount)
                return method;
        }
        throw new ExtractionException($"Production method '{name}' with {typeParameterCount} type parameter(s) was not found.");
    }

    private static MethodDeclarationSyntax FindMethod(TypeDeclarationSyntax syntax, string name, int typeParameterCount)
    {
        foreach (MethodDeclarationSyntax method in syntax.Members.OfType<MethodDeclarationSyntax>())
        {
            if (method.Identifier.ValueText == name && (method.TypeParameterList?.Parameters.Count ?? 0) == typeParameterCount)
                return method;
        }
        throw new ExtractionException(
            $"Production method '{syntax.Identifier.ValueText}.{name}' with {typeParameterCount} type parameter(s) was not found.");
    }

    private static MethodDeclarationSyntax FindMethodContaining(CompilationUnitSyntax syntax, string compactFragment)
    {
        MethodDeclarationSyntax? result = null;
        foreach (MethodDeclarationSyntax method in syntax.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!Compact(method).Contains(compactFragment, StringComparison.Ordinal))
                continue;
            if (result is not null)
                throw new ExtractionException($"Production method fragment '{compactFragment}' is not unique.");
            result = method;
        }
        return result ?? throw new ExtractionException($"Production method fragment '{compactFragment}' was not found.");
    }

    private static string Compact(SyntaxNode node)
    {
        StringBuilder builder = new();
        foreach (SyntaxToken token in node.DescendantTokens())
            builder.Append(token.Text);
        return builder.ToString();
    }

    private static string SyntaxFingerprint(CompilationUnitSyntax syntax)
    {
        StringBuilder canonical = new();
        foreach (SyntaxToken token in syntax.DescendantTokens())
        {
            AppendSyntaxElement(canonical, token.RawKind, token.Text);
            AppendTrivia(canonical, token.LeadingTrivia);
            AppendTrivia(canonical, token.TrailingTrivia);
        }
        return Hash(Utf8WithoutBom.GetBytes(canonical.ToString()));
    }

    private static void AppendTrivia(StringBuilder canonical, SyntaxTriviaList triviaList)
    {
        foreach (SyntaxTrivia trivia in triviaList)
        {
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                continue;
            AppendSyntaxElement(canonical, trivia.RawKind, trivia.ToFullString());
        }
    }

    private static void AppendSyntaxElement(StringBuilder canonical, int rawKind, string text) => canonical
        .Append(rawKind)
        .Append(':')
        .Append(text.Length)
        .Append(':')
        .Append(text)
        .Append('\n');

    private static void Require(string source, string fragment, string description)
    {
        if (!source.Contains(fragment, StringComparison.Ordinal))
            throw new ExtractionException($"Source profile rejected {description}; expected syntax fragment '{fragment}'.");
    }

    private static void RequireOrdered(string source, params string[] fragments)
    {
        int position = 0;
        for (int index = 0; index < fragments.Length; index++)
        {
            int found = source.IndexOf(fragments[index], position, StringComparison.Ordinal);
            if (found < 0)
                throw new ExtractionException($"Source profile rejected ordered production fragment '{fragments[index]}'.");
            position = found + fragments[index].Length;
        }
    }

    private static int ParseInteger(string source)
    {
        ulong value = ParseUnsignedInteger(source);
        if (value > int.MaxValue)
            throw new ExtractionException($"Integer literal '{source}' is outside the extractor range.");
        return (int)value;
    }

    private static ulong ParseUnsignedInteger(string source)
    {
        string value = source.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("UL", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("U", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private sealed record DescriptorSeed(
        string Name,
        string Instruction,
        string BodyType,
        string InstructionFamily,
        string OperationType,
        string WordSemantics,
        string FixedGasFamily,
        int StackInputs,
        string ActivationEvidence);

    private sealed record SourceFile(
        string RelativePath,
        byte[] Bytes,
        CompilationUnitSyntax Syntax,
        string CompactText,
        string SyntaxFingerprint);

    private sealed record RawSourceFile(string RelativePath, byte[] Bytes);
}
