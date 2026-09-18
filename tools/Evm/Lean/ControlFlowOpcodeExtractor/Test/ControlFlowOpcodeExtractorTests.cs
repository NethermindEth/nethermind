// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.ControlFlowOpcodeExtractor.Test;

[TestFixture]
public sealed class ControlFlowOpcodeExtractorTests
{
    private const string HandlerPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string SpecPath = "tools/Evm/Lean/ControlFlowOpcodeExtractor/Specification/ControlFlowExecution.lean";

    [Test]
    public void Production_sources_extract_to_byte_identical_artifacts()
    {
        using TemporaryDirectory first = new("control-flow-extractor-first");
        using TemporaryDirectory second = new("control-flow-extractor-second");
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
        using TemporaryDirectory fresh = new("control-flow-extractor-fresh");
        ExtractionResult result = Extract(RepoRoot(), fresh.Path);
        string generated = GeneratedDirectory();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(result.IrPath),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, ControlFlowOpcodeProfile.IrFileName))));
            Assert.That(File.ReadAllBytes(result.ManifestPath),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, ControlFlowOpcodeProfile.ManifestFileName))));
            Assert.That(File.ReadAllBytes(result.LeanPath),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, ControlFlowOpcodeProfile.LeanFileName))));
        }
    }

    [Test]
    public void Ir_closes_eight_opcodes_over_four_tables_and_two_gates_onto_four_shared_disabled_roots()
    {
        IrDocument document = ControlFlowOpcodeProfile.DeserializeIr(ReadChecked(ControlFlowOpcodeProfile.IrFileName));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.Opcodes, Has.Length.EqualTo(8));
            Assert.That(document.Specializations, Has.Length.EqualTo(32));
            Assert.That(document.Opcodes.Select(static opcode => opcode.OpcodeByte),
                Is.EquivalentTo(new[] { 0x00, 0x4b, 0x56, 0x57, 0x58, 0x5b, 0xf3, 0xfd }));
            Assert.That(document.Specializations.GroupBy(static item => item.Opcode)
                .All(static group => group.Count() == 4), Is.True);
            Assert.That(document.Specializations.Count(static item => item.DisabledRoot.Length != 0), Is.EqualTo(8));
            Assert.That(document.Specializations.Where(static item => item.DisabledRoot.Length != 0)
                .Select(static item => item.DisabledRoot).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(4));
            Assert.That(document.Opcodes.All(static item => !item.EndsInstructionTrace), Is.True);
            Assert.That(document.Specializations.All(static item =>
                !item.EnabledRoot.Contains("TTracingInst", StringComparison.Ordinal) &&
                !item.EnabledRoot.Contains("TCancelable", StringComparison.Ordinal)), Is.True);
        }
    }

    [Test]
    public void Ir_pins_order_sensitive_operational_edges()
    {
        IrDocument document = ControlFlowOpcodeProfile.DeserializeIr(ReadChecked(ControlFlowOpcodeProfile.IrFileName));
        OpcodeDescriptor slot = document.Opcodes.Single(static opcode => opcode.Name == "slotnum");
        OpcodeDescriptor jump = document.Opcodes.Single(static opcode => opcode.Name == "jump");
        OpcodeDescriptor returnOpcode = document.Opcodes.Single(static opcode => opcode.Name == "return_");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot.EffectOrder, Is.EqualTo(new[]
            {
                "traceStart", "incrementPc", "incrementOpcodeCount", "readNullableSlot",
                "missingBadInstruction", "chargeBase", "checkPushOverflow", "pushUInt64",
                "tracePush8", "checkBodyTraceOwnership", "traceFinish",
            }));
            Assert.That(Array.IndexOf(jump.EffectOrder, "popDestination"),
                Is.LessThan(Array.IndexOf(jump.EffectOrder, "validateInstructionBoundary")));
            Assert.That(Array.IndexOf(jump.EffectOrder, "untracedCountAndAdvanceJumpDest"),
                Is.LessThan(Array.IndexOf(jump.EffectOrder, "untracedChargeJumpDest")));
            Assert.That(Array.IndexOf(returnOpcode.EffectOrder, "chargeExpansion"),
                Is.LessThan(Array.IndexOf(returnOpcode.EffectOrder, "installLogicalMemorySize")));
            Assert.That(returnOpcode.FixedGas, Is.Zero);
        }
    }

    [Test]
    public void Generated_transition_is_theorem_free_and_does_not_call_the_handwritten_transition()
    {
        string generated = File.ReadAllText(Path.Combine(GeneratedDirectory(), ControlFlowOpcodeProfile.LeanFileName));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Match(@"(?m)^\s*(theorem|axiom|example|admit|sorry)\b"));
            Assert.That(generated, Does.Not.Contain("ControlFlowExecution.execute"));
            Assert.That(generated, Does.Not.Contain("MemoryStackControl.step"));
            Assert.That(generated, Does.Not.Contain("MemoryStackControl.isValidJumpDest"));
            Assert.That(generated, Does.Not.Match(@"\bisValidJumpDest\b"));
            Assert.That(generated, Does.Not.Contain("def beginInstruction"));
            Assert.That(generated, Does.Contain("def scanExtractedJumpDestination"));
            Assert.That(generated, Does.Contain("def extractedValidJumpDestination"));
            Assert.That(generated, Does.Contain("def executeSemantic"));
            Assert.That(generated, Does.Contain("def executeCore"));
            Assert.That(generated, Does.Contain("def closeFrame"));
            Assert.That(generated, Does.Contain("operationMemorySize"));
        }
    }

    [Test]
    public void Generated_executable_changes_with_semantic_ir()
    {
        IrDocument admitted = ControlFlowOpcodeProfile.DeserializeIr(ReadChecked(ControlFlowOpcodeProfile.IrFileName));
        OpcodeDescriptor[] changedOpcodes = [.. admitted.Opcodes];
        OpcodeDescriptor pc = changedOpcodes.Single(static opcode => opcode.Name == "pc");
        changedOpcodes[Array.IndexOf(changedOpcodes, pc)] = pc with { FixedGas = pc.FixedGas + 1 };
        IrDocument changedOpcode = admitted with { Opcodes = changedOpcodes };
        OpcodeDescriptor[] changedTraceOpcodes = [.. admitted.Opcodes];
        OpcodeDescriptor slot = changedTraceOpcodes.Single(static opcode => opcode.Name == "slotnum");
        changedTraceOpcodes[Array.IndexOf(changedTraceOpcodes, slot)] = slot with
        {
            TracePushWidth = slot.TracePushWidth - 1,
        };
        IrDocument changedTraceWidth = admitted with { Opcodes = changedTraceOpcodes };
        IrDocument changedSchedule = admitted with
        {
            Schedule = admitted.Schedule with { JumpDest = admitted.Schedule.JumpDest + 1 },
        };
        IrDocument changedJumpValidation = admitted with
        {
            JumpValidation = admitted.JumpValidation with
            {
                PushFirstOpcode = admitted.JumpValidation.PushFirstOpcode + 1,
            },
        };
        OpcodeDescriptor[] changedTraceOwnershipOpcodes = [.. admitted.Opcodes];
        OpcodeDescriptor jump = changedTraceOwnershipOpcodes.Single(static opcode => opcode.Name == "jump");
        changedTraceOwnershipOpcodes[Array.IndexOf(changedTraceOwnershipOpcodes, jump)] = jump with
        {
            EndsInstructionTrace = true,
        };
        IrDocument changedTraceOwnership = admitted with { Opcodes = changedTraceOwnershipOpcodes };
        byte[] template = File.ReadAllBytes(Path.Combine(RepoRoot(), SpecPath));
        string digest = new('a', 64);

        string original = Encoding.UTF8.GetString(ControlFlowOpcodeLeanEmitter.EmitFromSemanticIr(
            admitted, digest, digest, template));
        string opcodeMutation = Encoding.UTF8.GetString(ControlFlowOpcodeLeanEmitter.EmitFromSemanticIr(
            changedOpcode, digest, digest, template));
        string traceMutation = Encoding.UTF8.GetString(ControlFlowOpcodeLeanEmitter.EmitFromSemanticIr(
            changedTraceWidth, digest, digest, template));
        string scheduleMutation = Encoding.UTF8.GetString(ControlFlowOpcodeLeanEmitter.EmitFromSemanticIr(
            changedSchedule, digest, digest, template));
        string jumpValidationMutation = Encoding.UTF8.GetString(ControlFlowOpcodeLeanEmitter.EmitFromSemanticIr(
            changedJumpValidation, digest, digest, template));
        string traceOwnershipMutation = Encoding.UTF8.GetString(ControlFlowOpcodeLeanEmitter.EmitFromSemanticIr(
            changedTraceOwnership, digest, digest, template));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opcodeMutation, Is.Not.EqualTo(original));
            Assert.That(original, Does.Contain("opcode := .pc"));
            Assert.That(original, Does.Contain("fixedGas := 2"));
            Assert.That(opcodeMutation, Does.Contain("fixedGas := 3"));
            Assert.That(traceMutation, Is.Not.EqualTo(original));
            Assert.That(original, Does.Contain("tracePushWidth := 8"));
            Assert.That(traceMutation, Does.Contain("tracePushWidth := 7"));
            Assert.That(traceMutation, Does.Contain("descriptor.tracePushWidth"));
            Assert.That(scheduleMutation, Is.Not.EqualTo(original));
            Assert.That(original, Does.Contain("jumpdest := 1"));
            Assert.That(scheduleMutation, Does.Contain("jumpdest := 2"));
            Assert.That(scheduleMutation, Does.Contain("debitExecution schedule.jumpdest"));
            Assert.That(jumpValidationMutation, Is.Not.EqualTo(original));
            Assert.That(original, Does.Contain("pushFirstOpcode := 96"));
            Assert.That(jumpValidationMutation, Does.Contain("pushFirstOpcode := 97"));
            Assert.That(jumpValidationMutation, Does.Contain("extractedInstructionWidth profile"));
            Assert.That(traceOwnershipMutation, Is.Not.EqualTo(original));
            Assert.That(original, Does.Contain("endsInstructionTrace := false"));
            Assert.That(traceOwnershipMutation, Does.Contain("endsInstructionTrace := true"));
            Assert.That(traceOwnershipMutation, Does.Contain("!endsInstructionTrace"));
        }
    }

    [Test]
    public void Ir_rejects_every_property_omission_null_case_alias_duplicate_and_leaf_change()
    {
        int assertions = AssertEveryPropertyMutationRejected(
            ReadChecked(ControlFlowOpcodeProfile.IrFileName), ControlFlowOpcodeProfile.DeserializeIr);
        byte[] bytes = ReadChecked(ControlFlowOpcodeProfile.IrFileName);
        JsonNode root = JsonNode.Parse(bytes)!;
        foreach (string property in new[] { "forkLineage", "semanticBindings", "openExtractionObligations" })
            assertions += AssertScalarArrayElementMutationsRejected(
                root, [property], bytes, ControlFlowOpcodeProfile.DeserializeIr);
        JsonArray opcodes = root["opcodes"]!.AsArray();
        for (int index = 0; index < opcodes.Count; index++)
            assertions += AssertScalarArrayElementMutationsRejected(
                root, ["opcodes", index, "effectOrder"], bytes, ControlFlowOpcodeProfile.DeserializeIr);
        TestContext.Progress.WriteLine($"IR mutation assertions: {assertions}");
    }

    [Test]
    public void Manifest_rejects_every_property_omission_null_case_alias_duplicate_and_leaf_change()
    {
        byte[] bytes = ReadChecked(ControlFlowOpcodeProfile.ManifestFileName);
        int assertions = AssertEveryPropertyMutationRejected(
            bytes, ControlFlowOpcodeProfile.DeserializeManifest);
        JsonNode root = JsonNode.Parse(bytes)!;
        assertions += AssertScalarArrayElementMutationsRejected(
            root, ["semanticBindings"], bytes, ControlFlowOpcodeProfile.DeserializeManifest);
        assertions += AssertEveryDigestTextMutationRejected(root);
        TestContext.Progress.WriteLine($"Manifest mutation assertions: {assertions}");
    }

    [Test]
    public void Manifest_binds_both_artifact_digests_to_recomputed_canonical_bytes()
    {
        byte[] manifestBytes = ReadChecked(ControlFlowOpcodeProfile.ManifestFileName);
        byte[] irBytes = ReadChecked(ControlFlowOpcodeProfile.IrFileName);
        byte[] leanBytes = ReadChecked(ControlFlowOpcodeProfile.LeanFileName);
        SourceManifest manifest = ControlFlowOpcodeProfile.DeserializeManifest(manifestBytes);
        Assert.That(() => ControlFlowOpcodeProfile.ValidateArtifactBindings(manifest, irBytes, leanBytes), Throws.Nothing);

        JsonObject changedIr = JsonNode.Parse(manifestBytes)!.AsObject();
        changedIr["ir"]!["sha256"] = new string('a', 64);
        Assert.That(() => ControlFlowOpcodeProfile.DeserializeManifest(ToBytes(changedIr)),
            Throws.TypeOf<ExtractionException>());

        JsonObject changedLean = JsonNode.Parse(manifestBytes)!.AsObject();
        changedLean["lean"]!["sha256"] = new string('b', 64);
        Assert.That(() => ControlFlowOpcodeProfile.DeserializeManifest(ToBytes(changedLean)),
            Throws.TypeOf<ExtractionException>());

        byte[] forgedIr = irBytes.ReplaceBytes("\"fixedGas\": 2", "\"fixedGas\": 3");
        Assert.That(() => ControlFlowOpcodeProfile.ValidateArtifactBindings(manifest, forgedIr, leanBytes),
            Throws.TypeOf<ExtractionException>());
        byte[] forgedLean = leanBytes.ReplaceBytes("fixedGas := 2", "fixedGas := 3");
        Assert.That(() => ControlFlowOpcodeProfile.ValidateArtifactBindings(manifest, irBytes, forgedLean),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Every_admitted_source_build_selector_and_operational_reference_rejects_one_byte_change()
    {
        foreach (string path in ControlFlowOpcodeProfile.InputPaths)
        {
            using Fixture fixture = new();
            fixture.AppendByte(path, (byte)'\n');
            Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>(), path);
        }
    }

    [Test]
    public void Exact_paths_competing_owner_and_emitter_text_boundaries_fail_closed()
    {
        using (Fixture omitted = new())
        {
            omitted.Delete(HandlerPath);
            Assert.That(() => omitted.Extract(), Throws.TypeOf<ExtractionException>());
        }
        using (Fixture wrongCase = new())
        {
            wrongCase.RenameCaseOnly(HandlerPath, "virtualMachine.OpcodeHandlers.cs");
            Assert.That(() => wrongCase.Extract(), Throws.TypeOf<ExtractionException>());
        }
        using (Fixture competing = new())
        {
            competing.WriteAdditional("src/Nethermind/Nethermind.Evm/CompetingControlFlowOpcode.cs", """
                namespace Nethermind.Evm;
                public partial class VirtualMachine<TGasPolicy>
                {
                    private readonly struct JumpDestOpcode { }
                }
                """);
            Assert.That(() => competing.Extract(), Throws.TypeOf<ExtractionException>());
        }

        IrDocument document = ControlFlowOpcodeProfile.DeserializeIr(ReadChecked(ControlFlowOpcodeProfile.IrFileName));
        byte[] template = File.ReadAllBytes(Path.Combine(RepoRoot(), SpecPath));
        string digest = new('a', 64);
        Assert.That(() => ControlFlowOpcodeLeanEmitter.Emit(document, digest.ToUpperInvariant(), digest, template),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => ControlFlowOpcodeLeanEmitter.Emit(document, digest + "\n", digest, template),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => ControlFlowOpcodeLeanEmitter.Emit(document, digest, digest.ToUpperInvariant(), template),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => ControlFlowOpcodeLeanEmitter.Emit(document, digest, digest,
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(template).Replace("\n", "\r\n", StringComparison.Ordinal))),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => ControlFlowOpcodeLeanEmitter.Emit(document, digest, digest, [0xef, 0xbb, 0xbf, .. template]),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => ControlFlowOpcodeLeanEmitter.Emit(document, digest, digest,
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(template).TrimEnd('\n'))),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => ControlFlowOpcodeLeanEmitter.Emit(document, digest, digest,
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(template) + "  theorem forged : True := by trivial\n")),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => ControlFlowOpcodeLeanEmitter.Emit(document, digest, digest, [0xff, .. template]),
            Throws.TypeOf<ExtractionException>());
    }

    private static byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static int AssertScalarArrayElementMutationsRejected<T>(JsonNode root, object[] path,
        byte[] admittedBytes, Func<byte[], T> deserialize)
    {
        JsonArray array = FollowPath(root, path).AsArray();
        Assert.That(array, Is.Not.Empty, string.Join(".", path));
        int assertions = 0;
        for (int index = 0; index < array.Count; index++)
        {
            foreach (bool useNull in new[] { true, false })
            {
                JsonNode changed = JsonNode.Parse(admittedBytes)!;
                JsonArray changedArray = FollowPath(changed, path).AsArray();
                changedArray[index] = useNull ? null : ChangedValue(changedArray[index]!);
                Assert.That(() => deserialize(ToBytes(changed)), Throws.TypeOf<ExtractionException>(),
                    $"{string.Join(".", path)}[{index}] {(useNull ? "null" : "leaf change")}");
                assertions++;
            }
        }
        return assertions;
    }

    private static int AssertEveryDigestTextMutationRejected(JsonNode root)
    {
        PropertyPath[] digests = EnumerateProperties(root)
            .Where(static property => property.Name == "sha256" ||
                property.Name.EndsWith("Sha256", StringComparison.Ordinal)).ToArray();
        Assert.That(digests, Is.Not.Empty);
        int assertions = 0;
        foreach (PropertyPath digest in digests)
        foreach (Func<string, string> mutation in new Func<string, string>[]
                 {
                     static value => value.ToUpperInvariant(), static value => value + "\n",
                 })
        {
            JsonNode changed = root.DeepClone();
            JsonObject owner = LocateOwner(changed, digest);
            owner[digest.Name] = mutation(owner[digest.Name]!.GetValue<string>());
            Assert.That(() => ControlFlowOpcodeProfile.DeserializeManifest(ToBytes(changed)),
                Throws.TypeOf<ExtractionException>(), digest.Display);
            assertions++;
        }
        return assertions;
    }

    private static JsonNode FollowPath(JsonNode root, IEnumerable<object> path)
    {
        JsonNode current = root;
        foreach (object part in path)
            current = part is string property ? current[property]! : current[(int)part]!;
        return current;
    }

    private static int AssertEveryPropertyMutationRejected<T>(byte[] bytes, Func<byte[], T> deserialize)
    {
        JsonNode root = JsonNode.Parse(bytes)!;
        PropertyPath[] properties = EnumerateProperties(root).ToArray();
        Assert.That(properties, Is.Not.Empty);
        int assertions = 0;
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
                assertions++;
            }

            Assert.That(() => deserialize(DuplicateProperty(root, property)),
                Throws.TypeOf<ExtractionException>(), $"{property.Display} duplicate");
            assertions++;

            JsonNode? value = LocateOwner(root, property)[property.Name];
            if (value is JsonValue)
            {
                JsonNode changed = root.DeepClone();
                JsonObject owner = LocateOwner(changed, property);
                owner[property.Name] = ChangedValue(owner[property.Name]!);
                Assert.That(() => deserialize(ToBytes(changed)), Throws.TypeOf<ExtractionException>(),
                    $"{property.Display} leaf change");
                assertions++;
            }
        }
        return assertions;
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

    private static string GeneratedDirectory() =>
        Path.Combine(RepoRoot(), "tools/Evm/Lean/ControlFlowOpcodeExtractor/Generated");

    private enum PropertyMutation { Omit, Null, CaseAlias }

    private sealed record PathPart(string? Name, int? Index);

    private sealed record PropertyPath(IReadOnlyList<PathPart> Parent, string Name)
    {
        public string Display => string.Concat(Parent.Select(static part => part.Name ?? $"[{part.Index}]")) + "." + Name;
    }

    private static ExtractionResult Extract(string root, string output) => ControlFlowOpcodeProfile.Extract(
        root, output, Path.Combine(output, ControlFlowOpcodeProfile.LeanFileName));

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
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "control-flow-fixtures", Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            foreach (string relativePath in ControlFlowOpcodeProfile.InputPaths)
            {
                string target = Resolve(relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)), target);
            }
        }

        internal string Root { get; }
        internal string Output { get; }

        internal void Extract() => ControlFlowOpcodeProfile.Extract(
            Root, Output, Path.Combine(Output, ControlFlowOpcodeProfile.LeanFileName));

        internal void AppendByte(string relativePath, byte value)
        {
            using FileStream stream = File.Open(Resolve(relativePath), FileMode.Append, FileAccess.Write, FileShare.None);
            stream.WriteByte(value);
        }

        internal void Delete(string relativePath) => File.Delete(Resolve(relativePath));

        internal void RenameCaseOnly(string relativePath, string fileName)
        {
            string source = Resolve(relativePath);
            string directory = Path.GetDirectoryName(source)!;
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            File.Move(source, temporary);
            File.Move(temporary, Path.Combine(directory, fileName));
        }

        internal void WriteAdditional(string relativePath, string source)
        {
            string path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(false));
        }

        private string Resolve(string relativePath) =>
            Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TemporaryDirectory(string prefix) : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            TestContext.CurrentContext.WorkDirectory, prefix, Guid.NewGuid().ToString("N"));

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
