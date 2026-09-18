// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.PersistentStorageOpcodeExtractor.Test;

[TestFixture]
public class PersistentStorageOpcodeExtractorTests
{
    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new("persistent-storage-first");
        using TemporaryDirectory second = new("persistent-storage-second");
        ExtractionResult firstResult = PersistentStorageOpcodeProfile.Extract(root, first.Path, Path.Combine(first.Path, "Kernel.lean"));
        ExtractionResult secondResult = PersistentStorageOpcodeProfile.Extract(root, second.Path, Path.Combine(second.Path, "Kernel.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.SourceCount, Is.EqualTo(55));
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
        using TemporaryDirectory temporary = new("persistent-storage-checked");
        ExtractionResult result = PersistentStorageOpcodeProfile.Extract(root, temporary.Path, Path.Combine(temporary.Path, "Kernel.lean"));
        string checkedDirectory = Path.Combine(root, PersistentStorageOpcodeProfile.DefaultOutputRelativePath.Replace('/', Path.DirectorySeparatorChar));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(result.IrPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, PersistentStorageOpcodeProfile.IrFileName))));
            Assert.That(File.ReadAllBytes(result.ManifestPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, PersistentStorageOpcodeProfile.ManifestFileName))));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, "PersistentStorageOpcodeKernel.lean"))));
        }
    }

    [Test]
    public void Ir_names_closed_route_schedule_dependencies_and_obligations()
    {
        IrDocument ir = ReadCheckedIr();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ir.Opcodes.Select(static opcode => opcode.OpcodeByte).ToArray(), Is.EqualTo(new[] { 0x54, 0x55 }));
            Assert.That(ir.Specializations, Has.Length.EqualTo(8));
            Assert.That(ir.ExecutionMode.IsTracingAccess, Is.False);
            Assert.That(ir.ExecutionMode.Applicability, Does.Contain("Normal consensus execution only"));
            Assert.That(ir.Schedule, Is.EqualTo(new GasScheduleDescriptor(0, 2100, 100, 10000, 11616, 97920, 2300)));
            Assert.That(ir.ComposedGeneratedDependencies, Has.Some.Contains("SStorePricingKernel"));
            Assert.That(ir.ComposedGeneratedDependencies, Has.Some.Contains("StateGasChargeKernel"));
            Assert.That(ir.OpenExtractionObligations, Has.Some.Contains("IWorldState"));
            Assert.That(ir.OpenExtractionObligations, Has.Some.Contains("transaction refund cap"));
        }
    }

    [Test]
    public void Generated_Lean_is_theorem_free_and_independent()
    {
        string root = FindRepoRoot();
        string generated = File.ReadAllText(Path.Combine(
            root,
            PersistentStorageOpcodeProfile.DefaultLeanRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Match(@"(?m)^\s*theorem\s"));
            Assert.That(generated, Does.Not.Match(@"(?m)^\s*axiom\s"));
            Assert.That(generated, Does.Not.Contain("Eip803x.Evm.PersistentStorage"));
            Assert.That(generated, Does.Not.Contain("Eip803x.SStore"));
            Assert.That(generated, Does.Contain("def executeSLoad"));
            Assert.That(generated, Does.Contain("def executeSStore"));
            Assert.That(generated, Does.Contain("def consensusIsTracingAccess : Bool :=\n  false"));
            Assert.That(generated, Does.Contain("def openExtractionObligations : List String"));
            Assert.That(generated, Does.Contain("def dispatchSpecializations"));
        }
    }

    [Test]
    public void Dispatch_specializations_are_complete_unique_and_concrete()
    {
        DispatchSpecialization[] specializations = ReadCheckedIr().Specializations;
        (string Opcode, string Table)[] keys = specializations
            .Select(static specialization => (specialization.Opcode, specialization.Table))
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys.Distinct().ToArray(), Has.Length.EqualTo(8));
            Assert.That(keys.Count(static key => key.Opcode == "sload"), Is.EqualTo(4));
            Assert.That(keys.Count(static key => key.Opcode == "sstore"), Is.EqualTo(4));
            Assert.That(specializations.All(static specialization =>
                !specialization.ClosedRoot.Contains("/", StringComparison.Ordinal)), Is.True);
            Assert.That(specializations.Single(static specialization =>
                specialization.Opcode == "sstore" && specialization.Table == "TracedCancelable").ClosedRoot,
                Does.Contain("SStoreMeteredOpcode<OnFlag,OnFlag,OnFlag,Eip8038On,OnFlag>"));
        }
    }

    [TestCase(PersistentStorageOpcodeProfile.Eip8038ConstantsPath,
        "public const ulong StorageWrite = 10000;", "public const ulong StorageWrite = 9999;",
        "STORAGE_WRITE must remain 10000")]
    [TestCase(PersistentStorageOpcodeProfile.StorageInstructionsPath,
        "if (vmState.IsStatic) goto StaticCallViolation;", "if (false) goto StaticCallViolation;",
        "Ordered production fragment 'if(vmState.IsStatic)gotoStaticCallViolation'")]
    [TestCase(PersistentStorageOpcodeProfile.StorageInstructionsPath,
        "if (Eip8038.IsActive && TEip8037.IsActive)", "if (Eip8038.IsActive && false)",
        "Ordered production fragment 'if(Eip8038.IsActive&&TEip8037.IsActive)'")]
    [TestCase(PersistentStorageOpcodeProfile.StorageInstructionsPath,
        "vm.IsTracingAccess, in storageCell, StorageAccessType.SLOAD",
        "false, in storageCell, StorageAccessType.SLOAD",
        "vm.IsTracingAccess")]
    [TestCase("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
        "spec.IsEip8037Enabled = true;", "spec.IsEip8037Enabled = false;",
        "Amsterdam must activate EIP-8037")]
    [TestCase("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
        "NamedReleaseSpec<Amsterdam>(BPO2.Instance)", "NamedReleaseSpec<Amsterdam>(BPO1.Instance)",
        "Amsterdam must inherit BPO2")]
    [TestCase(PersistentStorageOpcodeProfile.MainnetDiPath,
        ".AddScoped<IVirtualMachine, EthereumVirtualMachine>()",
        ".AddScoped<IVirtualMachine, VirtualMachine>()",
        "mainnet DI must register EthereumVirtualMachine")]
    [TestCase(PersistentStorageOpcodeProfile.NamedReleaseSpecPath,
        "public static NamedReleaseSpec Instance { get; } = new TSelf();",
        "public static NamedReleaseSpec Instance { get; } = null!;",
        "NamedReleaseSpec<TSelf>.Instance must construct the exact closed fork type")]
    [TestCase(PersistentStorageOpcodeProfile.BuildTargetsPath,
        "<Compile Remove=\"**/std/**/*.cs\" />",
        "<Compile Remove=\"**/zkevm/**/*.cs\" />",
        "exact mutually exclusive zkEVM Compile/None directory and suffix selection pairs")]
    [TestCase(PersistentStorageOpcodeProfile.BuildTargetsPath,
        "<None Include=\"**/*.std.cs\" />",
        "<None Include=\"**/*.zkevm.cs\" />",
        "exact mutually exclusive zkEVM Compile/None directory and suffix selection pairs")]
    [TestCase(PersistentStorageOpcodeProfile.BuildTargetsPath,
        "<Compile Remove=\"**/zkevm/**/*.cs\" />",
        "<Compile Remove=\"**/std/**/*.cs\" />",
        "exact mutually exclusive standard Compile/None directory and suffix selection pairs")]
    [TestCase(PersistentStorageOpcodeProfile.BuildTargetsPath,
        "<None Include=\"**/*.zkevm.cs\" />",
        "<None Include=\"**/*.std.cs\" />",
        "exact mutually exclusive standard Compile/None directory and suffix selection pairs")]
    public void Semantic_source_mutation_is_rejected(
        string relativePath,
        string original,
        string replacement,
        string expected)
    {
        using Fixture fixture = new();
        fixture.Replace(relativePath, original, replacement);

        AssertRejected(fixture, expected);
    }

    [Test]
    public void Reachable_sload_early_return_is_rejected()
    {
        using Fixture fixture = new();
        fixture.InsertAtMethodBody(
            PersistentStorageOpcodeProfile.StorageInstructionsPath,
            "internal static EvmExceptionType InstructionSLoad",
            "return EvmExceptionType.None;");

        AssertRejected(fixture, "SLOAD has an early return before its base charge");
    }

    [Test]
    public void Source_only_dead_branch_is_rejected_by_complete_admission()
    {
        using Fixture fixture = new();
        fixture.InsertAtMethodBody(
            PersistentStorageOpcodeProfile.StorageInstructionsPath,
            "internal static EvmExceptionType InstructionSStoreMetered",
            "if (false) return EvmExceptionType.None;");

        AssertFingerprintRejected(fixture, PersistentStorageOpcodeProfile.StorageInstructionsPath);
    }

    [Test]
    public void Competing_sstore_dispatch_assignment_is_rejected()
    {
        const string assignment = "lookup[(int)Instruction.SSTORE] = SStoreOpcodeHandler<TTracingInst, TCancelable, Eip8038, Eip2929>(spec);";
        using Fixture fixture = new();
        fixture.Replace(PersistentStorageOpcodeProfile.HandlersPath, assignment,
            $"{assignment}{Environment.NewLine}        {assignment}");

        AssertRejected(fixture, "Instruction.SSTORE must have exactly one live dispatch assignment; found 2");
    }

    [Test]
    public void Duplicate_opcode_byte_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(PersistentStorageOpcodeProfile.InstructionPath, "SSTORE = 0x55,", "SSTORE = 0x54,");

        AssertRejected(fixture, "Instruction.SSTORE duplicates opcode byte 84 owned by SLOAD");
    }

    [Test]
    public void Out_of_range_opcode_byte_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(PersistentStorageOpcodeProfile.InstructionPath, "SSTORE = 0x55,", "SSTORE = 0x100,");

        AssertRejected(fixture, "Instruction.SSTORE byte 256 is outside the EVM byte range");
    }

    [Test]
    public void Premature_warming_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(PersistentStorageOpcodeProfile.EthereumGasPolicyPath,
            "if (!UpdateGas(ref gas, TMode.IsEip8038Enabled(spec) ? Eip8038Constants.ColdStorageAccess : GasCostOf.ColdSLoad))",
            $"accessTracker.WarmUp(in storageCell);{Environment.NewLine}            if (!UpdateGas(ref gas, TMode.IsEip8038Enabled(spec) ? Eip8038Constants.ColdStorageAccess : GasCostOf.ColdSLoad))");

        AssertFingerprintRejected(fixture, PersistentStorageOpcodeProfile.EthereumGasPolicyPath);
    }

    [Test]
    public void State_before_execution_charging_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(PersistentStorageOpcodeProfile.EthereumGasPolicyPath,
            "(executionGasCost <= 0 || UpdateGas(ref gas, executionGasCost)) &&\n        (stateGasCost <= 0 || TryConsumeStateGas(ref gas, stateGasCost))",
            "(stateGasCost <= 0 || TryConsumeStateGas(ref gas, stateGasCost)) &&\n        (executionGasCost <= 0 || UpdateGas(ref gas, executionGasCost))");

        AssertRejected(fixture, "Ordered production fragment 'TryConsumeStateGas(refgas,stateGasCost)' is missing or moved");
    }

    [Test]
    public void Write_before_final_refund_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Replace(PersistentStorageOpcodeProfile.StorageInstructionsPath,
            "ApplySStoreRefund(vm, vmState, pricing.RestoreOriginalRefund);",
            "vm.WorldState.Set(in storageCell, bytes.ToArray()); ApplySStoreRefund(vm, vmState, pricing.RestoreOriginalRefund);");

        AssertRejected(fixture, "SSTORE must contain exactly one persistent write; found 2");
    }

    [Test]
    public void Arbitrary_semantic_ir_mutation_is_rejected()
    {
        IrDocument ir = ReadCheckedIr();

        AssertIrRejected(ir with { Schedule = ir.Schedule with { WarmAccess = 101 } }, "unsupported Amsterdam persistent-storage schedule");
    }

    [Test]
    public void Access_tracing_mode_mutation_is_rejected()
    {
        IrDocument ir = ReadCheckedIr();

        AssertIrRejected(
            ir with { ExecutionMode = ir.ExecutionMode with { IsTracingAccess = true } },
            "unsupported persistent-storage execution mode");
    }

    [Test]
    public void Same_length_open_obligation_mutation_is_rejected()
    {
        IrDocument ir = ReadCheckedIr();
        string[] obligations = [.. ir.OpenExtractionObligations];
        obligations[0] = "arbitrary same-length trust boundary";

        AssertIrRejected(
            ir with { OpenExtractionObligations = obligations },
            "dependency or trust-boundary declarations");
    }

    [Test]
    public void Serialized_ir_unknown_member_is_rejected()
    {
        string json = Encoding.UTF8.GetString(ReadCheckedIrBytes());
        int closingBrace = json.LastIndexOf('}');
        Assert.That(closingBrace, Is.GreaterThan(0));
        byte[] mutated = Encoding.UTF8.GetBytes(json.Insert(closingBrace, ",\n  \"unknownMember\": true\n"));

        AssertSerializedIrRejected(mutated, "malformed or contains unknown members");
    }

    [TestCase("{")]
    [TestCase("null")]
    public void Malformed_or_null_serialized_ir_is_rejected(string json) =>
        AssertSerializedIrRejected(
            Encoding.UTF8.GetBytes(json),
            json == "null" ? "IR was null" : "IR is malformed");

    [TestCase("reachability")]
    [TestCase("executionMode")]
    [TestCase("schedule")]
    [TestCase("opcodes")]
    [TestCase("opcodeEntry")]
    [TestCase("orderedEffects")]
    [TestCase("orderedEffect")]
    [TestCase("specializations")]
    [TestCase("specializationEntry")]
    [TestCase("closedRoot")]
    [TestCase("composedGeneratedDependencies")]
    [TestCase("composedGeneratedDependency")]
    [TestCase("openExtractionObligations")]
    [TestCase("openExtractionObligation")]
    public void Nested_null_serialized_ir_is_rejected(string mutation)
    {
        JsonObject document = JsonNode.Parse(ReadCheckedIrBytes())!.AsObject();
        switch (mutation)
        {
            case "reachability":
                document["reachability"] = null;
                break;
            case "executionMode":
                document["executionMode"] = null;
                break;
            case "schedule":
                document["schedule"] = null;
                break;
            case "opcodes":
                document["opcodes"] = null;
                break;
            case "opcodeEntry":
                document["opcodes"]!.AsArray()[0] = null;
                break;
            case "orderedEffects":
                document["opcodes"]!.AsArray()[0]!["orderedEffects"] = null;
                break;
            case "orderedEffect":
                document["opcodes"]!.AsArray()[0]!["orderedEffects"]!.AsArray()[0] = null;
                break;
            case "specializations":
                document["specializations"] = null;
                break;
            case "specializationEntry":
                document["specializations"]!.AsArray()[0] = null;
                break;
            case "closedRoot":
                document["specializations"]!.AsArray()[0]!["closedRoot"] = null;
                break;
            case "composedGeneratedDependencies":
                document["composedGeneratedDependencies"] = null;
                break;
            case "composedGeneratedDependency":
                document["composedGeneratedDependencies"]!.AsArray()[0] = null;
                break;
            case "openExtractionObligations":
                document["openExtractionObligations"] = null;
                break;
            case "openExtractionObligation":
                document["openExtractionObligations"]!.AsArray()[0] = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        AssertSerializedIrRejected(
            Encoding.UTF8.GetBytes(document.ToJsonString()),
            "contains null collections, entries, or fields");
    }

    [Test]
    public void Serialized_ir_rejects_case_aliases_and_omitted_required_default_values()
    {
        byte[] bytes = ReadCheckedIrBytes();

        JsonObject topCase = ParseObject(bytes);
        Rename(topCase, "schemaVersion", "SchemaVersion");
        AssertSerializedIrRejected(Encoding.UTF8.GetBytes(topCase.ToJsonString()), "malformed or contains unknown members");
        JsonObject nestedCase = ParseObject(bytes);
        Rename(nestedCase["executionMode"]!.AsObject(), "isTracingAccess", "IsTracingAccess");
        AssertSerializedIrRejected(Encoding.UTF8.GetBytes(nestedCase.ToJsonString()), "malformed or contains unknown members");

        JsonObject omittedTop = ParseObject(bytes);
        omittedTop.Remove("schemaVersion");
        AssertSerializedIrRejected(Encoding.UTF8.GetBytes(omittedTop.ToJsonString()), "malformed or contains unknown members");
        JsonObject omittedZero = ParseObject(bytes);
        omittedZero["schedule"]!.AsObject().Remove("sloadBase");
        AssertSerializedIrRejected(Encoding.UTF8.GetBytes(omittedZero.ToJsonString()), "malformed or contains unknown members");
        JsonObject omittedFalse = ParseObject(bytes);
        omittedFalse["executionMode"]!.AsObject().Remove("isTracingAccess");
        AssertSerializedIrRejected(Encoding.UTF8.GetBytes(omittedFalse.ToJsonString()), "malformed or contains unknown members");
    }

    [Test]
    public void Serialized_ir_rejects_null_input_and_every_semantic_field_mutation()
    {
        Assert.That(() => PersistentStorageOpcodeProfile.DeserializeIr(null!), Throws.TypeOf<ExtractionException>());
        byte[] bytes = ReadCheckedIrBytes();
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
            root => root["reachability"]!["buildSelection"] = "changed",
            root => root["reachability"]!["dispatchRoot"] = "changed",
            root => root["executionMode"]!["isTracingAccess"] = true,
            root => root["executionMode"]!["applicability"] = "changed",
            root => root["schedule"]!["sloadBase"] = 1,
            root => root["schedule"]!["coldStorageAccess"] = 1,
            root => root["schedule"]!["warmAccess"] = 1,
            root => root["schedule"]!["storageWrite"] = 1,
            root => root["schedule"]!["storageClearRefund"] = 1,
            root => root["schedule"]!["storageSetStateGas"] = 1,
            root => root["schedule"]!["sstoreStipend"] = 1,
            root => root["opcodes"]!.AsArray()[0]!["name"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["instruction"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["opcodeByte"] = 1,
            root => root["opcodes"]!.AsArray()[0]!["handler"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["wrapper"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["stackInputs"] = 0,
            root => root["opcodes"]!.AsArray()[0]!["stackOutputs"] = 0,
            root => root["opcodes"]!.AsArray()[0]!["activation"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["orderedEffects"]!.AsArray()[0] = "changed",
            root => root["specializations"]!.AsArray()[0]!["opcode"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["table"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["tracingFlag"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["cancellationFlag"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["closedRoot"] = "changed",
            root => root["composedGeneratedDependencies"]!.AsArray()[0] = "changed",
            root => root["openExtractionObligations"]!.AsArray()[0] = "changed",
        ];
        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject document = ParseObject(bytes);
            mutate(document);
            Assert.That(() => PersistentStorageOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(document.ToJsonString())),
                Throws.TypeOf<ExtractionException>());
        }

        JsonObject duplicate = ParseObject(bytes);
        duplicate["specializations"]!.AsArray()[1] = duplicate["specializations"]!.AsArray()[0]!.DeepClone();
        Assert.That(() => PersistentStorageOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(duplicate.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Serialized_manifest_strictly_binds_sources_artifacts_and_semantics()
    {
        byte[] bytes = ReadCheckedManifestBytes();
        SourceManifest expected = PersistentStorageOpcodeProfile.DeserializeManifest(bytes);

        JsonObject unknown = ParseObject(bytes);
        unknown["unknown"] = true;
        AssertManifestJsonRejected(unknown);
        Assert.That(() => PersistentStorageOpcodeProfile.DeserializeManifest(null!), Throws.TypeOf<ExtractionException>());
        Assert.That(() => PersistentStorageOpcodeProfile.DeserializeManifest("{"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => PersistentStorageOpcodeProfile.DeserializeManifest("null"u8.ToArray()), Throws.TypeOf<ExtractionException>());

        JsonObject topCase = ParseObject(bytes);
        Rename(topCase, "schemaVersion", "SchemaVersion");
        AssertManifestJsonRejected(topCase);
        JsonObject nestedCase = ParseObject(bytes);
        Rename(nestedCase["sources"]!.AsArray()[0]!.AsObject(), "path", "Path");
        AssertManifestJsonRejected(nestedCase);
        JsonObject omitted = ParseObject(bytes);
        omitted.Remove("schemaVersion");
        AssertManifestJsonRejected(omitted);

        Action<JsonObject>[] mutations =
        [
            root => root["schemaVersion"] = 2,
            root => root["extractorVersion"] = "changed",
            root => root["compilerVersion"] = "changed",
            root => root["languageVersion"] = "changed",
            root => root["kernel"] = "changed",
            root => root["sources"]!.AsArray()[0]!["path"] = "changed",
            root => root["sources"]!.AsArray()[0]!["sha256"] = new string('0', 64),
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
            AssertManifestLineageRejected(document, expected);
        }

        foreach (Action<JsonObject> mutate in new Action<JsonObject>[]
        {
            root => root["sources"]!.AsArray()[0] = null,
            root => root["ir"] = null,
            root => root["lean"] = null,
            root => root["semanticBindings"]!.AsArray()[0] = null,
        })
        {
            JsonObject document = ParseObject(bytes);
            mutate(document);
            AssertManifestJsonRejected(document);
        }

        JsonObject duplicateSource = ParseObject(bytes);
        duplicateSource["sources"]!.AsArray()[1] = duplicateSource["sources"]!.AsArray()[0]!.DeepClone();
        AssertManifestJsonRejected(duplicateSource);
    }

    [Test]
    public void Duplicate_dispatch_key_is_rejected()
    {
        IrDocument ir = ReadCheckedIr();
        DispatchSpecialization[] specializations = [.. ir.Specializations];
        specializations[^1] = specializations[0];

        AssertIrRejected(ir with { Specializations = specializations }, "duplicates dispatch key sload/NoTrace");
    }

    [Test]
    public void Missing_dispatch_specialization_is_rejected()
    {
        IrDocument ir = ReadCheckedIr();

        AssertIrRejected(ir with { Specializations = ir.Specializations[..^1] }, "unsupported shape");
    }

    [Test]
    public void Unbound_dispatch_key_is_rejected()
    {
        IrDocument ir = ReadCheckedIr();
        DispatchSpecialization[] specializations = [.. ir.Specializations];
        specializations[0] = specializations[0] with { Table = "Unbound" };

        AssertIrRejected(ir with { Specializations = specializations }, "eight exact SLOAD/SSTORE dispatch specializations");
    }

    [Test]
    public void Placeholder_dispatch_root_is_rejected()
    {
        IrDocument ir = ReadCheckedIr();
        DispatchSpecialization[] specializations = [.. ir.Specializations];
        specializations[0] = specializations[0] with { ClosedRoot = "SLoadOpcode/SStoreOpcode" };

        AssertIrRejected(ir with { Specializations = specializations }, "placeholder dispatch root");
    }

    [Test]
    public void Forged_generated_artifact_cannot_mask_source_change()
    {
        using Fixture fixture = new();
        fixture.Replace(PersistentStorageOpcodeProfile.WorldStateInterfacePath,
            "ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell);",
            "ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell); // changed");
        Directory.CreateDirectory(Path.Combine(fixture.Root, PersistentStorageOpcodeProfile.DefaultOutputRelativePath));
        File.WriteAllText(Path.Combine(fixture.Root, PersistentStorageOpcodeProfile.DefaultLeanRelativePath),
            "-- forged", Encoding.UTF8);

        AssertFingerprintRejected(fixture, PersistentStorageOpcodeProfile.WorldStateInterfacePath);
    }

    private static IrDocument ReadCheckedIr() =>
        PersistentStorageOpcodeProfile.DeserializeIr(ReadCheckedIrBytes());

    private static byte[] ReadCheckedIrBytes() => ReadCheckedArtifact(PersistentStorageOpcodeProfile.IrFileName);

    private static byte[] ReadCheckedManifestBytes() => ReadCheckedArtifact(PersistentStorageOpcodeProfile.ManifestFileName);

    private static byte[] ReadCheckedArtifact(string name)
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root,
            PersistentStorageOpcodeProfile.DefaultOutputRelativePath.Replace('/', Path.DirectorySeparatorChar),
            name);
        return File.ReadAllBytes(path);
    }

    private static void AssertIrRejected(IrDocument document, string expected)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(() => LeanEmitter.Emit(document, "mutation"))!;
        Assert.That(exception.Message, Does.Contain(expected));
    }

    private static void AssertSerializedIrRejected(byte[] json, string expected)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => PersistentStorageOpcodeProfile.DeserializeIr(json))!;
        Assert.That(exception.Message, Does.Contain(expected));
    }

    private static JsonObject ParseObject(byte[] bytes) => JsonNode.Parse(bytes)!.AsObject();

    private static void Rename(JsonObject owner, string before, string after)
    {
        JsonNode value = owner[before]!.DeepClone();
        owner.Remove(before);
        owner[after] = value;
    }

    private static void AssertManifestJsonRejected(JsonObject document) => Assert.That(
        () => PersistentStorageOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(document.ToJsonString())),
        Throws.TypeOf<ExtractionException>());

    private static void AssertManifestLineageRejected(JsonObject document, SourceManifest expected) => Assert.That(
        () => PersistentStorageOpcodeProfile.ValidateManifest(
            PersistentStorageOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(document.ToJsonString())), expected),
        Throws.TypeOf<ExtractionException>());

    private static void AssertFingerprintRejected(Fixture fixture, string path) =>
        AssertRejected(fixture, $"Complete-source admission fingerprint changed for {path}");

    private static void AssertRejected(Fixture fixture, string expected)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.Extract())!;
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
        private readonly TemporaryDirectory _temporary = new("persistent-storage-fixture");

        public Fixture()
        {
            Root = _temporary.Path;
            string sourceRoot = FindRepoRoot();
            foreach (string relativePath in PersistentStorageOpcodeProfile.SourcePaths)
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
            return PersistentStorageOpcodeProfile.Extract(Root, output, Path.Combine(output, "Kernel.lean"));
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
