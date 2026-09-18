// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.TransientStorageOpcodeExtractor.Test;

[TestFixture]
public class TransientStorageOpcodeExtractorTests
{
    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new("transient-storage-first");
        using TemporaryDirectory second = new("transient-storage-second");
        ExtractionResult firstResult = TransientStorageOpcodeProfile.Extract(root, first.Path, Path.Combine(first.Path, "Kernel.lean"));
        ExtractionResult secondResult = TransientStorageOpcodeProfile.Extract(root, second.Path, Path.Combine(second.Path, "Kernel.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.SourceCount, Is.EqualTo(TransientStorageOpcodeProfile.SourcePaths.Count));
            Assert.That(firstResult.SpecializationCount, Is.EqualTo(8));
            Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.IrPath)));
            Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.ManifestPath)));
            Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.LeanPath)));
        }
    }

    [Test]
    public void Checked_artifacts_match_fresh_extraction()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory temporary = new("transient-storage-checked");
        ExtractionResult result = TransientStorageOpcodeProfile.Extract(root, temporary.Path, Path.Combine(temporary.Path, "Kernel.lean"));
        string checkedDirectory = Path.Combine(root, TransientStorageOpcodeProfile.DefaultOutputRelativePath.Replace('/', Path.DirectorySeparatorChar));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(result.IrPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, TransientStorageOpcodeProfile.IrFileName))));
            Assert.That(File.ReadAllBytes(result.ManifestPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, TransientStorageOpcodeProfile.ManifestFileName))));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, "TransientStorageOpcodeKernel.lean"))));
        }
    }

    [Test]
    public void Ir_names_closed_route_and_remaining_obligations()
    {
        string root = FindRepoRoot();
        string generated = Path.Combine(root, TransientStorageOpcodeProfile.DefaultOutputRelativePath.Replace('/', Path.DirectorySeparatorChar));
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated, TransientStorageOpcodeProfile.IrFileName)));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated, TransientStorageOpcodeProfile.ManifestFileName)));

        JsonElement opcodes = ir.RootElement.GetProperty("opcodes");
        JsonElement specializations = ir.RootElement.GetProperty("specializations");
        string[] obligations = ir.RootElement.GetProperty("openExtractionObligations")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opcodes.GetArrayLength(), Is.EqualTo(2));
            Assert.That(opcodes[0].GetProperty("opcodeByte").GetInt32(), Is.EqualTo(0x5c));
            Assert.That(opcodes[1].GetProperty("opcodeByte").GetInt32(), Is.EqualTo(0x5d));
            Assert.That(specializations.GetArrayLength(), Is.EqualTo(8));
            Assert.That(manifest.RootElement.GetProperty("sources").GetArrayLength(), Is.EqualTo(TransientStorageOpcodeProfile.SourcePaths.Count));
            Assert.That(obligations, Has.Some.Contains("journaling"));
            Assert.That(obligations, Has.Some.Contains("transaction-boundary ResetTransient"));
            Assert.That(obligations, Has.Some.Contains("Tracer callback behavior"));
        }
    }

    [Test]
    public void Generated_Lean_is_theorem_free_and_independent()
    {
        string root = FindRepoRoot();
        string generated = File.ReadAllText(Path.Combine(
            root,
            TransientStorageOpcodeProfile.DefaultLeanRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Contain("theorem "));
            Assert.That(generated, Does.Not.Contain("axiom "));
            Assert.That(generated, Does.Not.Contain("Eip803x.Evm.TransientStorage"));
            Assert.That(generated, Does.Not.Contain("TransientStorageStack"));
            Assert.That(generated, Does.Contain("def executeTLoad"));
            Assert.That(generated, Does.Contain("def executeTStore"));
            Assert.That(generated, Does.Contain("def dispatchSpecializations"));
            Assert.That(generated, Does.Contain("TLoadOpcode<OnFlag>"));
            Assert.That(generated, Does.Not.Contain("TLoadOpcode/TStoreOpcode"));
        }
    }

    [Test]
    public void Ir_dispatch_specializations_are_complete_and_exact()
    {
        IrDocument ir = ReadCheckedIr();
        (string Opcode, string Table)[] keys = ir.Specializations
            .Select(static specialization => (specialization.Opcode, specialization.Table))
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys, Has.Length.EqualTo(8));
            Assert.That(keys.Distinct().ToArray(), Has.Length.EqualTo(8));
            Assert.That(keys.Count(static key => key.Opcode == "tload"), Is.EqualTo(4));
            Assert.That(keys.Count(static key => key.Opcode == "tstore"), Is.EqualTo(4));
            Assert.That(ir.Specializations.All(static specialization =>
                !specialization.ClosedRoot.Contains("/", StringComparison.Ordinal)), Is.True);
            Assert.That(ir.Specializations.Single(static specialization =>
                specialization.Opcode == "tload" && specialization.Table == "Traced").ClosedRoot,
                Does.Contain("TLoadOpcode<OnFlag>,OnFlag,OffFlag,OnFlag"));
        }
    }

    [Test]
    public void Arbitrary_semantic_ir_mutation_is_rejected_after_deserialization()
    {
        IrDocument ir = ReadCheckedIr();
        OpcodeDescriptor[] opcodes = [.. ir.Opcodes];
        opcodes[1] = opcodes[1] with { StaticCheckBeforeGas = false };

        AssertIrRejected(ir with { Opcodes = opcodes }, "unsupported tstore descriptor");
    }

    [Test]
    public void Arbitrary_dispatch_root_is_rejected_after_deserialization()
    {
        IrDocument ir = ReadCheckedIr();
        DispatchSpecialization[] specializations = [.. ir.Specializations];
        specializations[0] = specializations[0] with { ClosedRoot = "arbitrary" };

        AssertIrRejected(ir with { Specializations = specializations }, "eight exact TLOAD/TSTORE dispatch specializations");
    }

    [Test]
    public void Duplicate_dispatch_key_is_rejected_after_deserialization()
    {
        IrDocument ir = ReadCheckedIr();
        DispatchSpecialization[] specializations = [.. ir.Specializations];
        specializations[^1] = specializations[0];

        AssertIrRejected(ir with { Specializations = specializations }, "duplicates dispatch key tload/NoTrace");
    }

    [Test]
    public void Missing_dispatch_specialization_is_rejected_after_deserialization()
    {
        IrDocument ir = ReadCheckedIr();

        AssertIrRejected(ir with { Specializations = ir.Specializations[..^1] }, "unsupported shape");
    }

    [Test]
    public void Unbound_dispatch_key_is_rejected_after_deserialization()
    {
        IrDocument ir = ReadCheckedIr();
        DispatchSpecialization[] specializations = [.. ir.Specializations];
        specializations[0] = specializations[0] with { Table = "Unbound" };

        AssertIrRejected(ir with { Specializations = specializations }, "eight exact TLOAD/TSTORE dispatch specializations");
    }

    [Test]
    public void Placeholder_dispatch_root_is_rejected_after_deserialization()
    {
        IrDocument ir = ReadCheckedIr();
        DispatchSpecialization[] specializations = [.. ir.Specializations];
        specializations[0] = specializations[0] with { ClosedRoot = "TLoadOpcode/TStoreOpcode" };

        AssertIrRejected(ir with { Specializations = specializations }, "placeholder dispatch root");
    }

    [TestCase(
        TransientStorageOpcodeProfile.GasCostPath,
        "public const ulong WarmStateRead = 100;",
        "public const ulong WarmStateRead = 101;",
        "WarmStateRead must remain 100")]
    [TestCase(
        TransientStorageOpcodeProfile.StorageInstructionsPath,
        "if (vmState.IsStatic) goto StaticCallViolation;",
        "if (false) goto StaticCallViolation;",
        "Ordered production fragment 'if(vmState.IsStatic)gotoStaticCallViolation'")]
    [TestCase(
        TransientStorageOpcodeProfile.StorageInstructionsPath,
        "if (!TGasPolicy.UpdateGas<TLoadGasCost>(ref gas)) goto OutOfGas;",
        "if (!TGasPolicy.UpdateGas<TStoreGasCost>(ref gas)) goto OutOfGas;",
        "Ordered production fragment 'TGasPolicy.UpdateGas<TLoadGasCost>(refgas)'")]
    [TestCase(
        "src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs",
        "spec.IsEip1153Enabled = true;",
        "spec.IsEip1153Enabled = false;",
        "Cancun must activate EIP-1153")]
    [TestCase(
        "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
        "NamedReleaseSpec<Amsterdam>(BPO2.Instance)",
        "NamedReleaseSpec<Amsterdam>(BPO1.Instance)",
        "Amsterdam must inherit BPO2")]
    [TestCase(
        TransientStorageOpcodeProfile.MainnetDiPath,
        ".AddScoped<IVirtualMachine, EthereumVirtualMachine>()",
        ".AddScoped<IVirtualMachine, VirtualMachine>()",
        "mainnet block processing must register EthereumVirtualMachine")]
    public void Semantic_mutation_is_rejected(string relativePath, string original, string replacement, string expected)
    {
        using Fixture fixture = new();
        fixture.Replace(relativePath, original, replacement);

        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.Extract())!;
        Assert.That(exception.Message, Does.Contain(expected));
    }

    [Test]
    public void Reachable_early_return_is_rejected_by_complete_source_admission()
    {
        using Fixture fixture = new();
        fixture.InsertAtMethodBody(
            TransientStorageOpcodeProfile.StorageInstructionsPath,
            "public static EvmExceptionType InstructionTLoad",
            "return EvmExceptionType.None;");

        AssertRejected(fixture, "TLOAD has an early return before its gas charge");
    }

    [Test]
    public void Source_only_dead_branch_is_rejected_by_complete_source_admission()
    {
        using Fixture fixture = new();
        fixture.InsertAtMethodBody(
            TransientStorageOpcodeProfile.StorageInstructionsPath,
            "public static EvmExceptionType InstructionTStore",
            "if (false) return EvmExceptionType.None;");

        AssertFingerprintRejected(fixture, TransientStorageOpcodeProfile.StorageInstructionsPath);
    }

    [Test]
    public void Competing_dispatch_assignment_is_rejected()
    {
        const string assignment = "lookup[(int)Instruction.TLOAD] = OpcodeHandler<TLoadOpcode<TTracingInst>, TTracingInst, TCancelable>();";
        using Fixture fixture = new();
        fixture.Replace(TransientStorageOpcodeProfile.HandlersPath, assignment, $"{assignment}{Environment.NewLine}            {assignment}");

        AssertRejected(fixture, "Instruction.TLOAD must have exactly one live dispatch assignment; found 2");
    }

    [Test]
    public void Assignment_outside_activation_gate_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(TransientStorageOpcodeProfile.HandlersPath, "if (spec.TransientStorageEnabled)", "if (true)");

        AssertRejected(fixture, "Instruction.TLOAD must be guarded only by spec.TransientStorageEnabled");
    }

    [Test]
    public void Duplicate_opcode_byte_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(TransientStorageOpcodeProfile.InstructionPath, "TSTORE = 0x5d,", "TSTORE = 0x5c,");

        AssertRejected(fixture, "Instruction.TSTORE duplicates opcode byte 92 owned by TLOAD");
    }

    [Test]
    public void Out_of_range_opcode_byte_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(TransientStorageOpcodeProfile.InstructionPath, "TSTORE = 0x5d,", "TSTORE = 0x100,");

        AssertRejected(fixture, "Instruction.TSTORE byte 256 is outside the EVM byte range");
    }

    [Test]
    public void Post_write_trace_read_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(
            TransientStorageOpcodeProfile.StorageInstructionsPath,
            "? vm.WorldState.GetTransientState(in storageCell).ToArray()",
            "? []");
        fixture.Replace(
            TransientStorageOpcodeProfile.StorageInstructionsPath,
            "vm.TxTracer.SetOperationTransientStorage(storageCell.Address, result, bytes, currentValue);",
            $"currentValue = vm.WorldState.GetTransientState(in storageCell).ToArray();{Environment.NewLine}            vm.TxTracer.SetOperationTransientStorage(storageCell.Address, result, bytes, currentValue);");

        AssertRejected(fixture, "Ordered production fragment 'vm.WorldState.SetTransientState(instorageCell' is missing or moved");
    }

    [Test]
    public void Generated_artifact_cannot_mask_a_source_mutation()
    {
        using Fixture fixture = new();
        fixture.Replace(
            TransientStorageOpcodeProfile.TransactionProcessorPath,
            "WorldState.ResetTransient();",
            "WorldState.ResetTransient(); WorldState.ResetTransient();");
        Directory.CreateDirectory(Path.Combine(fixture.Root, TransientStorageOpcodeProfile.DefaultOutputRelativePath));
        File.WriteAllText(
            Path.Combine(fixture.Root, TransientStorageOpcodeProfile.DefaultLeanRelativePath),
            "-- forged generated artifact",
            Encoding.UTF8);

        AssertFingerprintRejected(fixture, TransientStorageOpcodeProfile.TransactionProcessorPath);
    }

    [Test]
    public void Serialized_ir_rejects_unknown_malformed_top_and_nested_null()
    {
        byte[] bytes = ReadCheckedBytes(TransientStorageOpcodeProfile.IrFileName);
        JsonObject unknown = ParseObject(bytes);
        unknown["unknown"] = true;
        AssertIrJsonRejected(unknown);
        Assert.That(() => TransientStorageOpcodeProfile.DeserializeIr("{"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => TransientStorageOpcodeProfile.DeserializeIr("null"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => TransientStorageOpcodeProfile.DeserializeIr(null!), Throws.TypeOf<ExtractionException>());

        foreach (Action<JsonObject> mutation in new Action<JsonObject>[]
        {
            root => root["reachability"] = null,
            root => root["opcodes"] = null,
            root => root["opcodes"]!.AsArray()[0] = null,
            root => root["opcodes"]!.AsArray()[0]!["orderedEffects"] = null,
            root => root["opcodes"]!.AsArray()[0]!["orderedEffects"]!.AsArray()[0] = null,
            root => root["specializations"] = null,
            root => root["specializations"]!.AsArray()[0] = null,
            root => root["openExtractionObligations"] = null,
            root => root["openExtractionObligations"]!.AsArray()[0] = null,
        })
        {
            JsonObject document = ParseObject(bytes);
            mutation(document);
            AssertIrJsonRejected(document);
        }
    }

    [Test]
    public void Serialized_ir_rejects_case_aliases_and_omitted_required_default_fields()
    {
        byte[] bytes = ReadCheckedBytes(TransientStorageOpcodeProfile.IrFileName);
        JsonObject topCase = ParseObject(bytes);
        Rename(topCase, "schemaVersion", "SchemaVersion");
        AssertIrJsonRejected(topCase);
        JsonObject nestedCase = ParseObject(bytes);
        Rename(nestedCase["opcodes"]!.AsArray()[0]!.AsObject(), "name", "Name");
        AssertIrJsonRejected(nestedCase);

        JsonObject omittedTop = ParseObject(bytes);
        omittedTop.Remove("schemaVersion");
        AssertIrJsonRejected(omittedTop);
        JsonObject omittedFalse = ParseObject(bytes);
        omittedFalse["opcodes"]!.AsArray()[0]!.AsObject().Remove("staticCheckBeforeGas");
        AssertIrJsonRejected(omittedFalse);
        JsonObject omittedZero = ParseObject(bytes);
        omittedZero["opcodes"]!.AsArray()[1]!.AsObject().Remove("stackOutputs");
        AssertIrJsonRejected(omittedZero);
    }

    [Test]
    public void Every_serialized_ir_field_is_validated()
    {
        byte[] bytes = ReadCheckedBytes(TransientStorageOpcodeProfile.IrFileName);
        Action<JsonObject>[] mutations =
        [
            root => root["schemaVersion"] = 2,
            root => root["extractorVersion"] = "changed",
            root => root["kernel"] = "changed",
            root => root["reachability"]!["service"] = "changed",
            root => root["reachability"]!["concreteVm"] = "changed",
            root => root["reachability"]!["closedVm"] = "changed",
            root => root["reachability"]!["gasPolicy"] = "changed",
            root => root["reachability"]!["fork"] = "changed",
            root => root["reachability"]!["activation"] = "changed",
            root => root["reachability"]!["buildSelection"] = "changed",
            root => root["reachability"]!["dispatchRoot"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["name"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["instruction"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["opcodeByte"] = 1,
            root => root["opcodes"]!.AsArray()[0]!["handler"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["wrapper"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["fixedGas"] = 1,
            root => root["opcodes"]!.AsArray()[0]!["stackInputs"] = 0,
            root => root["opcodes"]!.AsArray()[0]!["stackOutputs"] = 0,
            root => root["opcodes"]!.AsArray()[0]!["staticCheckBeforeGas"] = true,
            root => root["opcodes"]!.AsArray()[0]!["orderedEffects"]!.AsArray()[0] = "changed",
            root =>
            {
                JsonArray opcodes = root["opcodes"]!.AsArray();
                JsonNode first = opcodes[0]!.DeepClone();
                opcodes[0] = opcodes[1]!.DeepClone();
                opcodes[1] = first;
            },
            root => root["specializations"]!.AsArray()[0]!["opcode"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["table"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["tracingFlag"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["cancellationFlag"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["closedRoot"] = "changed",
            root => root["openExtractionObligations"]!.AsArray()[0] = "changed",
        ];
        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject document = ParseObject(bytes);
            mutate(document);
            AssertIrJsonRejected(document);
        }
    }

    [Test]
    public void Serialized_manifest_strictly_binds_sources_admissions_artifacts_and_semantics()
    {
        byte[] bytes = ReadCheckedBytes(TransientStorageOpcodeProfile.ManifestFileName);
        SourceManifest expected = TransientStorageOpcodeProfile.DeserializeManifest(bytes);

        JsonObject unknown = ParseObject(bytes);
        unknown["unknown"] = true;
        AssertManifestJsonRejected(unknown);
        Assert.That(() => TransientStorageOpcodeProfile.DeserializeManifest("{"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => TransientStorageOpcodeProfile.DeserializeManifest("null"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => TransientStorageOpcodeProfile.DeserializeManifest(null!), Throws.TypeOf<ExtractionException>());

        Action<JsonObject>[] mutations =
        [
            root => root["schemaVersion"] = 2,
            root => root["extractorVersion"] = "changed",
            root => root["compilerVersion"] = "changed",
            root => root["languageVersion"] = "changed",
            root => root["kernel"] = "changed",
            root => root["sources"]!.AsArray()[0]!["path"] = "changed",
            root => root["sources"]!.AsArray()[0]!["sha256"] = new string('0', 64),
            root => root["admissions"]!.AsArray()[0]!["key"] = "changed",
            root => root["admissions"]!.AsArray()[0]!["sourceSha256"] = new string('0', 64),
            root => root["ir"]!["path"] = "changed",
            root => root["ir"]!["sha256"] = new string('0', 64),
            root => root["lean"]!["path"] = "changed",
            root => root["lean"]!["sha256"] = new string('0', 64),
            root => root["combinedSha256"] = new string('0', 64),
            root => root["semanticBindings"]!.AsArray()[0] = "changed",
        ];
        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject document = ParseObject(bytes);
            mutate(document);
            Assert.That(() => TransientStorageOpcodeProfile.ValidateManifest(
                    TransientStorageOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(document.ToJsonString())), expected),
                Throws.TypeOf<ExtractionException>());
        }

        foreach (Action<JsonObject> mutation in new Action<JsonObject>[]
        {
            root => root["sources"]!.AsArray()[0] = null,
            root => root["admissions"]!.AsArray()[0] = null,
            root => root["ir"] = null,
            root => root["semanticBindings"]!.AsArray()[0] = null,
        })
        {
            JsonObject document = ParseObject(bytes);
            mutation(document);
            AssertManifestJsonRejected(document);
        }

        JsonObject duplicate = ParseObject(bytes);
        duplicate["admissions"]!.AsArray()[1] = duplicate["admissions"]!.AsArray()[0]!.DeepClone();
        AssertManifestJsonRejected(duplicate, "Duplicate owner-qualified transient-storage admission identity");
        JsonObject topCase = ParseObject(bytes);
        Rename(topCase, "schemaVersion", "SchemaVersion");
        AssertManifestJsonRejected(topCase);
        JsonObject nestedCase = ParseObject(bytes);
        Rename(nestedCase["admissions"]!.AsArray()[0]!.AsObject(), "key", "Key");
        AssertManifestJsonRejected(nestedCase);
        JsonObject omitted = ParseObject(bytes);
        omitted.Remove("schemaVersion");
        AssertManifestJsonRejected(omitted);
    }

    private static void AssertFingerprintRejected(Fixture fixture, string path) =>
        AssertRejected(fixture, $"Complete-source admission fingerprint changed for {path}");

    private static void AssertRejected(Fixture fixture, string expected)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.Extract())!;
        Assert.That(exception.Message, Does.Contain(expected));
    }

    private static IrDocument ReadCheckedIr() =>
        TransientStorageOpcodeProfile.DeserializeIr(ReadCheckedBytes(TransientStorageOpcodeProfile.IrFileName));

    private static byte[] ReadCheckedBytes(string name) => File.ReadAllBytes(Path.Combine(
        FindRepoRoot(),
        TransientStorageOpcodeProfile.DefaultOutputRelativePath.Replace('/', Path.DirectorySeparatorChar),
        name));

    private static JsonObject ParseObject(byte[] bytes) => JsonNode.Parse(bytes)!.AsObject();

    private static void Rename(JsonObject owner, string before, string after)
    {
        JsonNode value = owner[before]!.DeepClone();
        owner.Remove(before);
        owner[after] = value;
    }

    private static void AssertIrJsonRejected(JsonObject document) => Assert.That(
        () => TransientStorageOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(document.ToJsonString())),
        Throws.TypeOf<ExtractionException>());

    private static void AssertManifestJsonRejected(JsonObject document, string? expected = null)
    {
        Action action = () => TransientStorageOpcodeProfile.DeserializeManifest(
            Encoding.UTF8.GetBytes(document.ToJsonString()));
        if (expected is null)
            Assert.That(action, Throws.TypeOf<ExtractionException>());
        else
            Assert.That(action, Throws.TypeOf<ExtractionException>().With.Message.Contains(expected));
    }

    private static void AssertIrRejected(IrDocument document, string expected)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(() => LeanEmitter.Emit(document, "mutation"))!;
        Assert.That(exception.Message, Does.Contain(expected));
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src/Nethermind/Nethermind.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot locate the Nethermind repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new("transient-storage-fixture");

        public Fixture()
        {
            Root = _temporary.Path;
            string sourceRoot = FindRepoRoot();
            foreach (string relativePath in TransientStorageOpcodeProfile.SourcePaths)
            {
                string source = Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                string destination = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
        }

        public string Root { get; }

        public void Replace(string relativePath, string original, string replacement)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string source = File.ReadAllText(path);
            Assert.That(source, Does.Contain(original), $"Mutation anchor was not found in {relativePath}.");
            File.WriteAllText(path, source.Replace(original, replacement, StringComparison.Ordinal), new UTF8Encoding(false));
        }

        public void InsertAtMethodBody(string relativePath, string signature, string statement)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string source = File.ReadAllText(path);
            int signaturePosition = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(signaturePosition, Is.GreaterThanOrEqualTo(0), $"Method anchor was not found in {relativePath}.");
            int body = source.IndexOf('{', signaturePosition);
            Assert.That(body, Is.GreaterThanOrEqualTo(0), $"Method body was not found in {relativePath}.");
            source = source.Insert(body + 1, $"{Environment.NewLine}        {statement}");
            File.WriteAllText(path, source, new UTF8Encoding(false));
        }

        public ExtractionResult Extract()
        {
            string output = Path.Combine(Root, "output");
            return TransientStorageOpcodeProfile.Extract(Root, output, Path.Combine(output, "Kernel.lean"));
        }

        public void Dispose() => _temporary.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string name)
        {
            string parent = System.IO.Path.Combine("D:\\tmp", "nethermind-formal-tests");
            Directory.CreateDirectory(parent);
            Path = System.IO.Path.Combine(parent, $"{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
