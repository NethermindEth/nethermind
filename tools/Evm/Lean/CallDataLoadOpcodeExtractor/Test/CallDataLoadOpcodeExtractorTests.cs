// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.CallDataLoadOpcodeExtractor.Test;

[TestFixture]
public sealed class CallDataLoadOpcodeExtractorTests
{
    private const string HandlerPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs";
    private const string SpecPath = "tools/Evm/Lean/CallDataLoadOpcodeExtractor/Specification/CallDataLoadExecution.lean";

    [Test]
    public void Production_sources_extract_to_byte_identical_artifacts()
    {
        using TemporaryDirectory first = new("calldata-load-extractor-first");
        using TemporaryDirectory second = new("calldata-load-extractor-second");
        ExtractionResult left = Extract(RepoRoot(), first.Path);
        ExtractionResult right = Extract(RepoRoot(), second.Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(left.IrPath), Is.EqualTo(File.ReadAllBytes(right.IrPath)));
            Assert.That(File.ReadAllBytes(left.ManifestPath), Is.EqualTo(File.ReadAllBytes(right.ManifestPath)));
            Assert.That(File.ReadAllBytes(left.LeanPath), Is.EqualTo(File.ReadAllBytes(right.LeanPath)));
        }
    }

    [Test]
    public void Checked_artifacts_equal_fresh_extraction()
    {
        using TemporaryDirectory fresh = new("calldata-load-extractor-fresh");
        ExtractionResult result = Extract(RepoRoot(), fresh.Path);
        string generated = Path.Combine(RepoRoot(), "tools/Evm/Lean/CallDataLoadOpcodeExtractor/Generated");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(result.IrPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, CallDataLoadOpcodeProfile.IrFileName))));
            Assert.That(File.ReadAllBytes(result.ManifestPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, CallDataLoadOpcodeProfile.ManifestFileName))));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, CallDataLoadOpcodeProfile.LeanFileName))));
        }
    }

    [Test]
    public void Ir_closes_calldataload_over_four_dispatch_tables()
    {
        IrDocument document = CallDataLoadOpcodeProfile.DeserializeIr(ReadChecked(CallDataLoadOpcodeProfile.IrFileName));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.Opcodes, Has.Length.EqualTo(1));
            Assert.That(document.Specializations, Has.Length.EqualTo(4));
            Assert.That(document.Opcodes.Single().OpcodeByte, Is.EqualTo(0x35));
            Assert.That(document.Specializations.GroupBy(static item => item.Opcode).All(static group => group.Count() == 4), Is.True);
            Assert.That(document.Specializations.All(static item =>
                !item.ClosedRoot.Contains("TGasPolicy", StringComparison.Ordinal) &&
                !item.ClosedRoot.Contains("TTracingInst", StringComparison.Ordinal) &&
                !item.ClosedRoot.Contains("TCancelable", StringComparison.Ordinal)), Is.True);
            Assert.That(document.Specializations.Select(static item => item.ClosedRoot).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(4));
        }
    }

    [Test]
    public void Ir_pins_order_sensitive_operational_edges()
    {
        IrDocument document = CallDataLoadOpcodeProfile.DeserializeIr(ReadChecked(CallDataLoadOpcodeProfile.IrFileName));
        OpcodeDescriptor opcode = document.Opcodes.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opcode.EffectOrder, Is.EqualTo(new[] { "traceStart", "traceStackBottomFirst", "advanceDispatchPc",
                "incrementOpcodeCount", "chargeVeryLow", "checkDepthOne", "peekTopSlot",
                "decodeUInt256Offset", "rejectAboveUInt64OrAtEnd", "zeroTopAndTraceZeroByte",
                "otherwiseCopyAtMost32", "rightZeroPadAndReplaceTop", "traceRawWord",
                "closeSuccessTrace", "tailDispatchOrCancelableBoundary" }));
            Assert.That(Array.IndexOf(opcode.EffectOrder, "chargeVeryLow"),
                Is.LessThan(Array.IndexOf(opcode.EffectOrder, "checkDepthOne")));
            Assert.That(opcode.ReplacesTop, Is.True);
            Assert.That(opcode.StackGrowth, Is.Zero);
            Assert.That(opcode.PushSize, Is.EqualTo(-1));
            Assert.That(opcode.EndsInstructionTrace, Is.False);
            Assert.That(opcode.Semantics.StackTraceOrder, Is.EqualTo("bottomFirst"));
            Assert.That(opcode.Semantics.TraceRule, Does.Contain("one zero byte"));
        }
    }

    [Test]
    public void Generated_transition_is_theorem_free_and_does_not_reuse_handwritten_transition()
    {
        string generated = File.ReadAllText(Path.Combine(GeneratedDirectory(), CallDataLoadOpcodeProfile.LeanFileName));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Match(@"(?m)^\s*(theorem|axiom|example|admit|sorry)\b"));
            Assert.That(generated, Does.Not.Contain("MemoryStackControl.step"));
            Assert.That(generated, Does.Not.Contain("CallDataLoadExecution.execute"));
            Assert.That(generated, Does.Not.Contain("def beginInstruction"));
            Assert.That(generated, Does.Contain("def readCallDataWordBytes"));
            Assert.That(generated, Does.Contain("def executeCheckedBody"));
            Assert.That(generated, Does.Contain("def executeSemantic"));
            Assert.That(generated, Does.Contain("def closeFailureTrace"));
        }
    }

    [Test]
    public void Refinement_contains_required_executable_boundary_vectors()
    {
        string refinement = File.ReadAllText(Path.Combine(RepoRoot(),
            "tools/Evm/Lean/CallDataLoadOpcodeExtractor/Refinement/CallDataLoadOpcode.lean"));
        foreach (string vector in new[]
                 {
                     "vector_offset_zero", "vector_tail_right_zero_pads", "vector_exact_end_is_zero",
                     "vector_beyond_end_is_zero", "vector_above_uint64_is_zero",
                     "vector_max_uint256_is_zero", "vector_one_short_oog",
                     "vector_underflow_retains_charge_and_stack", "vector_traced_event_order_and_payload",
                     "vector_traced_stack_payload_is_bottom_first",
                     "no_trace_cancelable_same_transition", "traced_cancelable_same_opcode_transition",
                 })
            Assert.That(refinement, Does.Contain($"theorem {vector}"), vector);
    }

    [Test]
    public void Generated_executable_is_a_function_of_semantic_ir_not_the_operational_spec_body()
    {
        IrDocument admitted = CallDataLoadOpcodeProfile.DeserializeIr(ReadChecked(CallDataLoadOpcodeProfile.IrFileName));
        OpcodeDescriptor[] changedOpcodes = [.. admitted.Opcodes];
        OpcodeDescriptor opcode = changedOpcodes.Single();
        changedOpcodes[0] = opcode with
        {
            Semantics = opcode.Semantics with { AccessWidth = 31 },
        };
        IrDocument changed = admitted with
        {
            Opcodes = changedOpcodes,
            Schedule = admitted.Schedule with { WordBytes = 31 },
        };
        IrDocument changedSchedule = admitted with
        {
            Schedule = admitted.Schedule with { VeryLow = admitted.Schedule.VeryLow + 1 },
        };
        IrDocument changedGasClosure = admitted with
        {
            OuterFailure = admitted.OuterFailure with { ClearGasOnOutOfGas = false },
        };
        IrDocument changedTraceClosure = admitted with
        {
            OuterFailure = admitted.OuterFailure with { FinishBeforeError = false },
        };
        IrDocument changedFaultPc = admitted with
        {
            OuterFailure = admitted.OuterFailure with { DecrementFaultPc = false },
        };
        IrDocument changedStackTraceOrder = admitted with
        {
            Opcodes = [opcode with
            {
                Semantics = opcode.Semantics with { StackTraceOrder = "topFirst" },
            }],
        };
        string digest = new('a', 64);

        string original = Encoding.UTF8.GetString(
            CallDataLoadOpcodeLeanEmitter.EmitFromSemanticIr(admitted, digest, digest));
        string mutated = Encoding.UTF8.GetString(
            CallDataLoadOpcodeLeanEmitter.EmitFromSemanticIr(changed, digest, digest));
        string scheduleMutated = Encoding.UTF8.GetString(
            CallDataLoadOpcodeLeanEmitter.EmitFromSemanticIr(changedSchedule, digest, digest));
        string gasClosureMutated = Encoding.UTF8.GetString(
            CallDataLoadOpcodeLeanEmitter.EmitFromSemanticIr(changedGasClosure, digest, digest));
        string traceClosureMutated = Encoding.UTF8.GetString(
            CallDataLoadOpcodeLeanEmitter.EmitFromSemanticIr(changedTraceClosure, digest, digest));
        string faultPcMutated = Encoding.UTF8.GetString(
            CallDataLoadOpcodeLeanEmitter.EmitFromSemanticIr(changedFaultPc, digest, digest));
        string stackTraceOrderMutated = Encoding.UTF8.GetString(
            CallDataLoadOpcodeLeanEmitter.EmitFromSemanticIr(changedStackTraceOrder, digest, digest));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mutated, Is.Not.EqualTo(original));
            Assert.That(original, Does.Contain("accessWidth := 32"));
            Assert.That(mutated, Does.Contain("accessWidth := 31"));
            Assert.That(scheduleMutated, Is.Not.EqualTo(original));
            Assert.That(original, Does.Contain("veryLow := 3"));
            Assert.That(scheduleMutated, Does.Contain("veryLow := 4"));
            Assert.That(scheduleMutated, Does.Contain("def executeAmsterdam"));
            Assert.That(gasClosureMutated, Is.Not.EqualTo(original));
            Assert.That(gasClosureMutated, Does.Contain("let state := outcome.state"));
            Assert.That(traceClosureMutated, Is.Not.EqualTo(original));
            Assert.That(traceClosureMutated, Does.Contain("[.error outcome.status, .finish state.gasLeft]"));
            Assert.That(faultPcMutated, Is.Not.EqualTo(original));
            Assert.That(faultPcMutated, Does.Contain("def faultPc (outcome : Outcome) : Nat :=\n  outcome.state.pc"));
            Assert.That(stackTraceOrderMutated, Is.Not.EqualTo(original));
            Assert.That(original, Does.Contain(".operationStack state.stack.words.reverse"));
            Assert.That(stackTraceOrderMutated, Does.Contain(".operationStack state.stack.words]"));
            Assert.That(original, Does.Contain("def executeAmsterdamClosed"));
        }
    }

    [Test]
    public void Ir_rejects_every_property_omission_null_case_alias_duplicate_and_leaf_change() =>
        AssertEveryPropertyMutationRejected(
            ReadChecked(CallDataLoadOpcodeProfile.IrFileName), CallDataLoadOpcodeProfile.DeserializeIr);

    [Test]
    public void Manifest_rejects_every_property_omission_null_case_alias_duplicate_and_leaf_change() =>
        AssertEveryPropertyMutationRejected(
            ReadChecked(CallDataLoadOpcodeProfile.ManifestFileName), CallDataLoadOpcodeProfile.DeserializeManifest);

    [Test]
    public void Ir_rejects_null_and_changed_scalar_array_elements()
    {
        byte[] bytes = ReadChecked(CallDataLoadOpcodeProfile.IrFileName);
        JsonNode root = JsonNode.Parse(bytes)!;
        foreach (string property in new[] { "forkLineage", "openExtractionObligations" })
            AssertScalarArrayElementMutationsRejected(root, [property], bytes, CallDataLoadOpcodeProfile.DeserializeIr);

        JsonArray opcodes = root["opcodes"]!.AsArray();
        for (int index = 0; index < opcodes.Count; index++)
            AssertScalarArrayElementMutationsRejected(root, ["opcodes", index, "effectOrder"], bytes,
                CallDataLoadOpcodeProfile.DeserializeIr);
    }

    [Test]
    public void Manifest_rejects_null_and_changed_semantic_binding_elements()
    {
        byte[] bytes = ReadChecked(CallDataLoadOpcodeProfile.ManifestFileName);
        JsonNode root = JsonNode.Parse(bytes)!;
        AssertScalarArrayElementMutationsRejected(root, ["semanticBindings"], bytes,
            CallDataLoadOpcodeProfile.DeserializeManifest);
    }

    [TestCase("ir")]
    [TestCase("lean")]
    public void Manifest_rejects_alternate_valid_artifact_digest(string artifact)
    {
        JsonObject manifest = JsonNode.Parse(ReadChecked(CallDataLoadOpcodeProfile.ManifestFileName))!.AsObject();
        manifest[artifact]!["sha256"] = new string('a', 64);
        Assert.That(() => CallDataLoadOpcodeProfile.DeserializeManifest(ToBytes(manifest)),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Manifest_rejects_uppercase_and_newline_in_every_digest()
    {
        JsonNode admitted = JsonNode.Parse(ReadChecked(CallDataLoadOpcodeProfile.ManifestFileName))!;
        PropertyPath[] digests = EnumerateProperties(admitted)
            .Where(static property => property.Name == "sha256" ||
                property.Name.EndsWith("Sha256", StringComparison.Ordinal)).ToArray();
        Assert.That(digests, Is.Not.Empty);
        foreach (PropertyPath digest in digests)
        {
            foreach (Func<string, string> mutation in new Func<string, string>[]
                     {
                         static value => value.ToUpperInvariant(), static value => value + "\n",
                     })
            {
                JsonNode changed = admitted.DeepClone();
                JsonObject owner = LocateOwner(changed, digest);
                owner[digest.Name] = mutation(owner[digest.Name]!.GetValue<string>());
                Assert.That(() => CallDataLoadOpcodeProfile.DeserializeManifest(ToBytes(changed)),
                    Throws.TypeOf<ExtractionException>(), digest.Display);
            }
        }
    }

    [TestCase("ir", "A")]
    [TestCase("ir", "\n")]
    [TestCase("source", "A")]
    [TestCase("source", "\n")]
    public void Emitter_rejects_noncanonical_digests(string target, string injection)
    {
        IrDocument document = CallDataLoadOpcodeProfile.DeserializeIr(ReadChecked(CallDataLoadOpcodeProfile.IrFileName));
        string valid = new('a', 64);
        string invalid = injection == "A" ? valid.ToUpperInvariant() : valid + injection;
        Assert.That(() => CallDataLoadOpcodeLeanEmitter.Emit(
            document, target == "ir" ? invalid : valid, target == "source" ? invalid : valid),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Every_admitted_source_build_and_operational_template_rejects_one_byte_change()
    {
        foreach (string path in CallDataLoadOpcodeProfile.InputPaths)
        {
            using Fixture fixture = new();
            fixture.AppendByte(path, (byte)'\n');
            Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>(), path);
        }
    }

    [Test]
    public void Competing_standard_handler_declaration_is_rejected()
    {
        using Fixture fixture = new();
        fixture.WriteAdditional("src/Nethermind/Nethermind.Evm/CompetingCallDataLoadOpcode.cs", """
            namespace Nethermind.Evm;
            public partial class VirtualMachine<TGasPolicy>
            {
                private readonly struct CallDataLoadOpcode<TTracingInst> { }
            }
            """);
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Bottom_first_stack_trace_reversal_and_raw_copy_are_mutation_guarded()
    {
        using (Fixture fixture = new())
        {
            fixture.Replace(SpecPath, ".operationStack state.stack.words.reverse",
                ".operationStack state.stack.words");
            Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
        }

        using (Fixture fixture = new())
        {
            fixture.Replace("src/Nethermind/Nethermind.Evm/Tracing/TraceStack.cs",
                "_stack.Span.CopyTo(raw);", "raw.AsSpan().CopyTo(raw);");
            Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
        }
    }

    [Test]
    public void Ethereum_virtual_machine_base_generic_specialization_mutation_is_rejected()
    {
        using Fixture fixture = new();
        fixture.ReplaceFirst("src/Nethermind/Nethermind.Evm/VirtualMachine.cs",
            ") : VirtualMachine<EthereumGasPolicy>(blockHashProvider, specProvider, logManager), IVirtualMachine",
            ") : VirtualMachine<AlternativeGasPolicy>(blockHashProvider, specProvider, logManager), IVirtualMachine");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("gasLeft := 0", "gasLeft := 1", TestName = "MutationRejectsGasExhaustion")]
    [TestCase("offset.val ≤ schedule.maxUInt64", "offset.val < schedule.maxUInt64", TestName = "MutationRejectsUInt64Boundary")]
    [TestCase("rightZeroPadAndReplaceTop", "leftPadAndReplaceTop", TestName = "MutationRejectsPaddingOrderClaim")]
    public void Operational_and_ir_mutations_cannot_reuse_admission(string before, string after)
    {
        using Fixture fixture = new();
        string path = before == "rightZeroPadAndReplaceTop" ?
            Path.Combine(fixture.Output, "forged.ir") : SpecPath;
        if (path == SpecPath)
        {
            fixture.Replace(SpecPath, before, after);
            Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
        }
        else
        {
            IrDocument document = CallDataLoadOpcodeProfile.DeserializeIr(ReadChecked(CallDataLoadOpcodeProfile.IrFileName));
            byte[] mutated = CallDataLoadOpcodeProfile.Serialize(document).ReplaceBytes(before, after);
            Assert.That(() => CallDataLoadOpcodeProfile.DeserializeIr(mutated), Throws.TypeOf<ExtractionException>());
        }
    }

    [TestCase("src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs",
        "CallDataLoadOpcode<TTracingInst>", "CallDataLoadOpcode<TTracingInst, TCancelable>",
        TestName = "RejectsHandlerGenericOwnerMutation")]
    [TestCase("src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs",
        "Instruction.CALLDATALOAD] = OpcodeHandler<CallDataLoadOpcode<TTracingInst>, TTracingInst, TCancelable>()",
        "Instruction.CALLDATALOAD] = OpcodeHandler<CallDataCopyOpcode<TTracingInst>, TTracingInst, TCancelable>()",
        TestName = "RejectsDispatchRouteMutation")]
    [TestCase("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs",
        "CallDataLoadCore<TGasPolicy, TTracingInst>(ref EvmStack stack, VirtualMachine<TGasPolicy> vm)",
        "CallDataLoadCore<TGasPolicy, TTracingInst>(ref EvmStack stack, VirtualMachine<TGasPolicy> vm, int extra = 0)",
        TestName = "RejectsHandlerSignatureMutation")]
    [TestCase("src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs",
        ".AddScoped<IVirtualMachine, EthereumVirtualMachine>()",
        ".AddScoped<IVirtualMachine, AlternativeVirtualMachine>()",
        TestName = "RejectsStandardVirtualMachineRegistrationImplementationSwap")]
    [TestCase("src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs",
        ".AddScoped<IVirtualMachine, EthereumVirtualMachine>()", "",
        TestName = "RejectsStandardVirtualMachineRegistrationRemoval")]
    [TestCase("src/Nethermind/Directory.Build.targets",
        "<ItemGroup Condition=\"'$(EnableZkEvm)' != 'true'\">",
        "<ItemGroup Condition=\"'$(EnableZkEvm)' == 'true'\">",
        TestName = "RejectsStandardBuildSelectorMutation")]
    public void Exact_source_owner_signature_and_route_mutations_are_rejected(
        string path, string before, string after)
    {
        using Fixture fixture = new();
        fixture.Replace(path, before, after);
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    private static byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static void AssertScalarArrayElementMutationsRejected<T>(JsonNode root, object[] path,
        byte[] admittedBytes, Func<byte[], T> deserialize)
    {
        JsonNode arrayNode = FollowPath(root, path);
        JsonArray array = arrayNode.AsArray();
        Assert.That(array, Is.Not.Empty, string.Join(".", path));
        for (int index = 0; index < array.Count; index++)
        {
            foreach (bool useNull in new[] { true, false })
            {
                JsonNode changed = JsonNode.Parse(admittedBytes)!;
                JsonArray changedArray = FollowPath(changed, path).AsArray();
                changedArray[index] = useNull ? null : ChangedValue(changedArray[index]!);
                Assert.That(() => deserialize(ToBytes(changed)), Throws.TypeOf<ExtractionException>(),
                    $"{string.Join(".", path)}[{index}] {(useNull ? "null" : "leaf change")}");
            }
        }
    }

    private static JsonNode FollowPath(JsonNode root, IEnumerable<object> path)
    {
        JsonNode current = root;
        foreach (object part in path)
            current = part is string property ? current[property]! : current[(int)part]!;
        return current;
    }

    private static void AssertEveryPropertyMutationRejected<T>(byte[] bytes, Func<byte[], T> deserialize)
    {
        JsonNode root = JsonNode.Parse(bytes)!;
        PropertyPath[] properties = EnumerateProperties(root).ToArray();
        Assert.That(properties, Is.Not.Empty);
        foreach (PropertyPath property in properties)
        {
            foreach (PropertyMutation mutation in new[]
                     {
                         PropertyMutation.Omit, PropertyMutation.Null, PropertyMutation.CaseAlias,
                     })
            {
                JsonNode changed = root.DeepClone();
                JsonObject owner = LocateOwner(changed, property);
                Mutate(owner, property.Name, mutation);
                Assert.That(() => deserialize(ToBytes(changed)), Throws.TypeOf<ExtractionException>(),
                    $"{property.Display} {mutation}");
            }

            Assert.That(() => deserialize(DuplicateProperty(root, property)),
                Throws.TypeOf<ExtractionException>(), $"{property.Display} duplicate");

            JsonNode? value = LocateOwner(root, property)[property.Name];
            if (value is JsonValue)
            {
                JsonNode changed = root.DeepClone();
                JsonObject owner = LocateOwner(changed, property);
                owner[property.Name] = ChangedValue(owner[property.Name]!);
                Assert.That(() => deserialize(ToBytes(changed)), Throws.TypeOf<ExtractionException>(),
                    $"{property.Display} leaf change");
            }
        }
    }

    private static IEnumerable<PropertyPath> EnumerateProperties(JsonNode node) => EnumerateProperties(node, []);

    private static IEnumerable<PropertyPath> EnumerateProperties(JsonNode node, IReadOnlyList<PathPart> parent)
    {
        if (node is JsonObject obj)
        {
            foreach ((string name, JsonNode? value) in obj)
            {
                yield return new([.. parent], name);
                if (value is not null)
                    foreach (PropertyPath nested in EnumerateProperties(value, [.. parent, new(name, null)]))
                        yield return nested;
            }
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
                if (array[index] is JsonNode item)
                    foreach (PropertyPath nested in EnumerateProperties(item, [.. parent, new(null, index)]))
                        yield return nested;
        }
    }

    private static JsonObject LocateOwner(JsonNode root, PropertyPath property)
    {
        JsonNode current = root;
        foreach (PathPart part in property.Parent)
            current = part.Name is not null ? current[part.Name]! : current[part.Index!.Value]!;
        return current.AsObject();
    }

    private static void Mutate(JsonObject owner, string name, PropertyMutation mutation)
    {
        switch (mutation)
        {
            case PropertyMutation.Omit:
                owner.Remove(name);
                break;
            case PropertyMutation.Null:
                owner[name] = null;
                break;
            case PropertyMutation.CaseAlias:
                JsonNode value = owner[name]!.DeepClone();
                owner.Remove(name);
                owner[char.ToUpperInvariant(name[0]) + name[1..]] = value;
                break;
        }
    }

    private static JsonNode ChangedValue(JsonNode node)
    {
        JsonValue value = node.AsValue();
        if (value.TryGetValue(out string? text)) return JsonValue.Create(text + "x")!;
        if (value.TryGetValue(out bool boolean)) return JsonValue.Create(!boolean)!;
        if (value.TryGetValue(out int integer)) return JsonValue.Create(integer + 1)!;
        if (value.TryGetValue(out long longInteger)) return JsonValue.Create(longInteger + 1)!;
        if (value.TryGetValue(out ulong unsignedInteger)) return JsonValue.Create(unsignedInteger + 1)!;
        throw new AssertionException($"Unsupported JSON primitive {node}.");
    }

    private static byte[] DuplicateProperty(JsonNode root, PropertyPath target)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream)) WriteNode(writer, root, [], target);
        return stream.ToArray();
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node, IReadOnlyList<PathPart> path,
        PropertyPath target)
    {
        switch (node)
        {
            case JsonObject obj:
                writer.WriteStartObject();
                foreach ((string name, JsonNode? value) in obj)
                {
                    writer.WritePropertyName(name);
                    WriteNode(writer, value, [.. path, new(name, null)], target);
                    if (target.Name == name && SamePath(path, target.Parent))
                    {
                        writer.WritePropertyName(name);
                        WriteNode(writer, value, [.. path, new(name, null)], target);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                for (int index = 0; index < array.Count; index++)
                    WriteNode(writer, array[index], [.. path, new(null, index)], target);
                writer.WriteEndArray();
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static bool SamePath(IReadOnlyList<PathPart> left, IReadOnlyList<PathPart> right) =>
        left.Count == right.Count && left.Zip(right).All(static pair => pair.First == pair.Second);

    private static byte[] ReadChecked(string name) => File.ReadAllBytes(Path.Combine(GeneratedDirectory(), name));

    private static string GeneratedDirectory() => Path.Combine(RepoRoot(), "tools/Evm/Lean/CallDataLoadOpcodeExtractor/Generated");

    private enum PropertyMutation { Omit, Null, CaseAlias }

    private sealed record PathPart(string? Name, int? Index);

    private sealed record PropertyPath(IReadOnlyList<PathPart> Parent, string Name)
    {
        public string Display => string.Concat(Parent.Select(static part => part.Name ?? $"[{part.Index}]")) + "." + Name;
    }

    private static ExtractionResult Extract(string root, string output) => CallDataLoadOpcodeProfile.Extract(
        root, output, Path.Combine(output, CallDataLoadOpcodeProfile.LeanFileName));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, HandlerPath))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Nethermind repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "calldata-load-fixtures", Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            foreach (string relativePath in CallDataLoadOpcodeProfile.InputPaths)
            {
                string target = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)), target);
            }
        }

        internal string Root { get; }
        internal string Output { get; }

        internal void Extract() => CallDataLoadOpcodeProfile.Extract(Root, Output, Path.Combine(Output, CallDataLoadOpcodeProfile.LeanFileName));

        internal void AppendByte(string relativePath, byte value)
        {
            using FileStream stream = File.Open(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)), FileMode.Append, FileAccess.Write, FileShare.None);
            stream.WriteByte(value);
        }

        internal void Replace(string relativePath, string before, string after)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string source = File.ReadAllText(path);
            Assert.That(source, Does.Contain(before));
            File.WriteAllText(path, source.Replace(before, after, StringComparison.Ordinal), new UTF8Encoding(false));
        }

        internal void ReplaceFirst(string relativePath, string before, string after)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string source = File.ReadAllText(path);
            int index = source.IndexOf(before, StringComparison.Ordinal);
            Assert.That(index, Is.GreaterThanOrEqualTo(0));
            File.WriteAllText(path, source[..index] + after + source[(index + before.Length)..],
                new UTF8Encoding(false));
        }

        internal void WriteAdditional(string relativePath, string source)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TemporaryDirectory(string prefix) : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, prefix, Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class ByteArrayTestExtensions
{
    internal static byte[] ReplaceBytes(this byte[] value, string before, string after)
    {
        string source = Encoding.UTF8.GetString(value);
        Assert.That(source, Does.Contain(before));
        return Encoding.UTF8.GetBytes(source.Replace(before, after, StringComparison.Ordinal));
    }
}
