// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.Keccak256OpcodeExtractor.Test;

[TestFixture]
public sealed class Keccak256OpcodeExtractorTests
{
    private const string HandlerPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Crypto.cs";
    private const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    private const string GasPath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    private const string MemoryPath = "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs";
    private const string StackPath = "src/Nethermind/Nethermind.Evm/EvmStack.cs";

    [Test]
    public void Production_sources_extract_to_byte_identical_artifacts()
    {
        using TemporaryDirectory first = new("keccak-extractor-first");
        using TemporaryDirectory second = new("keccak-extractor-second");
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
        using TemporaryDirectory fresh = new("keccak-extractor-fresh");
        ExtractionResult result = Extract(RepoRoot(), fresh.Path);
        string generated = Path.Combine(RepoRoot(), "tools/Evm/Lean/Keccak256OpcodeExtractor/Generated");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(result.IrPath),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, Keccak256OpcodeProfile.IrFileName))));
            Assert.That(File.ReadAllBytes(result.ManifestPath),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(generated, Keccak256OpcodeProfile.ManifestFileName))));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(
                RepoRoot(), Keccak256OpcodeProfile.DefaultLeanRelativePath))));
        }
    }

    [Test]
    public void Ir_closes_all_four_dispatch_roots_and_orders_every_effect()
    {
        IrDocument document = Keccak256OpcodeProfile.DeserializeIr(ReadChecked(Keccak256OpcodeProfile.IrFileName));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.Opcode.OpcodeByte, Is.EqualTo(0x20));
            Assert.That(document.Opcode.HandlerBody, Is.EqualTo("KeccakOpcode<TTracingInst>"));
            Assert.That(document.Opcode.HasCheckedBody, Is.False);
            Assert.That(document.Opcode.StackInputs, Is.EqualTo(2));
            Assert.That(document.Opcode.StackOutputs, Is.EqualTo(1));
            Assert.That(document.Specializations.Select(static item => item.DispatchTable), Is.EqualTo(new[]
            {
                "NoTrace", "NoTraceCancelable", "Traced", "TracedCancelable",
            }));
            Assert.That(document.Specializations, Has.Length.EqualTo(4));
            Assert.That(document.Specializations.All(static item =>
                !item.ClosedRoot.Contains("TTracingInst", StringComparison.Ordinal) &&
                !item.ClosedRoot.Contains("TCancelable", StringComparison.Ordinal) &&
                !item.ClosedRoot.Contains("TGasPolicy", StringComparison.Ordinal)), Is.True);
            Assert.That(document.Opcode.EffectOrder, Is.EqualTo(new[]
            {
                "traceStartIfInstructionTracing", "incrementProgramCounter", "incrementOpcodeCount",
                "popOffsetLengthAtomically", "calculateCheckedCeilingWords", "chargeBaseAndWords",
                "rejectWordOverflowAfterCharge", "installLogicalMemoryExpansion", "chargeMemoryExpansion",
                "loadExactZeroExtendedSlice", "callHashOracle", "checkPushDepth",
                "traceHashPushIfInstructionTracing", "replaceTwoStackInputsWithHash",
                "traceEndIfInstructionTracing", "dispatchContinue",
            }));
        }
    }

    [Test]
    public void Generated_transition_is_theorem_free_and_does_not_depend_on_handwritten_transition()
    {
        string generated = File.ReadAllText(Path.Combine(RepoRoot(), Keccak256OpcodeProfile.DefaultLeanRelativePath));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Match(@"(?m)^\s*(theorem|axiom|example|admit|sorry)\b"));
            Assert.That(generated, Does.Not.Contain("import Eip803x.Evm.Keccak256"));
            Assert.That(generated, Does.Not.Contain("Keccak256.execute"));
            Assert.That(generated, Does.Contain("abbrev HashOracle := List Byte -> UInt256"));
            Assert.That(generated, Does.Contain("def executeCore"));
            Assert.That(generated, Does.Contain("def prepareMemory"));
            Assert.That(generated, Does.Contain("let input := readRange expanded.bytes offset.val length.val"));
            Assert.That(generated, Does.Contain("if invalidWordCount then"));
        }
    }

    [Test]
    public void Refinement_targets_independently_reviewed_transition_and_pins_trace_primitives()
    {
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), "tools/Evm/Lean/Keccak256OpcodeExtractor/Refinement/Keccak256Opcode.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Contain("import Eip803x.Evm.Keccak256"));
            Assert.That(source, Does.Contain("theorem execute_refines"));
            Assert.That(source, Does.Contain("theorem noTrace_root_admitted"));
            Assert.That(source, Does.Contain("theorem tracedCancelable_root_admitted"));
            Assert.That(source, Does.Contain("theorem trace_start_uses_preincrement_state"));
            Assert.That(source, Does.Contain("theorem trace_push_reports_hash"));
            Assert.That(source, Does.Contain("theorem trace_end_reports_postcharge_gas"));
        }
    }

    [Test]
    public void Ir_rejects_every_property_omission_null_case_alias_duplicate_and_leaf_change() =>
        AssertEveryPropertyMutationRejected(
            ReadChecked(Keccak256OpcodeProfile.IrFileName),
            Keccak256OpcodeProfile.DeserializeIr);

    [Test]
    public void Manifest_rejects_every_property_omission_null_case_alias_duplicate_and_leaf_change() =>
        AssertEveryPropertyMutationRejected(
            ReadChecked(Keccak256OpcodeProfile.ManifestFileName),
            Keccak256OpcodeProfile.DeserializeManifest);

    [Test]
    public void Every_manifest_digest_rejects_uppercase_and_newline_injection()
    {
        byte[] bytes = ReadChecked(Keccak256OpcodeProfile.ManifestFileName);
        JsonNode root = JsonNode.Parse(bytes)!;
        PropertyPath[] hashes = EnumerateProperties(root)
            .Where(static item => item.Name == "sha256" || item.Name.EndsWith("Sha256", StringComparison.Ordinal))
            .ToArray();
        Assert.That(hashes, Is.Not.Empty);
        foreach (PropertyPath hash in hashes)
        {
            JsonNode upper = root.DeepClone();
            JsonObject owner = LocateOwner(upper, hash);
            owner[hash.Name] = owner[hash.Name]!.GetValue<string>().ToUpperInvariant();
            Assert.That(() => Keccak256OpcodeProfile.DeserializeManifest(ToBytes(upper)),
                Throws.TypeOf<ExtractionException>(), hash.Display + " uppercase");

            JsonNode newline = root.DeepClone();
            owner = LocateOwner(newline, hash);
            owner[hash.Name] = owner[hash.Name]!.GetValue<string>() + "\n";
            Assert.That(() => Keccak256OpcodeProfile.DeserializeManifest(ToBytes(newline)),
                Throws.TypeOf<ExtractionException>(), hash.Display + " newline");
        }
    }

    [TestCase("A", TestName = "EmitterRejectsUppercaseIrDigest")]
    [TestCase("\n", TestName = "EmitterRejectsNewlineIrDigest")]
    public void Emitter_rejects_noncanonical_ir_digest(string injection)
    {
        IrDocument document = Keccak256OpcodeProfile.DeserializeIr(ReadChecked(Keccak256OpcodeProfile.IrFileName));
        string valid = new('a', 64);
        string invalid = injection == "A" ? valid.ToUpperInvariant() : valid + injection;
        Assert.That(() => Keccak256OpcodeLeanEmitter.Emit(document, invalid, valid),
            Throws.TypeOf<ExtractionException>());
    }

    [TestCase("A", TestName = "EmitterRejectsUppercaseSourceDigest")]
    [TestCase("\n", TestName = "EmitterRejectsNewlineSourceDigest")]
    public void Emitter_rejects_noncanonical_source_digest(string injection)
    {
        IrDocument document = Keccak256OpcodeProfile.DeserializeIr(ReadChecked(Keccak256OpcodeProfile.IrFileName));
        string valid = new('a', 64);
        string invalid = injection == "A" ? valid.ToUpperInvariant() : valid + injection;
        Assert.That(() => Keccak256OpcodeLeanEmitter.Emit(document, valid, invalid),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Every_admitted_source_and_build_input_rejects_a_one_byte_change()
    {
        foreach (string path in Keccak256OpcodeProfile.InputPaths)
        {
            using Fixture fixture = new();
            fixture.AppendByte(path, (byte)'\n');
            Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>(), path);
        }
    }

    [Test]
    public void Required_source_paths_are_case_sensitive()
    {
        using Fixture fixture = new();
        fixture.ChangeFileNameCase(HandlerPath, "evminstructions.crypto.cs");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Competing_partial_declarations_are_rejected()
    {
        using Fixture fixture = new();
        fixture.WriteAdditional("src/Nethermind/Nethermind.Evm/Instructions/CompetingKeccak.cs", """
            namespace Nethermind.Evm;
            public static partial class EvmInstructions
            {
                public static EvmExceptionType InstructionKeccak256<TGasPolicy, TTracingInst>(
                    ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm) => default;
            }
            """);
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [TestCase(HandlerPath, "TGasPolicy.TryConsumeKeccak(ref gas, words)", "if (outOfGas) goto OutOfGas;")]
    [TestCase(DispatchPath, "pc++;", "opCodeCount++;")]
    [TestCase(MemoryPath, "ulong activeWords = Size >> 5;", "Size = newActiveWords << 5;")]
    [TestCase(StackPath, "Head = newHead;", "ReadUInt256FromSlot(ref baseRef, out value);")]
    public void Reviewed_control_flow_rejects_order_changes(string path, string first, string second)
    {
        using Fixture fixture = new();
        fixture.Swap(path, first, second);
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [TestCase(HandlerPath, "KeccakCache.ComputeTo(bytes, out ValueHash256 keccak);",
        "KeccakCache.ComputeTo(default, out ValueHash256 keccak);")]
    [TestCase(GasPath, "GasCostOf.Sha3 + GasCostOf.Sha3Word * words",
        "GasCostOf.Sha3 + words")]
    [TestCase(MemoryPath, "data = LoadSpan(newLength, TruncateToInt32(location.u0), TruncateToInt32(length.u0));",
        "data = default;")]
    public void Reviewed_semantic_edges_reject_source_changes(string path, string before, string after)
    {
        using Fixture fixture = new();
        fixture.Replace(path, before, after);
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Preexisting_forged_artifacts_cannot_mask_a_source_change()
    {
        using Fixture fixture = new();
        Directory.CreateDirectory(fixture.Output);
        File.WriteAllText(Path.Combine(fixture.Output, Keccak256OpcodeProfile.IrFileName), "{}");
        File.WriteAllText(Path.Combine(fixture.Output, Keccak256OpcodeProfile.ManifestFileName), "{}");
        fixture.AppendByte(HandlerPath, (byte)'\n');
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
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

            byte[] duplicate = DuplicateProperty(root, property);
            Assert.That(() => deserialize(duplicate), Throws.TypeOf<ExtractionException>(),
                $"{property.Display} duplicate");

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
        using (Utf8JsonWriter writer = new(stream))
            WriteNode(writer, root, [], target);
        return stream.ToArray();
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node, IReadOnlyList<PathPart> path, PropertyPath target)
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

    private static byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static byte[] ReadChecked(string fileName) => File.ReadAllBytes(Path.Combine(
        RepoRoot(), "tools/Evm/Lean/Keccak256OpcodeExtractor/Generated", fileName));

    private static ExtractionResult Extract(string root, string output) => Keccak256OpcodeProfile.Extract(
        root, output, Path.Combine(output, "Keccak256OpcodeKernel.lean"));

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

    private enum PropertyMutation { Omit, Null, CaseAlias }

    private sealed record PathPart(string? Name, int? Index);

    private sealed record PropertyPath(IReadOnlyList<PathPart> Parent, string Name)
    {
        public string Display => string.Concat(Parent.Select(static part => part.Name ?? $"[{part.Index}]")) + "." + Name;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "keccak-extractor-fixtures", Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            foreach (string relativePath in Keccak256OpcodeProfile.InputPaths)
            {
                string target = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)), target);
            }
        }

        public string Root { get; }
        public string Output { get; }

        public void Extract() => Keccak256OpcodeProfile.Extract(
            Root, Output, Path.Combine(Output, "Keccak256OpcodeKernel.lean"));

        public void AppendByte(string relativePath, byte value)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            using FileStream stream = File.Open(path, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.WriteByte(value);
        }

        public void Replace(string relativePath, string before, string after)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string source = File.ReadAllText(path);
            Assert.That(source, Does.Contain(before));
            File.WriteAllText(path, source.Replace(before, after, StringComparison.Ordinal), new UTF8Encoding(false));
        }

        public void ChangeFileNameCase(string relativePath, string changedName)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string temporary = path + ".case-change";
            File.Move(path, temporary);
            File.Move(temporary, Path.Combine(Path.GetDirectoryName(path)!, changedName));
        }

        public void WriteAdditional(string relativePath, string source)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(false));
        }

        public void Swap(string relativePath, string first, string second)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string source = File.ReadAllText(path);
            int firstPosition = source.IndexOf(first, StringComparison.Ordinal);
            int secondPosition = source.IndexOf(second, StringComparison.Ordinal);
            Assert.That(firstPosition, Is.GreaterThanOrEqualTo(0));
            Assert.That(secondPosition, Is.GreaterThan(firstPosition));
            source = source.Remove(secondPosition, second.Length).Insert(secondPosition, first);
            source = source.Remove(firstPosition, first.Length).Insert(firstPosition, second);
            File.WriteAllText(path, source, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TemporaryDirectory(string prefix) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            TestContext.CurrentContext.WorkDirectory, prefix, Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
