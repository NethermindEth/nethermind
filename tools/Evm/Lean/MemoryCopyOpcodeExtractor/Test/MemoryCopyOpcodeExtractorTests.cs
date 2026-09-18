// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.MemoryCopyOpcodeExtractor.Test;

[TestFixture]
public sealed class MemoryCopyOpcodeExtractorTests
{
    private const string HandlerPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs";
    private const string SpecPath = "tools/Evm/Lean/MemoryCopyOpcodeExtractor/Specification/MemoryCopyExecution.lean";

    [Test]
    public void Production_sources_extract_to_byte_identical_artifacts()
    {
        using TemporaryDirectory first = new("memory-copy-extractor-first");
        using TemporaryDirectory second = new("memory-copy-extractor-second");
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
        using TemporaryDirectory fresh = new("memory-copy-extractor-fresh");
        ExtractionResult result = Extract(RepoRoot(), fresh.Path);
        string generated = Path.Combine(RepoRoot(), "tools/Evm/Lean/MemoryCopyOpcodeExtractor/Generated");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(result.IrPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, MemoryCopyOpcodeProfile.IrFileName))));
            Assert.That(File.ReadAllBytes(result.ManifestPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, MemoryCopyOpcodeProfile.ManifestFileName))));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, MemoryCopyOpcodeProfile.LeanFileName))));
        }
    }

    [Test]
    public void Ir_closes_nine_opcodes_over_four_dispatch_tables()
    {
        IrDocument document = MemoryCopyOpcodeProfile.DeserializeIr(ReadChecked(MemoryCopyOpcodeProfile.IrFileName));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.Opcodes, Has.Length.EqualTo(9));
            Assert.That(document.Specializations, Has.Length.EqualTo(36));
            Assert.That(document.Opcodes.Select(static opcode => opcode.OpcodeByte),
                Is.EquivalentTo(new[] { 0x37, 0x39, 0x3e, 0x51, 0x52, 0x53, 0x59, 0x5a, 0x5e }));
            Assert.That(document.Specializations.GroupBy(static item => item.Opcode).All(static group => group.Count() == 4), Is.True);
            Assert.That(document.Specializations.All(static item =>
                !item.ClosedRoot.Contains("TGasPolicy", StringComparison.Ordinal) &&
                !item.ClosedRoot.Contains("TTracingInst", StringComparison.Ordinal) &&
                !item.ClosedRoot.Contains("TCancelable", StringComparison.Ordinal)), Is.True);
            Assert.That(document.Specializations.Select(static item => item.ClosedRoot).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(36));
        }
    }

    [Test]
    public void Ir_pins_order_sensitive_operational_edges()
    {
        IrDocument document = MemoryCopyOpcodeProfile.DeserializeIr(ReadChecked(MemoryCopyOpcodeProfile.IrFileName));
        OpcodeDescriptor mload = document.Opcodes.Single(static opcode => opcode.Name == "mload");
        OpcodeDescriptor returnCopy = document.Opcodes.Single(static opcode => opcode.Name == "returndatacopy");
        OpcodeDescriptor mcopy = document.Opcodes.Single(static opcode => opcode.Name == "mcopy");
        OpcodeDescriptor gas = document.Opcodes.Single(static opcode => opcode.Name == "gas");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mload.EffectOrder, Is.EqualTo(new[] { "chargeVeryLow", "checkDepth1", "peekOffset", "installExpansion", "chargeExpansion", "load32", "traceParityMemory", "replaceTop", "tracePush" }));
            Assert.That(returnCopy.EffectOrder, Does.Contain("rejectAccessBeforeExpansion"));
            Assert.That(Array.IndexOf(returnCopy.EffectOrder, "rejectAccessBeforeExpansion"), Is.LessThan(Array.IndexOf(returnCopy.EffectOrder, "installExpansion")));
            Assert.That(mcopy.EffectOrder, Does.Contain("traceSource"));
            Assert.That(Array.IndexOf(mcopy.EffectOrder, "traceSource"), Is.LessThan(Array.IndexOf(mcopy.EffectOrder, "memmove")));
            Assert.That(gas.EffectOrder, Does.Contain("readPostChargeExecutionGas"));
        }
    }

    [Test]
    public void Generated_transition_is_theorem_free_and_does_not_reuse_handwritten_transition()
    {
        string generated = File.ReadAllText(Path.Combine(GeneratedDirectory(), MemoryCopyOpcodeProfile.LeanFileName));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Match(@"(?m)^\s*(theorem|axiom|example|admit|sorry)\b"));
            Assert.That(generated, Does.Not.Contain("MemoryStackControl.step"));
            Assert.That(generated, Does.Not.Contain("MemoryCopyExecution.execute"));
            Assert.That(generated, Does.Not.Contain("def beginInstruction"));
            Assert.That(generated, Does.Not.Contain("def executeMload"));
            Assert.That(generated, Does.Contain("def runLoad"));
            Assert.That(generated, Does.Contain("def runReturnDataCopy"));
            Assert.That(generated, Does.Contain("def runMemoryCopy"));
            Assert.That(generated, Does.Contain("def executeSemantic"));
            Assert.That(generated, Does.Contain("def closeFailureTrace"));
        }
    }

    [Test]
    public void Generated_executable_is_a_function_of_semantic_ir_not_the_operational_spec_body()
    {
        IrDocument admitted = MemoryCopyOpcodeProfile.DeserializeIr(ReadChecked(MemoryCopyOpcodeProfile.IrFileName));
        OpcodeDescriptor[] changedOpcodes = [.. admitted.Opcodes];
        OpcodeDescriptor mload = changedOpcodes.Single(static opcode => opcode.Name == "mload");
        changedOpcodes[Array.IndexOf(changedOpcodes, mload)] = mload with
        {
            Semantics = mload.Semantics with { AccessWidth = 31 },
        };
        IrDocument changed = admitted with { Opcodes = changedOpcodes };
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
        string digest = new('a', 64);

        string original = Encoding.UTF8.GetString(
            MemoryCopyOpcodeLeanEmitter.EmitFromSemanticIr(admitted, digest, digest));
        string mutated = Encoding.UTF8.GetString(
            MemoryCopyOpcodeLeanEmitter.EmitFromSemanticIr(changed, digest, digest));
        string scheduleMutated = Encoding.UTF8.GetString(
            MemoryCopyOpcodeLeanEmitter.EmitFromSemanticIr(changedSchedule, digest, digest));
        string gasClosureMutated = Encoding.UTF8.GetString(
            MemoryCopyOpcodeLeanEmitter.EmitFromSemanticIr(changedGasClosure, digest, digest));
        string traceClosureMutated = Encoding.UTF8.GetString(
            MemoryCopyOpcodeLeanEmitter.EmitFromSemanticIr(changedTraceClosure, digest, digest));
        string faultPcMutated = Encoding.UTF8.GetString(
            MemoryCopyOpcodeLeanEmitter.EmitFromSemanticIr(changedFaultPc, digest, digest));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mutated, Is.Not.EqualTo(original));
            Assert.That(original, Does.Contain("accessWidth := 32"));
            Assert.That(mutated, Does.Contain("accessWidth := 31"));
            Assert.That(mutated, Does.Contain("Eip803x.Evm.Word.ofNat semantic.accessWidth"));
            Assert.That(scheduleMutated, Is.Not.EqualTo(original));
            Assert.That(original, Does.Contain("veryLow := 3"));
            Assert.That(scheduleMutated, Does.Contain("veryLow := 4"));
            Assert.That(scheduleMutated, Does.Contain("def executeAmsterdam"));
            Assert.That(gasClosureMutated, Is.Not.EqualTo(original));
            Assert.That(gasClosureMutated, Does.Contain("let state := result.state"));
            Assert.That(traceClosureMutated, Is.Not.EqualTo(original));
            Assert.That(traceClosureMutated, Does.Contain("[.error result.status, .finish state.gas.gasLeft]"));
            Assert.That(faultPcMutated, Is.Not.EqualTo(original));
            Assert.That(faultPcMutated, Does.Contain("def faultPc (result : Outcome) : Nat :=\n  result.state.pc"));
            Assert.That(original, Does.Contain("def executeAmsterdamClosed"));
        }
    }

    [Test]
    public void Ir_rejects_every_property_omission_null_case_alias_duplicate_and_leaf_change() =>
        AssertEveryPropertyMutationRejected(
            ReadChecked(MemoryCopyOpcodeProfile.IrFileName), MemoryCopyOpcodeProfile.DeserializeIr);

    [Test]
    public void Manifest_rejects_every_property_omission_null_case_alias_duplicate_and_leaf_change() =>
        AssertEveryPropertyMutationRejected(
            ReadChecked(MemoryCopyOpcodeProfile.ManifestFileName), MemoryCopyOpcodeProfile.DeserializeManifest);

    [Test]
    public void Ir_rejects_null_and_changed_scalar_array_elements()
    {
        byte[] bytes = ReadChecked(MemoryCopyOpcodeProfile.IrFileName);
        JsonNode root = JsonNode.Parse(bytes)!;
        foreach (string property in new[] { "forkLineage", "openExtractionObligations" })
            AssertScalarArrayElementMutationsRejected(root, [property], bytes, MemoryCopyOpcodeProfile.DeserializeIr);

        JsonArray opcodes = root["opcodes"]!.AsArray();
        for (int index = 0; index < opcodes.Count; index++)
            AssertScalarArrayElementMutationsRejected(root, ["opcodes", index, "effectOrder"], bytes,
                MemoryCopyOpcodeProfile.DeserializeIr);
    }

    [Test]
    public void Manifest_rejects_null_and_changed_semantic_binding_elements()
    {
        byte[] bytes = ReadChecked(MemoryCopyOpcodeProfile.ManifestFileName);
        JsonNode root = JsonNode.Parse(bytes)!;
        AssertScalarArrayElementMutationsRejected(root, ["semanticBindings"], bytes,
            MemoryCopyOpcodeProfile.DeserializeManifest);
    }

    [TestCase("ir")]
    [TestCase("lean")]
    public void Manifest_rejects_alternate_valid_artifact_digest(string artifact)
    {
        JsonObject manifest = JsonNode.Parse(ReadChecked(MemoryCopyOpcodeProfile.ManifestFileName))!.AsObject();
        manifest[artifact]!["sha256"] = new string('a', 64);
        Assert.That(() => MemoryCopyOpcodeProfile.DeserializeManifest(ToBytes(manifest)),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Manifest_rejects_uppercase_and_newline_in_every_digest()
    {
        JsonNode admitted = JsonNode.Parse(ReadChecked(MemoryCopyOpcodeProfile.ManifestFileName))!;
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
                Assert.That(() => MemoryCopyOpcodeProfile.DeserializeManifest(ToBytes(changed)),
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
        IrDocument document = MemoryCopyOpcodeProfile.DeserializeIr(ReadChecked(MemoryCopyOpcodeProfile.IrFileName));
        string valid = new('a', 64);
        string invalid = injection == "A" ? valid.ToUpperInvariant() : valid + injection;
        Assert.That(() => MemoryCopyOpcodeLeanEmitter.Emit(
            document, target == "ir" ? invalid : valid, target == "source" ? invalid : valid),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Every_admitted_source_build_and_operational_template_rejects_one_byte_change()
    {
        foreach (string path in MemoryCopyOpcodeProfile.InputPaths)
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
        fixture.WriteAdditional("src/Nethermind/Nethermind.Evm/CompetingMemoryOpcode.cs", """
            namespace Nethermind.Evm;
            public partial class VirtualMachine<TGasPolicy>
            {
                private readonly struct MLoadOpcode<TTracingInst> { }
            }
            """);
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("gasLeft := 0", "gasLeft := 1", TestName = "MutationRejectsGasExhaustion")]
    [TestCase("else if length.val = 0 then withStatus .ok", "else if false then withStatus .ok", TestName = "MutationRejectsZeroLengthGate")]
    [TestCase("rejectAccessBeforeExpansion", "installExpansionBeforeAccess", TestName = "MutationRejectsReturnDataOrderClaim")]
    public void Operational_and_ir_mutations_cannot_reuse_admission(string before, string after)
    {
        using Fixture fixture = new();
        string path = before == "rejectAccessBeforeExpansion" ?
            Path.Combine(fixture.Output, "forged.ir") : SpecPath;
        if (path == SpecPath)
        {
            fixture.Replace(SpecPath, before, after);
            Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
        }
        else
        {
            IrDocument document = MemoryCopyOpcodeProfile.DeserializeIr(ReadChecked(MemoryCopyOpcodeProfile.IrFileName));
            byte[] mutated = MemoryCopyOpcodeProfile.Serialize(document) .ReplaceBytes(before, after);
            Assert.That(() => MemoryCopyOpcodeProfile.DeserializeIr(mutated), Throws.TypeOf<ExtractionException>());
        }
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

    private static string GeneratedDirectory() => Path.Combine(RepoRoot(), "tools/Evm/Lean/MemoryCopyOpcodeExtractor/Generated");

    private enum PropertyMutation { Omit, Null, CaseAlias }

    private sealed record PathPart(string? Name, int? Index);

    private sealed record PropertyPath(IReadOnlyList<PathPart> Parent, string Name)
    {
        public string Display => string.Concat(Parent.Select(static part => part.Name ?? $"[{part.Index}]")) + "." + Name;
    }

    private static ExtractionResult Extract(string root, string output) => MemoryCopyOpcodeProfile.Extract(
        root, output, Path.Combine(output, MemoryCopyOpcodeProfile.LeanFileName));

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
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "memory-copy-fixtures", Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            foreach (string relativePath in MemoryCopyOpcodeProfile.InputPaths)
            {
                string target = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)), target);
            }
        }

        internal string Root { get; }
        internal string Output { get; }

        internal void Extract() => MemoryCopyOpcodeProfile.Extract(Root, Output, Path.Combine(Output, MemoryCopyOpcodeProfile.LeanFileName));

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
