// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.PureWordOpcodeExtractor.Test;

[TestFixture]
public class PureWordOpcodeExtractorTests
{
    private const string HandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    private const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    private const string GasCostsPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    private const string GasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string Math2Path = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math2Param.cs";
    private const string StandardVirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs";
    private const string NamedReleaseSpecPath = "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs";
    private const string ByzantiumPath = "src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs";
    private const string AmsterdamPath = "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs";
    private const string BlockProcessingModulePath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    private const string BuildTargetsPath = PureWordOpcodeProfile.BuildTargetsPath;

    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new("pure-word-opcode-first");
        using TemporaryDirectory second = new("pure-word-opcode-second");
        ExtractionResult firstResult = Extract(root, first.Path);
        ExtractionResult secondResult = Extract(root, second.Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.OpcodeCount, Is.EqualTo(26));
            Assert.That(firstResult.SpecializationCount, Is.EqualTo(104));
            Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.IrPath)));
            Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.ManifestPath)));
            Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.LeanPath)));
        }
    }

    [Test]
    public void Checked_artifacts_match_fresh_extraction()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory temporary = new("pure-word-opcode-checked");
        ExtractionResult result = Extract(root, temporary.Path);
        string generatedDirectory = System.IO.Path.Combine(
            root,
            "tools/Evm/Lean/PureWordOpcodeExtractor/Generated");
        string generatedLeanPath = System.IO.Path.Combine(
            root,
            PureWordOpcodeProfile.DefaultLeanRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                File.ReadAllBytes(result.IrPath),
                Is.EqualTo(File.ReadAllBytes(System.IO.Path.Combine(generatedDirectory, PureWordOpcodeProfile.IrFileName))));
            Assert.That(
                File.ReadAllBytes(result.ManifestPath),
                Is.EqualTo(File.ReadAllBytes(System.IO.Path.Combine(generatedDirectory, PureWordOpcodeProfile.ManifestFileName))));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(File.ReadAllBytes(generatedLeanPath)));
        }
    }

    [Test]
    public void Ir_names_every_closed_specialization_and_source()
    {
        string root = FindRepoRoot();
        string generatedDirectory = System.IO.Path.Combine(
            root,
            "tools/Evm/Lean/PureWordOpcodeExtractor/Generated");
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(
            generatedDirectory,
            PureWordOpcodeProfile.IrFileName)));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(
            generatedDirectory,
            PureWordOpcodeProfile.ManifestFileName)));

        JsonElement opcodes = ir.RootElement.GetProperty("opcodes");
        JsonElement specializations = ir.RootElement.GetProperty("specializations");
        Dictionary<string, int> specializationCounts = new(StringComparer.Ordinal);
        HashSet<string> dispatchTables = new(StringComparer.Ordinal);
        foreach (JsonElement specialization in specializations.EnumerateArray())
        {
            string opcode = specialization.GetProperty("opcode").GetString()!;
            specializationCounts.TryGetValue(opcode, out int count);
            specializationCounts[opcode] = count + 1;
            dispatchTables.Add(specialization.GetProperty("dispatchTable").GetString()!);
            Assert.That(specialization.GetProperty("continuableFlag").GetString(), Is.EqualTo("OnFlag"));
            Assert.That(
                specialization.GetProperty("closedRoot").GetString(),
                Does.StartWith("VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<"));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opcodes.GetArrayLength(), Is.EqualTo(26));
            Assert.That(specializations.GetArrayLength(), Is.EqualTo(104));
            Assert.That(specializationCounts, Has.Count.EqualTo(26));
            Assert.That(dispatchTables, Is.EquivalentTo(new[]
            {
                "NoTrace",
                "NoTraceCancelable",
                "Traced",
                "TracedCancelable",
            }));
            foreach (KeyValuePair<string, int> specialization in specializationCounts)
                Assert.That(specialization.Value, Is.EqualTo(4), specialization.Key);
            Assert.That(ir.RootElement.GetProperty("openExtractionObligations").GetArrayLength(), Is.EqualTo(3));
            Assert.That(manifest.RootElement.GetProperty("sources").GetArrayLength(), Is.EqualTo(41));
            Assert.That(manifest.RootElement.GetProperty("sources").EnumerateArray()
                .Select(static source => source.GetProperty("path").GetString()).Distinct(StringComparer.Ordinal).ToArray(),
                Has.Length.EqualTo(41));
        }
    }

    [Test]
    public void Serialized_ir_rejects_unknown_malformed_and_top_level_null_json()
    {
        byte[] bytes = ReadCheckedIrBytes();
        string json = Encoding.UTF8.GetString(bytes);
        string unknown = json.Insert(json.IndexOf('{') + 1, "\n  \"unexpected\": true,");

        ExtractionException unmapped = Assert.Throws<ExtractionException>(() =>
            PureWordOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(unknown)))!;
        ExtractionException malformed = Assert.Throws<ExtractionException>(() =>
            PureWordOpcodeProfile.DeserializeIr("{"u8.ToArray()))!;
        ExtractionException nullDocument = Assert.Throws<ExtractionException>(() =>
            PureWordOpcodeProfile.DeserializeIr("null"u8.ToArray()))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unmapped.Message, Does.Contain("unknown members"));
            Assert.That(malformed.Message, Does.Contain("malformed"));
            Assert.That(nullDocument.Message, Does.Contain("empty"));
        }
    }

    [TestCase("topCase")]
    [TestCase("nestedCase")]
    [TestCase("topOmitted")]
    [TestCase("nestedDefaultOmitted")]
    public void Serialized_ir_rejects_case_aliases_and_omitted_constructor_fields(string mutation)
    {
        JsonObject document = JsonNode.Parse(ReadCheckedIrBytes())!.AsObject();
        switch (mutation)
        {
            case "topCase":
                Rename(document, "schemaVersion", "SchemaVersion");
                break;
            case "nestedCase":
                Rename(document["opcodes"]!.AsArray()[0]!.AsObject(), "stackGrowth", "StackGrowth");
                break;
            case "topOmitted":
                document.Remove("schemaVersion");
                break;
            case "nestedDefaultOmitted":
                document["opcodes"]!.AsArray()[0]!.AsObject().Remove("stackGrowth");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        Assert.That(() => PureWordOpcodeProfile.DeserializeIr(
            Encoding.UTF8.GetBytes(document.ToJsonString())), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("opcodes")]
    [TestCase("opcodeEntry")]
    [TestCase("opcodeField")]
    [TestCase("specializations")]
    [TestCase("specializationEntry")]
    [TestCase("specializationField")]
    [TestCase("obligations")]
    [TestCase("obligationEntry")]
    public void Serialized_ir_rejects_nested_nulls(string mutation)
    {
        JsonObject document = JsonNode.Parse(ReadCheckedIrBytes())!.AsObject();
        switch (mutation)
        {
            case "opcodes":
                document["opcodes"] = null;
                break;
            case "opcodeEntry":
                document["opcodes"]!.AsArray()[0] = null;
                break;
            case "opcodeField":
                document["opcodes"]!.AsArray()[0]!["bodyType"] = null;
                break;
            case "specializations":
                document["specializations"] = null;
                break;
            case "specializationEntry":
                document["specializations"]!.AsArray()[0] = null;
                break;
            case "specializationField":
                document["specializations"]!.AsArray()[0]!["closedRoot"] = null;
                break;
            case "obligations":
                document["openExtractionObligations"] = null;
                break;
            case "obligationEntry":
                document["openExtractionObligations"]!.AsArray()[0] = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            PureWordOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(document.ToJsonString())))!;
        Assert.That(exception.Message, Does.Contain("null collections, entries, or fields"));
    }

    [TestCase("header")]
    [TestCase("gas")]
    [TestCase("descriptor")]
    [TestCase("specialization")]
    [TestCase("obligation")]
    public void Arbitrary_serialized_ir_fields_are_rejected(string mutation)
    {
        JsonObject document = JsonNode.Parse(ReadCheckedIrBytes())!.AsObject();
        switch (mutation)
        {
            case "header":
                document["fork"] = "Nethermind.Specs.Forks.Frontier";
                break;
            case "gas":
                document["expByteGas"] = 49;
                break;
            case "descriptor":
                document["opcodes"]!.AsArray()[0]!["wordSemantics"] = "sub";
                break;
            case "specialization":
                document["specializations"]!.AsArray()[0]!["closedRoot"] = "ArbitraryRoot";
                break;
            case "obligation":
                document["openExtractionObligations"]!.AsArray()[0] = "arbitrary";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        Assert.That(() => PureWordOpcodeProfile.DeserializeIr(
            Encoding.UTF8.GetBytes(document.ToJsonString())), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("duplicate", "Duplicate specialization")]
    [TestCase("unbound", "does not match its exact closed production root")]
    public void Duplicate_or_unbound_serialized_specialization_is_rejected(string mutation, string expectedMessage)
    {
        IrDocument document = PureWordOpcodeProfile.DeserializeIr(ReadCheckedIrBytes());
        List<OpcodeSpecialization> specializations = document.Specializations.ToList();
        specializations[0] = mutation switch
        {
            "duplicate" => specializations[1],
            "unbound" => specializations[0] with
            {
                ClosedRoot = "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<Math2Opcode<EvmInstructions.OpAdd,TTracingInst>,OffFlag,OffFlag,OnFlag>",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };

        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            PureWordOpcodeProfile.ValidateSpecializations(document.Opcodes, specializations))!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    [Test]
    public void Generated_Lean_is_theorem_free_and_excludes_the_execution_reference()
    {
        string root = FindRepoRoot();
        string generated = File.ReadAllText(System.IO.Path.Combine(
            root,
            PureWordOpcodeProfile.DefaultLeanRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Contain("theorem "));
            Assert.That(generated, Does.Not.Contain("axiom "));
            Assert.That(generated, Does.Not.Contain("PureStackExecution"));
            Assert.That(generated, Does.Contain("import Eip803x.Gas"));
            Assert.That(generated, Does.Contain("import Eip803x.Evm.MemoryStackControl"));
            Assert.That(generated, Does.Contain("import Eip803x.Evm.Word"));
            Assert.That(generated, Does.Contain("def stackExecute"));
            Assert.That(generated, Does.Contain("def execute"));
        }
    }

    [TestCase(
        HandlersPath,
        "lookup[(int)Instruction.ADD] = OpcodeHandler<Math2Opcode<EvmInstructions.OpAdd, TTracingInst>, TTracingInst, TCancelable>();",
        "lookup[(int)Instruction.ADD] = OpcodeHandler<Math2Opcode<EvmInstructions.OpSub, TTracingInst>, TTracingInst, TCancelable>();",
        "Production dispatch body for ADD changed")]
    [TestCase(DispatchPath, "pc++;", "pc--;", "ordered production fragment 'pc++;'")]
    [TestCase(GasCostsPath, "public const ulong VeryLow = 3;", "public const ulong VeryLow = 4;", "GasCostOf.VeryLow must be 3")]
    [TestCase(GasPolicyPath, "gas.Value = 0;", "gas.Value = 1;", "execution OOG must clear gas")]
    [TestCase(
        HandlersPath,
        "public static int StackInputs => 2;",
        "public static int StackInputs => 3;",
        "Math2Opcode stack arity")]
    [TestCase(
        Math2Path,
        "UInt256.Add(in a, in b, out result)",
        "UInt256.Subtract(in a, in b, out result)",
        "ADD operation")]
    [TestCase(
        AmsterdamPath,
        "NamedReleaseSpec<Amsterdam>(BPO2.Instance)",
        "NamedReleaseSpec<Amsterdam>(BPO1.Instance)",
        "Amsterdam must inherit BPO2")]
    [TestCase(
        BlockProcessingModulePath,
        ".AddScoped<IVirtualMachine, EthereumVirtualMachine>()",
        ".AddScoped<IVirtualMachine, VirtualMachine>()",
        "mainnet block processing must register EthereumVirtualMachine")]
    public void Production_mutation_is_rejected(
        string relativePath,
        string original,
        string replacement,
        string expectedMessage)
    {
        using Fixture fixture = new();
        fixture.Replace(relativePath, original, replacement);

        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.Extract())!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    [Test]
    public void Unmodeled_semantic_insertion_is_rejected()
    {
        using Fixture fixture = new();
        fixture.InsertAtMethodBody(
            Math2Path,
            "internal static EvmExceptionType Math2ParamCore",
            "int unmodeled = 0;");

        AssertFingerprintRejected(fixture);
    }

    [Test]
    public void Dead_semantic_branch_is_rejected()
    {
        using Fixture fixture = new();
        fixture.InsertAtMethodBody(
            Math2Path,
            "internal static EvmExceptionType Math2ParamCore",
            "if (false) return EvmExceptionType.OutOfGas;");

        AssertFingerprintRejected(fixture);
    }

    [Test]
    public void Reachable_early_success_return_is_rejected()
    {
        using Fixture fixture = new();
        fixture.InsertAtMethodBody(
            Math2Path,
            "internal static EvmExceptionType Math2ParamCore",
            "return EvmExceptionType.None;");

        AssertFingerprintRejected(fixture);
    }

    [Test]
    public void Dead_branch_dispatch_anchor_is_not_admitted()
    {
        const string original =
            "lookup[(int)Instruction.ADD] = OpcodeHandler<Math2Opcode<EvmInstructions.OpAdd, TTracingInst>, TTracingInst, TCancelable>();";
        const string wrong =
            "lookup[(int)Instruction.ADD] = OpcodeHandler<Math2Opcode<EvmInstructions.OpSub, TTracingInst>, TTracingInst, TCancelable>();";
        using Fixture fixture = new();
        fixture.Replace(
            HandlersPath,
            original,
            $"if (false) {{ {original} }}\n        {wrong}");

        AssertRejected(fixture, "must have exactly one final live assignment; found 2");
    }

    [Test]
    public void Later_competing_dispatch_assignment_is_rejected()
    {
        const string original =
            "lookup[(int)Instruction.ADD] = OpcodeHandler<Math2Opcode<EvmInstructions.OpAdd, TTracingInst>, TTracingInst, TCancelable>();";
        const string competing =
            "lookup[(int)Instruction.ADD] = OpcodeHandler<Math2Opcode<EvmInstructions.OpSub, TTracingInst>, TTracingInst, TCancelable>();";
        using Fixture fixture = new();
        fixture.Replace(HandlersPath, original, $"{original}\n        {competing}");

        AssertRejected(fixture, "must have exactly one final live assignment; found 2");
    }

    [Test]
    public void Duplicate_opcode_byte_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(InstructionPath, "SUB = 0x03,", "SUB = 0x01,");

        AssertRejected(fixture, "duplicates pure-word opcode byte 1");
    }

    [Test]
    public void Out_of_range_opcode_byte_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(InstructionPath, "CLZ = 0x1e,", "CLZ = 0x100,");

        AssertRejected(fixture, "byte 256 is outside the EVM byte range");
    }

    [Test]
    public void Live_table_binding_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(
            DispatchPath,
            "TCancelable.IsActive ? ref TracedCancelable : ref Traced",
            "TCancelable.IsActive ? ref Traced : ref TracedCancelable");

        AssertRejected(fixture, "four live tracing/cancellation table bindings");
    }

    [Test]
    public void Function_pointer_root_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(
            HandlersPath,
            "&ExecuteOpcode<TOpcode, TTracingInst, TCancelable, OnFlag>;",
            "&ExecuteOpcode<TOpcode, TTracingInst, TCancelable, OffFlag>;");

        AssertRejected(fixture, "ordinary opcode handlers must select the continuable ExecuteOpcode root");
    }

    [Test]
    public void Alternate_standard_opcode_table_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(
            StandardVirtualMachinePath,
            "private OpcodeTable GetOpcodeTable() =>\n        _opcodeTablesBySpec.GetValue(Spec, static _ => new OpcodeTable());",
            "private OpcodeTable GetOpcodeTable() => new OpcodeTable();");

        AssertRejected(fixture, "Standard GetOpcodeTable must return the per-spec initially empty OpcodeTable");
    }

    [Test]
    public void Standard_opcode_refresh_side_effect_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(
            StandardVirtualMachinePath,
            "private partial bool ShouldRefreshOpcodes()\n    {",
            "private partial bool ShouldRefreshOpcodes()\n    {\n        _ = GetOpcodeTable().GetHandlers<OffFlag, OffFlag>(Spec);");

        AssertRejected(fixture, "Standard ShouldRefreshOpcodes must only select the admitted non-traced table refresh cadence");
    }

    [Test]
    public void Named_fork_replay_order_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(
            NamedReleaseSpecPath,
            "ReplayAncestors(fork.Parent);\n        fork.Apply(this);",
            "fork.Apply(this);\n        ReplayAncestors(fork.Parent);");

        AssertRejected(fixture, "named-fork replay must apply every ancestor from root to leaf");
    }

    [Test]
    public void Fork_parent_mutation_that_skips_Eip160_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(
            ByzantiumPath,
            "NamedReleaseSpec<Byzantium>(SpuriousDragon.Instance)",
            "NamedReleaseSpec<Byzantium>(TangerineWhistle.Instance)");

        AssertRejected(fixture, "Byzantium must inherit SpuriousDragon");
    }

    [Test]
    public void Named_fork_singleton_bridge_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(NamedReleaseSpecPath, "new TSelf()", "Amsterdam.Instance");

        AssertRejected(fixture, "named-fork generic singleton must construct the selected fork type");
    }

    [Test]
    public void Standard_build_source_selection_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(
            BuildTargetsPath,
            "<ItemGroup Condition=\"'$(EnableZkEvm)' != 'true'\">",
            "<ItemGroup Condition=\"'$(EnableZkEvm)' == 'true'\">");

        AssertRejected(fixture, "Directory.Build.targets must select .std.cs and exclude .zkevm.cs");
    }

    private static void AssertFingerprintRejected(Fixture fixture) =>
        AssertRejected(fixture, "Pinned Roslyn token/trivia fingerprint rejected");

    private static void AssertRejected(Fixture fixture, string expectedMessage)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.Extract())!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    private static ExtractionResult Extract(string root, string output) => PureWordOpcodeProfile.Extract(
        root,
        output,
        System.IO.Path.Combine(output, "PureWordOpcodeKernel.lean"));

    private static byte[] ReadCheckedIrBytes() => File.ReadAllBytes(System.IO.Path.Combine(
        FindRepoRoot(),
        "tools/Evm/Lean/PureWordOpcodeExtractor/Generated",
        PureWordOpcodeProfile.IrFileName));

    private static void Rename(JsonObject document, string oldName, string newName)
    {
        JsonNode? value = document[oldName];
        Assert.That(value, Is.Not.Null);
        document.Remove(oldName);
        document[newName] = value;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, HandlersPath)))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Nethermind repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = System.IO.Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "pure-word-opcode-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
            Output = System.IO.Path.Combine(Root, "generated");
            string productionRoot = FindRepoRoot();
            foreach (string relativePath in PureWordOpcodeProfile.SourcePaths)
            {
                Write(relativePath, File.ReadAllText(System.IO.Path.Combine(productionRoot, relativePath)));
            }
        }

        public string Root { get; }

        public string Output { get; }

        public void Extract() => PureWordOpcodeProfile.Extract(
            Root,
            Output,
            System.IO.Path.Combine(Output, "PureWordOpcodeKernel.lean"));

        public void Replace(string relativePath, string original, string replacement)
        {
            string source = Read(relativePath);
            Assert.That(source, Does.Contain(original));
            Write(relativePath, source.Replace(original, replacement, StringComparison.Ordinal));
        }

        public void InsertAtMethodBody(string relativePath, string methodName, string statement)
        {
            string source = Read(relativePath);
            int methodStart = source.IndexOf(methodName, StringComparison.Ordinal);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            int bodyStart = source.IndexOf('{', methodStart);
            Assert.That(bodyStart, Is.GreaterThan(methodStart));
            Write(relativePath, source.Insert(bodyStart + 1, $"\n        {statement}"));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private string Read(string relativePath) => File.ReadAllText(System.IO.Path.Combine(Root, relativePath));

        private void Write(string relativePath, string source)
        {
            string path = System.IO.Path.Combine(Root, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private sealed class TemporaryDirectory(string prefix) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            prefix,
            Guid.NewGuid().ToString("N"));

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
