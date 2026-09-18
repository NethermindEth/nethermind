// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.LogOpcodeExtractor.Test;

[TestFixture]
public sealed class LogOpcodeExtractorTests
{
    private const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    private const string HandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    private const string InstructionLogPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Stack.cs";
    private const string GasCostPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    private const string GasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string MemoryPath = "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs";
    private const string VirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    private const string StandardVirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs";
    private const string NamedReleaseSpecPath = "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs";
    private const string AmsterdamPath = "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs";
    private const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";

    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = RepoRoot();
        using TemporaryDirectory first = new("log-opcode-first");
        using TemporaryDirectory second = new("log-opcode-second");

        ExtractionResult firstResult = Extract(root, first.Path);
        ExtractionResult secondResult = Extract(root, second.Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.IrPath)));
            Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.ManifestPath)));
            Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.LeanPath)));
        }
    }

    [Test]
    public void Checked_artifacts_match_fresh_extraction()
    {
        string root = RepoRoot();
        using TemporaryDirectory fresh = new("log-opcode-fresh");
        ExtractionResult result = Extract(root, fresh.Path);
        string checkedOutput = Path.Combine(root, "tools/Evm/Lean/LogOpcodeExtractor/Generated");
        string checkedLean = Path.Combine(root, LogOpcodeProfile.DefaultLeanRelativePath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(result.IrPath),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedOutput, LogOpcodeProfile.IrFileName))));
            Assert.That(File.ReadAllBytes(result.ManifestPath),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedOutput, LogOpcodeProfile.ManifestFileName))));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(File.ReadAllBytes(checkedLean)));
        }
    }

    [Test]
    public void Ir_binds_exact_descriptors_effects_and_closed_specializations()
    {
        string root = RepoRoot();
        using TemporaryDirectory output = new("log-opcode-ir");
        ExtractionResult result = Extract(root, output.Path);
        IrDocument document = LogOpcodeProfile.DeserializeIr(File.ReadAllBytes(result.IrPath));
        LogOpcodeProfile.ValidateIr(document);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.Opcodes.Select(static opcode => opcode.OpcodeByte),
                Is.EqualTo(new[] { 0xa0, 0xa1, 0xa2, 0xa3, 0xa4 }));
            Assert.That(document.Opcodes.Select(static opcode => opcode.TopicCount),
                Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
            Assert.That(document.Opcodes.Select(static opcode => opcode.HandlerBody), Is.EqualTo(new[]
            {
                "LogOpcode<EvmInstructions.Op0>", "LogOpcode<EvmInstructions.Op1>",
                "LogOpcode<EvmInstructions.Op2>", "LogOpcode<EvmInstructions.Op3>",
                "LogOpcode<EvmInstructions.Op4>",
            }));
            Assert.That(document.Opcodes.All(static opcode => opcode.ProgramCounterDelta == 1 &&
                opcode.HeaderStackInputs == 2 && opcode.EffectOrder.Count == 17), Is.True);
            Assert.That(document.Specializations, Has.Count.EqualTo(20));
            Assert.That(document.Specializations.Select(static specialization =>
                (specialization.Opcode, specialization.DispatchTable)).Distinct().Count(), Is.EqualTo(20));
            Assert.That(document.Specializations.All(static specialization =>
                !specialization.ClosedRoot.Contains("TTracingInst", StringComparison.Ordinal) &&
                !specialization.ClosedRoot.Contains("TCancelable", StringComparison.Ordinal) &&
                !specialization.ClosedRoot.Contains("TGasPolicy", StringComparison.Ordinal) &&
                !specialization.ClosedRoot.Contains("TOpCount", StringComparison.Ordinal)), Is.True);
        }
    }

    [Test]
    public void Admission_keys_are_owner_qualified_unique_and_duplicates_are_rejected()
    {
        string root = RepoRoot();
        using TemporaryDirectory output = new("log-opcode-admissions");
        SourceManifest manifest = LogOpcodeProfile.DeserializeManifest(
            File.ReadAllBytes(Extract(root, output.Path).ManifestPath));
        IReadOnlyList<string> keys = manifest.Admissions.Select(static identity => identity.Key).ToList();
        AdmissionIdentity first = manifest.Admissions[0];

        ExtractionException duplicate = Assert.Throws<ExtractionException>(() =>
            LogOpcodeProfile.ValidateAdmissionIdentities([first, first]))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(manifest.Admissions, Has.Count.EqualTo(101));
            Assert.That(keys.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(keys.Count));
            Assert.That(keys, Does.Contain(
                "src/Nethermind/Nethermind.Evm/StackAccessTracker.cs:StackAccessTrackerLog:StackAccessTracker/0:property:Logs/0/0/"));
            Assert.That(keys, Does.Contain(
                "src/Nethermind/Nethermind.Evm/StackAccessTracker.cs:TrackingStateLog:TrackingState/0:property:Logs/0/0/"));
            Assert.That(duplicate.Message, Does.Contain("Duplicate owner/path-qualified syntax admission key"));
        }
    }

    [Test]
    public void Generated_Lean_is_theorem_free_independent_and_operational()
    {
        string root = RepoRoot();
        using TemporaryDirectory output = new("log-opcode-lean");
        ExtractionResult result = Extract(root, output.Path);
        string generated = File.ReadAllText(result.LeanPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Contain("theorem "));
            Assert.That(generated, Does.Not.Contain("Eip803x.Evm.Logs"));
            Assert.That(generated, Does.Contain("def executeCore"));
            Assert.That(generated, Does.Contain("def execute"));
            Assert.That(generated, Does.Contain("effectOrder := expectedEffectOrder"));
            Assert.That(generated, Does.Contain("reportedLogs := state.reportedLogs ++ [entry]"));
            Assert.That(generated, Does.Contain("logs := state.logs ++ [entry]"));
            Assert.That(generated, Does.Contain("pc := state.pc + (descriptor opcode).programCounterDelta"));
        }
    }

    [Test]
    public void Refinement_connects_the_generated_transition_to_logs_and_memory_gas()
    {
        string refinement = File.ReadAllText(Path.Combine(
            RepoRoot(), "tools/Evm/Lean/LogOpcodeExtractor/Refinement/LogOpcode.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refinement, Does.Contain("import Eip803x.Evm.Logs"));
            Assert.That(refinement, Does.Contain("import Eip803x.Evm.MemoryGas"));
            Assert.That(refinement, Does.Contain("theorem generated_execute_refines_reference"));
            Assert.That(refinement, Does.Contain("theorem generated_prepare_memory_refines_memory_gas"));
            Assert.That(refinement, Does.Contain("theorem logs_prepare_memory_refines_memory_gas"));
        }
    }

    [Test]
    public void Serialized_ir_rejects_malformed_and_unmapped_json()
    {
        string root = RepoRoot();
        using TemporaryDirectory output = new("log-opcode-json");
        ExtractionResult result = Extract(root, output.Path);
        string json = File.ReadAllText(result.IrPath);
        string withUnmappedMember = json.Insert(json.IndexOf('{') + 1, "\n  \"unexpected\": true,");

        ExtractionException unmapped = Assert.Throws<ExtractionException>(() =>
            LogOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(withUnmappedMember)))!;
        ExtractionException malformed = Assert.Throws<ExtractionException>(() =>
            LogOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes("{")))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unmapped.Message, Does.Contain("not valid JSON"));
            Assert.That(malformed.Message, Does.Contain("not valid JSON"));
        }
    }

    [Test]
    public void Serialized_ir_rejects_case_aliases_and_omitted_required_default_values()
    {
        byte[] bytes = ReadCheckedArtifact(LogOpcodeProfile.IrFileName);

        JsonObject topCase = ParseObject(bytes);
        Rename(topCase, "schemaVersion", "SchemaVersion");
        AssertSerializedIrRejected(topCase);
        JsonObject nestedCase = ParseObject(bytes);
        Rename(nestedCase["opcodes"]!.AsArray()[0]!.AsObject(), "topicCount", "TopicCount");
        AssertSerializedIrRejected(nestedCase);

        JsonObject omittedTop = ParseObject(bytes);
        omittedTop.Remove("schemaVersion");
        AssertSerializedIrRejected(omittedTop);
        JsonObject omittedZero = ParseObject(bytes);
        omittedZero["opcodes"]!.AsArray()[0]!.AsObject().Remove("topicCount");
        AssertSerializedIrRejected(omittedZero);
    }

    [Test]
    public void Serialized_ir_rejects_unknown_malformed_top_and_nested_null()
    {
        byte[] bytes = ReadCheckedArtifact(LogOpcodeProfile.IrFileName);
        Assert.That(() => LogOpcodeProfile.DeserializeIr(null!), Throws.TypeOf<ExtractionException>());
        JsonObject unknown = ParseObject(bytes);
        unknown["unknown"] = true;
        AssertSerializedIrRejected(unknown);
        Assert.That(() => LogOpcodeProfile.DeserializeIr("{"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => LogOpcodeProfile.DeserializeIr("null"u8.ToArray()), Throws.TypeOf<ExtractionException>());

        foreach (Action<JsonObject> mutate in new Action<JsonObject>[]
        {
            root => root["opcodes"] = null,
            root => root["opcodes"]!.AsArray()[0] = null,
            root => root["opcodes"]!.AsArray()[0]!["effectOrder"] = null,
            root => root["opcodes"]!.AsArray()[0]!["effectOrder"]!.AsArray()[0] = null,
            root => root["specializations"] = null,
            root => root["specializations"]!.AsArray()[0] = null,
            root => root["specializations"]!.AsArray()[0]!["closedRoot"] = null,
            root => root["forkLineage"] = null,
            root => root["forkLineage"]!.AsArray()[0] = null,
            root => root["openExtractionObligations"] = null,
            root => root["openExtractionObligations"]!.AsArray()[0] = null,
        })
        {
            JsonObject document = ParseObject(bytes);
            mutate(document);
            AssertSerializedIrRejected(document);
        }
    }

    [Test]
    public void Every_serialized_ir_field_is_validated()
    {
        byte[] bytes = ReadCheckedArtifact(LogOpcodeProfile.IrFileName);
        Action<JsonObject>[] mutations =
        [
            root => root["schemaVersion"] = 2,
            root => root["extractorVersion"] = "changed",
            root => root["root"] = "changed",
            root => root["gasPolicy"] = "changed",
            root => root["fork"] = "changed",
            root => root["logBaseGas"] = 1,
            root => root["logTopicGas"] = 1,
            root => root["logDataByteGas"] = 1,
            root => root["memoryLinearGas"] = 1,
            root => root["memoryQuadraticDivisor"] = 1,
            root => root["maxMemorySize"] = 1,
            root => root["opcodes"]!.AsArray()[0]!["name"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["instruction"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["opcodeByte"] = 0,
            root => root["opcodes"]!.AsArray()[0]!["topicCount"] = 1,
            root => root["opcodes"]!.AsArray()[0]!["handlerBody"] = "changed",
            root => root["opcodes"]!.AsArray()[0]!["programCounterDelta"] = 0,
            root => root["opcodes"]!.AsArray()[0]!["headerStackInputs"] = 0,
            root => root["opcodes"]!.AsArray()[0]!["effectOrder"]!.AsArray()[0] = "changed",
            root => root["specializations"]!.AsArray()[0]!["opcode"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["dispatchTable"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["tracingFlag"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["cancelableFlag"] = "changed",
            root => root["specializations"]!.AsArray()[0]!["closedRoot"] = "changed",
            root => root["forkLineage"]!.AsArray()[0] = "changed",
            root => root["openExtractionObligations"]!.AsArray()[0] = "changed",
        ];
        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject document = ParseObject(bytes);
            mutate(document);
            AssertSerializedIrRejected(document);
        }

        JsonObject duplicate = ParseObject(bytes);
        duplicate["specializations"]!.AsArray()[1] = duplicate["specializations"]!.AsArray()[0]!.DeepClone();
        AssertSerializedIrRejected(duplicate);
    }

    [Test]
    public void Serialized_manifest_strictly_binds_sources_build_admissions_and_artifacts()
    {
        byte[] bytes = ReadCheckedArtifact(LogOpcodeProfile.ManifestFileName);
        SourceManifest expected = LogOpcodeProfile.DeserializeManifest(bytes);

        JsonObject unknown = ParseObject(bytes);
        unknown["unknown"] = true;
        AssertManifestJsonRejected(unknown);
        Assert.That(() => LogOpcodeProfile.DeserializeManifest(null!), Throws.TypeOf<ExtractionException>());
        Assert.That(() => LogOpcodeProfile.DeserializeManifest("{"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => LogOpcodeProfile.DeserializeManifest("null"u8.ToArray()), Throws.TypeOf<ExtractionException>());

        JsonObject topCase = ParseObject(bytes);
        Rename(topCase, "schemaVersion", "SchemaVersion");
        AssertManifestJsonRejected(topCase);
        JsonObject nestedCase = ParseObject(bytes);
        Rename(nestedCase["admissions"]!.AsArray()[0]!.AsObject(), "key", "Key");
        AssertManifestJsonRejected(nestedCase);
        JsonObject omitted = ParseObject(bytes);
        omitted.Remove("schemaVersion");
        AssertManifestJsonRejected(omitted);

        Action<JsonObject>[] mutations =
        [
            root => root["schemaVersion"] = 2,
            root => root["extractorVersion"] = "changed",
            root => root["irSha256"] = new string('0', 64),
            root => root["leanSha256"] = new string('0', 64),
            root => root["combinedSourceSha256"] = new string('0', 64),
            root => root["sources"]!.AsArray()[0]!["path"] = "changed",
            root => root["sources"]!.AsArray()[0]!["sha256"] = new string('0', 64),
            root => root["sources"]!.AsArray()[0]!["roslynSyntaxSha256"] = new string('0', 64),
            root => root["rawSources"]!.AsArray()[0]!["path"] = "changed",
            root => root["rawSources"]!.AsArray()[0]!["sha256"] = new string('0', 64),
            root => root["admissions"]!.AsArray()[0]!["key"] = "changed",
            root => root["admissions"]!.AsArray()[0]!["templateSha256"] = new string('0', 64),
            root => root["admissions"]!.AsArray()[0]!["sourceSyntaxSha256"] = new string('0', 64),
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
            root => root["rawSources"]!.AsArray()[0] = null,
            root => root["admissions"]!.AsArray()[0] = null,
        })
        {
            JsonObject document = ParseObject(bytes);
            mutate(document);
            AssertManifestJsonRejected(document);
        }

        JsonObject duplicateSource = ParseObject(bytes);
        duplicateSource["sources"]!.AsArray()[1] = duplicateSource["sources"]!.AsArray()[0]!.DeepClone();
        AssertManifestJsonRejected(duplicateSource);
        JsonObject duplicateAdmission = ParseObject(bytes);
        duplicateAdmission["admissions"]!.AsArray()[1] = duplicateAdmission["admissions"]!.AsArray()[0]!.DeepClone();
        AssertManifestJsonRejected(duplicateAdmission);
    }

    [Test]
    public void Ir_null_collections_and_mutated_obligations_are_rejected_before_emission()
    {
        string root = RepoRoot();
        using TemporaryDirectory output = new("log-opcode-ir-boundary");
        IrDocument document = LogOpcodeProfile.DeserializeIr(File.ReadAllBytes(Extract(root, output.Path).IrPath));
        IrDocument nullOpcodes = document with { Opcodes = null! };
        IrDocument mutatedObligations = document with
        {
            OpenExtractionObligations = ["untrusted Lean text"],
        };

        ExtractionException nullCollection = Assert.Throws<ExtractionException>(() =>
            LogOpcodeProfile.ValidateIr(nullOpcodes))!;
        ExtractionException obligations = Assert.Throws<ExtractionException>(() =>
            LogOpcodeProfile.ValidateIr(mutatedObligations))!;
        ExtractionException emission = Assert.Throws<ExtractionException>(() =>
            LogOpcodeLeanEmitter.Emit(mutatedObligations, "ir", "source"))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nullCollection.Message, Does.Contain("null collection"));
            Assert.That(obligations.Message, Does.Contain("extraction obligations changed"));
            Assert.That(emission.Message, Does.Contain("extraction obligations changed"));
        }
    }

    [Test]
    public void Emitter_rejects_noncanonical_digest_comments()
    {
        string root = RepoRoot();
        using TemporaryDirectory output = new("log-opcode-emitter-digests");
        IrDocument document = LogOpcodeProfile.DeserializeIr(File.ReadAllBytes(Extract(root, output.Path).IrPath));

        ExtractionException malformedIrDigest = Assert.Throws<ExtractionException>(() =>
            LogOpcodeLeanEmitter.Emit(document, new string('a', 63), new string('b', 64)))!;
        ExtractionException injectedSourceDigest = Assert.Throws<ExtractionException>(() =>
            LogOpcodeLeanEmitter.Emit(document, new string('a', 64), new string('b', 46) + "\n theorem injected"))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(malformedIrDigest.Message, Does.Contain("Canonical IR SHA-256"));
            Assert.That(injectedSourceDigest.Message, Does.Contain("Source closure SHA-256"));
        }
    }

    [TestCase("root")]
    [TestCase("duplicate")]
    [TestCase("missing")]
    [TestCase("unbound")]
    public void Specialization_mutation_is_rejected(string mutation)
    {
        string root = RepoRoot();
        using TemporaryDirectory output = new("log-opcode-specialization");
        IrDocument document = LogOpcodeProfile.DeserializeIr(File.ReadAllBytes(Extract(root, output.Path).IrPath));
        List<OpcodeSpecialization> specializations = document.Specializations.ToList();
        specializations = mutation switch
        {
            "root" => [specializations[0] with { ClosedRoot = "arbitrary" }, .. specializations.Skip(1)],
            "duplicate" => [specializations[0], specializations[0], .. specializations.Skip(2)],
            "missing" => specializations.Skip(1).ToList(),
            "unbound" => [specializations[0] with { ClosedRoot = specializations[0].ClosedRoot + "<TGasPolicy>" }, .. specializations.Skip(1)],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            LogOpcodeProfile.ValidateSpecializations(document.Opcodes, specializations))!;
        Assert.That(exception.Message, Does.Match("exact closed production root|Duplicate specialization|Expected 20 exact closed roots"));
    }

    [TestCase("effect")]
    [TestCase("topic")]
    [TestCase("byte")]
    [TestCase("pc")]
    [TestCase("header")]
    public void Descriptor_mutation_is_rejected(string mutation)
    {
        string root = RepoRoot();
        using TemporaryDirectory output = new("log-opcode-descriptor");
        IrDocument document = LogOpcodeProfile.DeserializeIr(File.ReadAllBytes(Extract(root, output.Path).IrPath));
        List<OpcodeDescriptor> opcodes = document.Opcodes.ToList();
        OpcodeDescriptor first = opcodes[0];
        opcodes[0] = mutation switch
        {
            "effect" => first with { EffectOrder = [first.EffectOrder[1], first.EffectOrder[0], .. first.EffectOrder.Skip(2)] },
            "topic" => first with { TopicCount = 4 },
            "byte" => first with { OpcodeByte = 0xa1 },
            "pc" => first with { ProgramCounterDelta = 0 },
            "header" => first with { HeaderStackInputs = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            LogOpcodeProfile.ValidateIr(document with { Opcodes = opcodes }))!;
        Assert.That(exception.Message, Does.Contain("descriptor"));
    }

    [TestCase(HandlersPath,
        "lookup[(int)Instruction.LOG2] = OpcodeHandler<LogOpcode<EvmInstructions.Op2>, TTracingInst, TCancelable>();",
        "lookup[(int)Instruction.LOG2] = OpcodeHandler<LogOpcode<EvmInstructions.Op3>, TTracingInst, TCancelable>();")]
    [TestCase(InstructionPath, "LOG4 = 0xa4,", "LOG4 = 0xa3,")]
    [TestCase(GasCostPath, "public const ulong LogTopic = 375;", "public const ulong LogTopic = 376;")]
    [TestCase(GasPolicyPath,
        "GasCostOf.Log + topicCount * GasCostOf.LogTopic + dataSize * GasCostOf.LogData",
        "GasCostOf.Log + dataSize * GasCostOf.LogTopic + topicCount * GasCostOf.LogData")]
    [TestCase(MemoryPath, "((newActiveWords * newActiveWords) >> 9)", "((newActiveWords * newActiveWords) >> 8)")]
    [TestCase(DispatchPath, "pc++;", "pc--;")]
    [TestCase(HandlersPath,
        "EvmInstructions.InstructionLog<TGasPolicy, TOpCount>(ref stack, ref gas, vm);",
        "EvmInstructions.InstructionLog<TGasPolicy, TOpCount>(ref stack, ref gas, null!);")]
    [TestCase(StandardVirtualMachinePath,
        "_opcodeTablesBySpec.GetValue(Spec, static _ => new OpcodeTable());",
        "new OpcodeTable();")]
    [TestCase(NamedReleaseSpecPath, "new TSelf()", "Amsterdam.Instance")]
    [TestCase(AmsterdamPath,
        "NamedReleaseSpec<Amsterdam>(BPO2.Instance)",
        "NamedReleaseSpec<Amsterdam>(BPO1.Instance)")]
    [TestCase(BuildTargetsPath,
        "<ItemGroup Condition=\"'$(EnableZkEvm)' != 'true'\">",
        "<ItemGroup Condition=\"'$(EnableZkEvm)' == 'true'\">")]
    public void Production_mutation_is_rejected(string path, string before, string after)
    {
        using Fixture fixture = new();
        fixture.Replace(path, before, after);
        Assert.Throws<ExtractionException>(() => fixture.Extract());
    }

    [Test]
    public void Dead_semantic_branch_is_rejected()
    {
        using Fixture fixture = new();
        fixture.InsertAtMethodBody(InstructionLogPath, "InstructionLog", "if (false) return EvmExceptionType.None;");
        AssertRejected(fixture, "Unadmitted complete syntax");
    }

    [Test]
    public void Reachable_early_return_is_rejected()
    {
        using Fixture fixture = new();
        fixture.InsertAtMethodBody(InstructionLogPath, "InstructionLog", "return EvmExceptionType.None;");
        AssertRejected(fixture, "Unadmitted complete syntax");
    }

    [Test]
    public void Later_competing_dispatch_assignment_is_rejected()
    {
        const string assignment =
            "lookup[(int)Instruction.LOG0] = OpcodeHandler<LogOpcode<EvmInstructions.Op0>, TTracingInst, TCancelable>();";
        using Fixture fixture = new();
        fixture.Replace(HandlersPath, assignment, $"{assignment}\n        {assignment}");
        AssertRejected(fixture, "exactly one dispatch assignment", new Dictionary<string, string>
        {
            ["VirtualMachineOpcodeHandlers"] = fixture.Read(HandlersPath),
        });
    }

    [Test]
    public void Dead_dispatch_assignment_is_rejected()
    {
        const string assignment =
            "lookup[(int)Instruction.LOG0] = OpcodeHandler<LogOpcode<EvmInstructions.Op0>, TTracingInst, TCancelable>();";
        using Fixture fixture = new();
        fixture.Replace(HandlersPath, assignment, $"if (false) {{ {assignment} }}\n        {assignment}");
        AssertRejected(fixture, "unconditional top-level statement", new Dictionary<string, string>
        {
            ["VirtualMachineOpcodeHandlers"] = fixture.Read(HandlersPath),
        });
    }

    [Test]
    public void Compile_valid_semantic_order_mutation_is_rejected_after_template_admission()
    {
        const string load =
            "if (!vmState.Memory.TryLoad(in position, length, out ReadOnlyMemory<byte> data))\n            goto OutOfGas;";
        const string allocation = "Hash256[] topics = topicsCount == 0 ? [] : new Hash256[topicsCount];";
        using Fixture fixture = new();
        fixture.Swap(InstructionLogPath, load, allocation);
        string mutatedSource = fixture.Read(InstructionLogPath);

        AssertRejected(fixture, "LOG semantic effect order", new Dictionary<string, string>
        {
            ["InstructionLog"] = mutatedSource,
        });
    }

    [Test]
    public void Compile_valid_log_observation_reorder_is_rejected_after_template_admission()
    {
        const string add = "VmState.AccessTracker.Logs.Add(logEntry);";
        const string report =
            "if (DispatchFlags.ConstTracing && TxTracer.IsTracingLogs)\n        {\n            TxTracer.ReportLog(logEntry);\n        }";
        using Fixture fixture = new();
        fixture.Swap(VirtualMachinePath, add, report);
        string admissionPath = Path.Combine(
            RepoRoot(), "tools/Evm/Lean/LogOpcodeExtractor/Admission/VirtualMachineLog.cs.txt");
        string mutatedAdmission = SwapText(File.ReadAllText(admissionPath), add, report);

        AssertRejected(fixture, "journal append before optional log tracing", new Dictionary<string, string>
        {
            ["VirtualMachineLog"] = mutatedAdmission,
        });
    }

    [Test]
    public void Preprocessor_directive_is_rejected()
    {
        using Fixture fixture = new();
        fixture.InsertAtMethodBody(InstructionLogPath, "InstructionLog", "#if NEVER\n#endif");
        AssertRejected(fixture, "Preprocessor directives are not admitted");
    }

    [Test]
    public void Using_alias_is_rejected()
    {
        using Fixture fixture = new();
        fixture.Prepend(InstructionLogPath, "using Hidden = Nethermind.Evm.EvmInstructions;\n");
        AssertRejected(fixture, "Using aliases are not admitted");
    }

    private static void AssertRejected(
        Fixture fixture,
        string message,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.Extract(overrides))!;
        Assert.That(exception.Message, Does.Contain(message));
    }

    private static ExtractionResult Extract(string root, string output) => LogOpcodeProfile.Extract(
        root,
        output,
        Path.Combine(output, "LogOpcodeKernel.lean"));

    private static string SwapText(string source, string first, string second)
    {
        int firstPosition = source.IndexOf(first, StringComparison.Ordinal);
        int secondPosition = source.IndexOf(second, StringComparison.Ordinal);
        Assert.That(firstPosition, Is.GreaterThanOrEqualTo(0));
        Assert.That(secondPosition, Is.GreaterThan(firstPosition));
        source = source.Remove(secondPosition, second.Length).Insert(secondPosition, first);
        return source.Remove(firstPosition, first.Length).Insert(firstPosition, second);
    }

    private static byte[] ReadCheckedArtifact(string name) => File.ReadAllBytes(Path.Combine(
        RepoRoot(), "tools/Evm/Lean/LogOpcodeExtractor/Generated", name));

    private static JsonObject ParseObject(byte[] bytes) => JsonNode.Parse(bytes)!.AsObject();

    private static void Rename(JsonObject owner, string before, string after)
    {
        JsonNode value = owner[before]!.DeepClone();
        owner.Remove(before);
        owner[after] = value;
    }

    private static void AssertSerializedIrRejected(JsonObject document) => Assert.That(
        () => LogOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(document.ToJsonString())),
        Throws.TypeOf<ExtractionException>());

    private static void AssertManifestJsonRejected(JsonObject document) => Assert.That(
        () => LogOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(document.ToJsonString())),
        Throws.TypeOf<ExtractionException>());

    private static void AssertManifestLineageRejected(JsonObject document, SourceManifest expected) => Assert.That(
        () => LogOpcodeProfile.ValidateManifest(
            LogOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(document.ToJsonString())), expected),
        Throws.TypeOf<ExtractionException>());

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, HandlersPath))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Nethermind repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "log-opcode-fixtures", Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            string productionRoot = RepoRoot();
            foreach (string relativePath in LogOpcodeProfile.InputPaths)
                Write(relativePath, File.ReadAllText(Path.Combine(productionRoot, relativePath)));
        }

        public string Root { get; }
        public string Output { get; }

        public void Extract(IReadOnlyDictionary<string, string>? overrides = null) =>
            LogOpcodeProfile.Extract(Root, Output, Path.Combine(Output, "LogOpcodeKernel.lean"), overrides);

        public string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

        public void Replace(string relativePath, string before, string after)
        {
            string source = Read(relativePath);
            Assert.That(source, Does.Contain(before));
            Write(relativePath, source.Replace(before, after, StringComparison.Ordinal));
        }

        public void Swap(string relativePath, string first, string second) =>
            Write(relativePath, SwapText(Read(relativePath), first, second));

        public void InsertAtMethodBody(string relativePath, string methodName, string statement)
        {
            string source = Read(relativePath);
            int methodStart = source.IndexOf(methodName, StringComparison.Ordinal);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            int bodyStart = source.IndexOf('{', methodStart);
            Assert.That(bodyStart, Is.GreaterThan(methodStart));
            Write(relativePath, source.Insert(bodyStart + 1, $"\n        {statement}"));
        }

        public void Prepend(string relativePath, string text) => Write(relativePath, text + Read(relativePath));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private void Write(string relativePath, string source)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private sealed class TemporaryDirectory(string prefix) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            prefix,
            Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
