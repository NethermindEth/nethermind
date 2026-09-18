// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.MemoryControlOpcodeExtractor.Test;

[TestFixture]
public class MemoryControlOpcodeExtractorTests
{
    private const string HandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    private const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    private const string StoragePath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs";
    private const string BlockModulePath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    private const string AmsterdamPath = "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs";
    private const string VirtualMachineStandardPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs";
    private const string NamedReleaseSpecPath = "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs";
    private const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";
    private const string GasCostPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";

    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = RepoRoot();
        using TemporaryDirectory first = new("memory-control-first");
        using TemporaryDirectory second = new("memory-control-second");
        ExtractionResult firstResult = Extract(root, first.Path);
        ExtractionResult secondResult = Extract(root, second.Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.OpcodeCount, Is.EqualTo(84));
            Assert.That(firstResult.SpecializationCount, Is.EqualTo(336));
            Assert.That(firstResult.AdmissionCount, Is.EqualTo(167));
            Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.IrPath)));
            Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.ManifestPath)));
            Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.LeanPath)));
        }
    }

    [Test]
    public void Checked_artifacts_match_fresh_extraction()
    {
        string root = RepoRoot();
        using TemporaryDirectory temporary = new("memory-control-checked");
        ExtractionResult actual = Extract(root, temporary.Path);
        string checkedDirectory = Path.Combine(root, "tools/Evm/Lean/MemoryControlOpcodeExtractor/Generated");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(actual.IrPath),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, MemoryControlOpcodeProfile.IrFileName))));
            Assert.That(File.ReadAllBytes(actual.ManifestPath),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, MemoryControlOpcodeProfile.ManifestFileName))));
            Assert.That(File.ReadAllBytes(actual.LeanPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(
                root, MemoryControlOpcodeProfile.DefaultLeanRelativePath.Replace('/', Path.DirectorySeparatorChar)))));
        }
    }

    [Test]
    public void Ir_names_every_opcode_table_specialization_and_admission()
    {
        string root = RepoRoot();
        string generated = Path.Combine(root, "tools/Evm/Lean/MemoryControlOpcodeExtractor/Generated");
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated, MemoryControlOpcodeProfile.IrFileName)));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated, MemoryControlOpcodeProfile.ManifestFileName)));
        JsonElement opcodes = ir.RootElement.GetProperty("opcodes");
        JsonElement specializations = ir.RootElement.GetProperty("specializations");
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        HashSet<string> tables = new(StringComparer.Ordinal);
        foreach (JsonElement specialization in specializations.EnumerateArray())
        {
            string opcode = specialization.GetProperty("opcode").GetString()!;
            counts.TryGetValue(opcode, out int count);
            counts[opcode] = count + 1;
            tables.Add(specialization.GetProperty("dispatchTable").GetString()!);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opcodes.GetArrayLength(), Is.EqualTo(84));
            Assert.That(specializations.GetArrayLength(), Is.EqualTo(336));
            Assert.That(counts, Has.Count.EqualTo(84));
            Assert.That(counts.Values, Is.All.EqualTo(4));
            Assert.That(tables, Is.EquivalentTo(new[] { "NoTrace", "NoTraceCancelable", "Traced", "TracedCancelable" }));
            Assert.That(ir.RootElement.GetProperty("forkLineage").GetArrayLength(), Is.EqualTo(23));
            Assert.That(manifest.RootElement.GetProperty("admissions").GetArrayLength(), Is.EqualTo(167));
            Assert.That(manifest.RootElement.GetProperty("rawSources").GetArrayLength(), Is.EqualTo(1));
            Assert.That(opcodes.EnumerateArray().Select(static opcode => opcode.GetProperty("fixedGas").GetInt32()),
                Is.All.InRange(0, 10));
        }
    }

    [Test]
    public void Admission_identities_are_owner_qualified_unique_and_duplicate_rejected()
    {
        string manifestPath = Path.Combine(RepoRoot(),
            "tools/Evm/Lean/MemoryControlOpcodeExtractor/Generated",
            MemoryControlOpcodeProfile.ManifestFileName);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        string[] keys = manifest.RootElement.GetProperty("admissions").EnumerateArray()
            .Select(static admission => admission.GetProperty("key").GetString()!).ToArray();

        ExtractionException duplicate = Assert.Throws<ExtractionException>(() =>
            MemoryControlOpcodeProfile.ValidateAdmissionIdentities(
            [
                new("same", "template-a", "source-a"),
                new("same", "template-b", "source-b"),
            ]))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys, Has.Length.EqualTo(167));
            Assert.That(keys.Distinct(StringComparer.Ordinal).ToArray(), Has.Length.EqualTo(keys.Length));
            Assert.That(keys, Has.Some.Contains(":Nethermind.Evm:VirtualMachineOpcodeHandlers:VirtualMachine/1:"));
            Assert.That(duplicate.Message, Does.Contain("Duplicate owner-qualified admission identity"));
        }
    }

    [Test]
    public void Serialized_ir_rejects_unknown_malformed_and_top_level_null_json()
    {
        byte[] bytes = ReadCheckedIrBytes();
        string json = Encoding.UTF8.GetString(bytes);
        string unknown = json.Insert(json.IndexOf('{') + 1, "\n  \"unexpected\": true,");

        ExtractionException unmapped = Assert.Throws<ExtractionException>(() =>
            MemoryControlOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(unknown)))!;
        ExtractionException malformed = Assert.Throws<ExtractionException>(() =>
            MemoryControlOpcodeProfile.DeserializeIr("{"u8.ToArray()))!;
        ExtractionException nullDocument = Assert.Throws<ExtractionException>(() =>
            MemoryControlOpcodeProfile.DeserializeIr("null"u8.ToArray()))!;

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
                Rename(document["opcodes"]!.AsArray()[0]!.AsObject(), "fixedGas", "FixedGas");
                break;
            case "topOmitted":
                document.Remove("schemaVersion");
                break;
            case "nestedDefaultOmitted":
                document["opcodes"]!.AsArray()[0]!.AsObject().Remove("fixedGas");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        Assert.That(() => MemoryControlOpcodeProfile.DeserializeIr(
            Encoding.UTF8.GetBytes(document.ToJsonString())), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("opcodes")]
    [TestCase("opcodeEntry")]
    [TestCase("opcodeField")]
    [TestCase("specializations")]
    [TestCase("specializationEntry")]
    [TestCase("specializationField")]
    [TestCase("forkLineage")]
    [TestCase("forkEntry")]
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
                document["opcodes"]!.AsArray()[0]!["handlerBody"] = null;
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
            case "forkLineage":
                document["forkLineage"] = null;
                break;
            case "forkEntry":
                document["forkLineage"]!.AsArray()[0] = null;
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
            MemoryControlOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(document.ToJsonString())))!;
        Assert.That(exception.Message, Does.Contain("null collections, entries, or fields"));
    }

    [TestCase("header")]
    [TestCase("gas")]
    [TestCase("descriptor")]
    [TestCase("specialization")]
    [TestCase("fork")]
    [TestCase("obligation")]
    public void Arbitrary_serialized_ir_fields_are_rejected(string mutation)
    {
        JsonObject document = JsonNode.Parse(ReadCheckedIrBytes())!.AsObject();
        switch (mutation)
        {
            case "header":
                document["root"] = "arbitrary";
                break;
            case "gas":
                document["copyWordGas"] = 4;
                break;
            case "descriptor":
                document["opcodes"]!.AsArray()[0]!["exitKind"] = "continue";
                break;
            case "specialization":
                document["specializations"]!.AsArray()[0]!["closedRoot"] = "ArbitraryRoot";
                break;
            case "fork":
                document["forkLineage"]!.AsArray()[0] = "Frontier";
                break;
            case "obligation":
                document["openExtractionObligations"]!.AsArray()[0] = "arbitrary";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        Assert.That(() => MemoryControlOpcodeProfile.DeserializeIr(
            Encoding.UTF8.GetBytes(document.ToJsonString())), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Generated_Lean_is_theorem_free_and_independent()
    {
        string generated = File.ReadAllText(Path.Combine(
            RepoRoot(), MemoryControlOpcodeProfile.DefaultLeanRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Contain("theorem "));
            Assert.That(generated, Does.Not.Contain("axiom "));
            Assert.That(generated, Does.Not.Contain("MemoryStackControl"));
            Assert.That(generated, Does.Not.Contain("MemoryControlExecution"));
            Assert.That(generated, Does.Contain("def descriptor"));
            Assert.That(generated, Does.Contain("def specializations"));
        }
    }

    [Test]
    public void Every_specialization_has_the_exact_fully_closed_root()
    {
        string generated = Path.Combine(RepoRoot(), "tools/Evm/Lean/MemoryControlOpcodeExtractor/Generated");
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            generated, MemoryControlOpcodeProfile.IrFileName)));
        Dictionary<string, (string HandlerBody, string HandlerRoot)> descriptors = ir.RootElement
            .GetProperty("opcodes")
            .EnumerateArray()
            .ToDictionary(
                static opcode => opcode.GetProperty("name").GetString()!,
                static opcode => (
                    opcode.GetProperty("handlerBody").GetString()!,
                    opcode.GetProperty("handlerRoot").GetString()!),
                StringComparer.Ordinal);
        Dictionary<string, (string Tracing, string Cancelable)> tables = new(StringComparer.Ordinal)
        {
            ["NoTrace"] = ("OffFlag", "OffFlag"),
            ["NoTraceCancelable"] = ("OffFlag", "OnFlag"),
            ["Traced"] = ("OnFlag", "OffFlag"),
            ["TracedCancelable"] = ("OnFlag", "OnFlag"),
        };
        Dictionary<string, string> roots = new(StringComparer.Ordinal);
        List<string> mismatches = [];

        foreach (JsonElement specialization in ir.RootElement.GetProperty("specializations").EnumerateArray())
        {
            string opcode = specialization.GetProperty("opcode").GetString()!;
            string table = specialization.GetProperty("dispatchTable").GetString()!;
            (string handlerBody, string handlerRoot) = descriptors[opcode];
            (string tracing, string cancelable) = tables[table];
            string closedBody = handlerBody
                .Replace("TTracingInst", tracing, StringComparison.Ordinal)
                .Replace("TGasPolicy", "EthereumGasPolicy", StringComparison.Ordinal);
            string expected = handlerRoot switch
            {
                "ordinary" => $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{closedBody},{tracing},{cancelable},OnFlag>",
                "terminating" => $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{closedBody},{tracing},{cancelable},OffFlag>",
                "jumpIf" => $"VirtualMachine<EthereumGasPolicy>.ExecuteJumpIfOpcode<{tracing},{cancelable}>",
                _ => throw new InvalidOperationException($"Unknown handler root {handlerRoot}."),
            };
            string actual = specialization.GetProperty("closedRoot").GetString()!;
            if (actual != expected || actual.Contains("TTracingInst", StringComparison.Ordinal) ||
                actual.Contains("TGasPolicy", StringComparison.Ordinal))
                mismatches.Add($"{opcode}/{table}: expected {expected}, actual {actual}");
            roots.Add($"{opcode}/{table}", actual);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches, Is.Empty);
            Assert.That(roots, Has.Count.EqualTo(336));
            Assert.That(roots["calldataload/NoTrace"], Is.EqualTo(
                "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CallDataLoadOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>"));
            Assert.That(roots["msize/NoTrace"], Is.EqualTo(
                "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<EnvUInt64Opcode<EvmInstructions.OpMSize<EthereumGasPolicy>,OffFlag>,OffFlag,OffFlag,OnFlag>"));
        }
    }

    [TestCase(false, "does not match its exact closed production root")]
    [TestCase(true, "Duplicate specialization")]
    public void Mutated_or_duplicate_specialization_is_rejected(bool duplicate, string expectedMessage)
    {
        string irPath = Path.Combine(
            RepoRoot(),
            "tools/Evm/Lean/MemoryControlOpcodeExtractor/Generated",
            MemoryControlOpcodeProfile.IrFileName);
        IrDocument document = MemoryControlOpcodeProfile.DeserializeIr(File.ReadAllBytes(irPath));
        List<OpcodeSpecialization> specializations = document.Specializations.ToList();
        specializations[0] = duplicate
            ? specializations[1]
            : specializations[0] with { ClosedRoot = "ArbitraryRoot" };

        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            MemoryControlOpcodeProfile.ValidateSpecializations(document.Opcodes, specializations))!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    [TestCase(
        HandlersPath,
        "lookup[(int)Instruction.MLOAD] = OpcodeHandler<MLoadOpcode<TTracingInst>, TTracingInst, TCancelable>();",
        "lookup[(int)Instruction.MLOAD] = OpcodeHandler<MStoreOpcode<TTracingInst>, TTracingInst, TCancelable>();",
        "Unadmitted complete syntax")]
    [TestCase(StoragePath, "TGasPolicy.TryConsumeMemoryCopy(ref gas, words)", "TGasPolicy.UpdateGas(ref gas, words)", "Unadmitted complete syntax")]
    [TestCase(InstructionPath, "MLOAD = 0x51,", "MLOAD = 0x50,", "Unadmitted complete syntax")]
    [TestCase(
        DispatchPath,
        "TCancelable.IsActive ? ref TracedCancelable : ref Traced",
        "TCancelable.IsActive ? ref Traced : ref TracedCancelable",
        "Unadmitted complete syntax")]
    [TestCase(
        BlockModulePath,
        ".AddScoped<IVirtualMachine, EthereumVirtualMachine>()",
        ".AddScoped<IVirtualMachine, PrewarmingEthereumVirtualMachine>()",
        "Unadmitted complete syntax")]
    [TestCase(
        AmsterdamPath,
        "NamedReleaseSpec<Amsterdam>(BPO2.Instance)",
        "NamedReleaseSpec<Amsterdam>(BPO1.Instance)",
        "Unadmitted complete syntax")]
    [TestCase(
        VirtualMachineStandardPath,
        "_opcodeTablesBySpec.GetValue(Spec, static _ => new OpcodeTable())",
        "_opcodeTablesBySpec.GetValue(Frontier.Instance, static _ => new OpcodeTable())",
        "Unadmitted complete syntax")]
    [TestCase(
        NamedReleaseSpecPath,
        "ReplayAncestors(fork.Parent);\n        fork.Apply(this);",
        "fork.Apply(this);\n        ReplayAncestors(fork.Parent);",
        "Unadmitted complete syntax")]
    [TestCase(NamedReleaseSpecPath, "new TSelf()", "Amsterdam.Instance", "Unadmitted complete syntax")]
    [TestCase(GasCostPath, "public const ulong VeryLow = 3;", "public const ulong VeryLow = 4;", "Unadmitted complete syntax")]
    public void Production_mutation_is_rejected(string path, string before, string after, string message)
    {
        using Fixture fixture = new();
        fixture.Replace(path, before, after);
        AssertRejected(fixture, message);
    }

    [Test]
    public void Dead_semantic_branch_is_rejected()
    {
        using Fixture fixture = new();
        fixture.InsertAtMethodBody(StoragePath, "InstructionMLoad", "if (false) return EvmExceptionType.None;");
        AssertRejected(fixture, "Unadmitted complete syntax");
    }

    [Test]
    public void Reachable_early_return_is_rejected()
    {
        using Fixture fixture = new();
        fixture.InsertAtMethodBody(StoragePath, "InstructionMStore", "return EvmExceptionType.None;");
        AssertRejected(fixture, "Unadmitted complete syntax");
    }

    [Test]
    public void Later_competing_dispatch_assignment_is_rejected()
    {
        const string assignment =
            "lookup[(int)Instruction.POP] = OpcodeHandler<PopOpcode, TTracingInst, TCancelable>();";
        using Fixture fixture = new();
        fixture.Replace(HandlersPath, assignment, assignment + Environment.NewLine + "        " + assignment);
        AssertRejected(fixture, "Unadmitted complete syntax");
    }

    [Test]
    public void Dead_dispatch_assignment_is_rejected()
    {
        const string assignment =
            "lookup[(int)Instruction.POP] = OpcodeHandler<PopOpcode, TTracingInst, TCancelable>();";
        using Fixture fixture = new();
        fixture.Replace(HandlersPath, assignment, $"if (false) {{ {assignment} }}{Environment.NewLine}        {assignment}");
        AssertRejected(fixture, "Unadmitted complete syntax");
    }

    [Test]
    public void Preprocessor_directive_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(StoragePath, "namespace Nethermind.Evm;", "#if EXTRA\nnamespace Nethermind.Evm;\n#endif");
        AssertRejected(fixture, "Preprocessor directives are not admitted");
    }

    [Test]
    public void Using_alias_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(StoragePath, "using System;", "using System;\nusing StackAlias = Nethermind.Evm.EvmStack;");
        AssertRejected(fixture, "Using aliases are not admitted");
    }

    [Test]
    public void Standard_build_source_selection_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(BuildTargetsPath, "'$(EnableZkEvm)' != 'true'", "'$(EnableZkEvm)' == 'false'");
        AssertRejected(fixture, "Unadmitted standard/zkEVM build-target content");
    }

    private static void AssertRejected(Fixture fixture, string message)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.Extract())!;
        Assert.That(exception.Message, Does.Contain(message));
    }

    private static ExtractionResult Extract(string root, string output) => MemoryControlOpcodeProfile.Extract(
        root,
        output,
        Path.Combine(output, "MemoryControlOpcodeKernel.lean"));

    private static byte[] ReadCheckedIrBytes() => File.ReadAllBytes(Path.Combine(
        RepoRoot(),
        "tools/Evm/Lean/MemoryControlOpcodeExtractor/Generated",
        MemoryControlOpcodeProfile.IrFileName));

    private static void Rename(JsonObject document, string oldName, string newName)
    {
        JsonNode? value = document[oldName];
        Assert.That(value, Is.Not.Null);
        document.Remove(oldName);
        document[newName] = value;
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, HandlersPath))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "memory-control-fixtures", Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            string production = RepoRoot();
            foreach (string source in MemoryControlOpcodeProfile.InputPaths)
                Write(source, File.ReadAllText(Path.Combine(production, source.Replace('/', Path.DirectorySeparatorChar))));
        }

        internal string Root { get; }

        internal string Output { get; }

        internal void Extract() => MemoryControlOpcodeProfile.Extract(
            Root, Output, Path.Combine(Output, "MemoryControlOpcodeKernel.lean"));

        internal void Replace(string relativePath, string before, string after)
        {
            string text = File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.That(text, Does.Contain(before), "Mutation target must exist.");
            Write(relativePath, text.Replace(before, after, StringComparison.Ordinal));
        }

        internal void InsertAtMethodBody(string relativePath, string methodName, string statement)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string source = File.ReadAllText(path);
            int method = source.IndexOf(methodName, StringComparison.Ordinal);
            Assert.That(method, Is.GreaterThanOrEqualTo(0));
            int body = source.IndexOf('{', method);
            Assert.That(body, Is.GreaterThan(method));
            Write(relativePath, source.Insert(body + 1, Environment.NewLine + "        " + statement));
        }

        internal void Write(string relativePath, string contents)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class TemporaryDirectory(string prefix) : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            TestContext.CurrentContext.WorkDirectory, prefix, Guid.NewGuid().ToString("N"));

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
