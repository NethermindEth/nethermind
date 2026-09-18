// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.PushOpcodeExtractor.Test;

[TestFixture]
public class PushOpcodeExtractorTests
{
    [Test]
    public void Production_extraction_is_deterministic_and_complete()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        ExtractionResult firstResult = PushOpcodeProfile.Extract(root, first.Path, Path.Combine(first.Path, "PushOpcodeKernel.lean"));
        ExtractionResult secondResult = PushOpcodeProfile.Extract(root, second.Path, Path.Combine(second.Path, "PushOpcodeKernel.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.SourceCount, Is.EqualTo(42));
            Assert.That(firstResult.OpcodeCount, Is.EqualTo(33));
            Assert.That(firstResult.SpecializationCount, Is.EqualTo(132));
            Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.IrPath)));
            Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.ManifestPath)));
            Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.LeanPath)));
            Assert.That(File.ReadAllText(firstResult.LeanPath), Does.Not.Contain("theorem "));
            Assert.That(File.ReadAllText(firstResult.LeanPath), Does.Not.Contain("Specification.PushOpcode"));
        }
    }

    [Test]
    public void Checked_artifacts_match_fresh_extraction()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory temporary = new();
        ExtractionResult result = PushOpcodeProfile.Extract(root, temporary.Path, Path.Combine(temporary.Path, "PushOpcodeKernel.lean"));
        string checkedDirectory = Path.Combine(root, PushOpcodeProfile.DefaultOutputRelativePath);
        Assert.That(File.ReadAllBytes(result.IrPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, PushOpcodeProfile.IrFileName))));
        Assert.That(File.ReadAllBytes(result.ManifestPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, PushOpcodeProfile.ManifestFileName))));
        Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(File.ReadAllBytes(Path.Combine(checkedDirectory, "PushOpcodeKernel.lean"))));
    }

    [Test]
    public void Serialized_ir_is_strict_and_every_scalar_coordinate_is_bound()
    {
        byte[] bytes = CheckedBytes(PushOpcodeProfile.IrFileName);
        IrDocument expected = PushOpcodeProfile.DeserializeIr(bytes);
        JsonObject unknown = Parse(bytes);
        unknown["unknown"] = true;
        AssertIrRejected(Encoding.UTF8.GetBytes(unknown.ToJsonString()), expected);
        Assert.That(() => PushOpcodeProfile.DeserializeIr("{"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => PushOpcodeProfile.DeserializeIr("null"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => PushOpcodeProfile.DeserializeIr(null!), Throws.TypeOf<ExtractionException>());

        JsonObject cased = Parse(bytes);
        Rename(cased["opcodes"]!.AsArray()[0]!.AsObject(), "fixedGas", "FixedGas");
        AssertIrRejected(Encoding.UTF8.GetBytes(cased.ToJsonString()), expected);
        JsonObject omittedFalse = Parse(bytes);
        omittedFalse["opcodes"]!.AsArray()[2]!.AsObject().Remove("hasCheckedBodyWhenUntraced");
        AssertIrRejected(Encoding.UTF8.GetBytes(omittedFalse.ToJsonString()), expected);
        JsonObject omittedZero = Parse(bytes);
        omittedZero["opcodes"]!.AsArray()[0]!.AsObject().Remove("immediateBytes");
        AssertIrRejected(Encoding.UTF8.GetBytes(omittedZero.ToJsonString()), expected);
        Action<JsonObject>[] nullMutations =
        [
            root => root["reachability"] = null,
            root => root["opcodes"] = null,
            root => root["opcodes"]!.AsArray()[0] = null,
            root => root["opcodes"]!.AsArray()[0]!["orderedEffects"] = null,
            root => root["specializations"]!.AsArray()[0] = null,
            root => root["push2Fusion"] = null,
            root => root["push2Fusion"]!["orderedEffects"] = null,
            root => root["forkLineage"]!.AsArray()[0] = null,
            root => root["openExtractionObligations"]!.AsArray()[0] = null,
        ];
        foreach (Action<JsonObject> mutation in nullMutations)
        {
            JsonObject candidate = Parse(bytes);
            mutation(candidate);
            AssertIrRejected(Encoding.UTF8.GetBytes(candidate.ToJsonString()), expected);
        }

        foreach (string path in EnumerateScalarPaths(Parse(bytes)))
        {
            JsonObject candidate = Parse(bytes);
            MutateScalarAtPath(candidate, path);
            AssertIrRejected(Encoding.UTF8.GetBytes(candidate.ToJsonString()), expected);
        }
    }

    [Test]
    public void Serialized_ir_rejects_duplicate_out_of_range_missing_and_unbound_roots()
    {
        byte[] bytes = CheckedBytes(PushOpcodeProfile.IrFileName);
        IrDocument expected = PushOpcodeProfile.DeserializeIr(bytes);
        Action<JsonObject>[] mutations =
        [
            root => root["opcodes"]!.AsArray()[1]!["opcodeByte"] = root["opcodes"]!.AsArray()[0]!["opcodeByte"]!.DeepClone(),
            root => root["opcodes"]!.AsArray()[0]!["opcodeByte"] = 256,
            root => root["opcodes"]!.AsArray().RemoveAt(0),
            root => root["specializations"]!.AsArray()[1] = root["specializations"]!.AsArray()[0]!.DeepClone(),
            root => root["specializations"]!.AsArray().RemoveAt(0),
            root => root["specializations"]!.AsArray()[0]!["closedRoot"] = "VirtualMachine<TGasPolicy>.ExecuteOpcode<TTracingInst>",
        ];
        foreach (Action<JsonObject> mutation in mutations)
        {
            JsonObject candidate = Parse(bytes);
            mutation(candidate);
            AssertIrRejected(Encoding.UTF8.GetBytes(candidate.ToJsonString()), expected);
        }
    }

    [Test]
    public void Serialized_manifest_is_strict_and_binds_every_lineage_field()
    {
        byte[] bytes = CheckedBytes(PushOpcodeProfile.ManifestFileName);
        SourceManifest expected = PushOpcodeProfile.DeserializeManifest(bytes);
        JsonObject unknown = Parse(bytes);
        unknown["unknown"] = true;
        AssertManifestRejected(Encoding.UTF8.GetBytes(unknown.ToJsonString()), expected);
        Assert.That(() => PushOpcodeProfile.DeserializeManifest("{"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => PushOpcodeProfile.DeserializeManifest("null"u8.ToArray()), Throws.TypeOf<ExtractionException>());
        Assert.That(() => PushOpcodeProfile.DeserializeManifest(null!), Throws.TypeOf<ExtractionException>());

        JsonObject cased = Parse(bytes);
        Rename(cased["admissions"]!.AsArray()[0]!.AsObject(), "key", "Key");
        AssertManifestRejected(Encoding.UTF8.GetBytes(cased.ToJsonString()), expected);
        JsonObject omitted = Parse(bytes);
        omitted.Remove("schemaVersion");
        AssertManifestRejected(Encoding.UTF8.GetBytes(omitted.ToJsonString()), expected);
        Action<JsonObject>[] nullMutations =
        [
            root => root["sources"] = null,
            root => root["sources"]!.AsArray()[0] = null,
            root => root["admissions"]!.AsArray()[0] = null,
            root => root["ir"] = null,
            root => root["lean"] = null,
            root => root["semanticBindings"]!.AsArray()[0] = null,
        ];
        foreach (Action<JsonObject> mutation in nullMutations)
        {
            JsonObject candidate = Parse(bytes);
            mutation(candidate);
            AssertManifestRejected(Encoding.UTF8.GetBytes(candidate.ToJsonString()), expected);
        }

        foreach (string path in EnumerateScalarPaths(Parse(bytes)))
        {
            JsonObject candidate = Parse(bytes);
            MutateScalarAtPath(candidate, path);
            AssertManifestRejected(Encoding.UTF8.GetBytes(candidate.ToJsonString()), expected);
        }

        JsonObject duplicate = Parse(bytes);
        duplicate["admissions"]!.AsArray()[1] = duplicate["admissions"]!.AsArray()[0]!.DeepClone();
        AssertManifestRejected(Encoding.UTF8.GetBytes(duplicate.ToJsonString()), expected);

        JsonObject uppercaseDigest = Parse(bytes);
        uppercaseDigest["sources"]!.AsArray()[0]!["sha256"] =
            uppercaseDigest["sources"]!.AsArray()[0]!["sha256"]!.GetValue<string>().ToUpperInvariant();
        AssertManifestRejected(Encoding.UTF8.GetBytes(uppercaseDigest.ToJsonString()), expected);

        JsonObject newlineDigest = Parse(bytes);
        newlineDigest["ir"]!["sha256"] = newlineDigest["ir"]!["sha256"]!.GetValue<string>() + "\n";
        AssertManifestRejected(Encoding.UTF8.GetBytes(newlineDigest.ToJsonString()), expected);
    }

    [Test]
    public void Lean_emitter_rejects_malformed_uppercase_and_newline_ir_digests()
    {
        IrDocument document = PushOpcodeProfile.DeserializeIr(CheckedBytes(PushOpcodeProfile.IrFileName));
        string[] invalidDigests = ["not-a-digest", new string('A', 64), new string('a', 63) + "\n"];

        foreach (string digest in invalidDigests)
            Assert.That(() => LeanEmitter.Emit(document, digest), Throws.TypeOf<ExtractionException>());
    }

    [TestCase(PushOpcodeProfile.InstructionPath, "PUSH32 = 0x7f", "PUSH32 = 0x7e", TestName = "Rejects_opcode_byte_drift")]
    [TestCase(PushOpcodeProfile.HandlersPath,
        "lookup[(int)Instruction.PUSH1] = OpcodeHandler<PushOpcode<EvmInstructions.Op1, TTracingInst>, TTracingInst, TCancelable>();",
        "lookup[(int)Instruction.PUSH1] = OpcodeHandler<PushOpcode<EvmInstructions.Op3, TTracingInst>, TTracingInst, TCancelable>();",
        TestName = "Rejects_wrong_handler_route")]
    [TestCase(PushOpcodeProfile.HandlersPath, "if (spec.IncludePush0Instruction)", "if (!spec.IncludePush0Instruction)",
        TestName = "Rejects_push0_gate_drift")]
    [TestCase(PushOpcodeProfile.HandlersPath, "public static int PushSize => 0;", "public static int PushSize => 1;",
        TestName = "Rejects_push0_wrapper_drift")]
    [TestCase(PushOpcodeProfile.DispatchPath, "pc++;\n        opCodeCount++;", "opCodeCount++;\n        pc++;",
        TestName = "Rejects_dispatch_pc_count_order_drift")]
    [TestCase(PushOpcodeProfile.StackInstructionsPath,
        "if (!TGasPolicy.UpdateGas<VeryLowGasCost>(ref gas)) return EvmExceptionType.OutOfGas;",
        "if (!TGasPolicy.UpdateGas<BaseGasCost>(ref gas)) return EvmExceptionType.OutOfGas;",
        TestName = "Rejects_push_gas_drift")]
    [TestCase(PushOpcodeProfile.StackInstructionsPath, "if (remainingCode <= Size)", "if (remainingCode < Size)",
        TestName = "Rejects_push2_terminal_drift")]
    [TestCase(PushOpcodeProfile.StackPath, "WordSize - pushSize", "WordSize - pushSize + 1",
        TestName = "Rejects_truncated_padding_drift")]
    [TestCase(PushOpcodeProfile.ReleaseExtensionsPath, "spec.IsEip3855Enabled", "false",
        TestName = "Rejects_activation_projection_drift")]
    [TestCase(PushOpcodeProfile.NamedReleaseSpecPath, "new TSelf()", "default!",
        TestName = "Rejects_named_fork_singleton_drift")]
    [TestCase(PushOpcodeProfile.StandardVirtualMachinePath, "new OpcodeTable()", "new OpcodeTable(null!)",
        TestName = "Rejects_prepopulated_table_factory_drift")]
    [TestCase(PushOpcodeProfile.MainnetDiPath, "AddScoped<IVirtualMachine, EthereumVirtualMachine>()", "AddScoped<IVirtualMachine, NullVirtualMachine>()",
        TestName = "Rejects_mainnet_vm_registration_drift")]
    public void Production_semantic_mutations_fail_closed(string path, string original, string replacement)
    {
        using Fixture fixture = new();
        fixture.Replace(path, original, replacement);
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Duplicate_dispatch_assignment_fails_closed()
    {
        using Fixture fixture = new();
        string assignment = "        lookup[(int)Instruction.PUSH1] = OpcodeHandler<PushOpcode<EvmInstructions.Op1, TTracingInst>, TTracingInst, TCancelable>();";
        fixture.Replace(PushOpcodeProfile.HandlersPath, assignment, assignment + "\n" + assignment);
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Early_return_dead_local_and_disabled_dispatch_anchors_fail_closed()
    {
        const string assignment = "        lookup[(int)Instruction.PUSH1] = OpcodeHandler<PushOpcode<EvmInstructions.Op1, TTracingInst>, TTracingInst, TCancelable>();";

        using Fixture earlyReturn = new();
        earlyReturn.Replace(PushOpcodeProfile.HandlersPath, assignment, "        if (false) return lookup;\n" + assignment);
        Assert.That(() => earlyReturn.Extract(), Throws.TypeOf<ExtractionException>());

        using Fixture deadLocal = new();
        deadLocal.Replace(PushOpcodeProfile.HandlersPath, assignment,
            "        void DeadPushAnchor()\n        {\n" + assignment + "\n        }\n");
        Assert.That(() => deadLocal.Extract(), Throws.TypeOf<ExtractionException>());

        using Fixture disabled = new();
        disabled.Replace(PushOpcodeProfile.HandlersPath, assignment, "#if false\n" + assignment + "\n#endif");
        Assert.That(() => disabled.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Fork_parent_and_deactivation_mutations_fail_closed()
    {
        using Fixture parent = new();
        parent.Replace("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs", "BPO2.Instance", "BPO1.Instance");
        Assert.That(() => parent.Extract(), Throws.TypeOf<ExtractionException>());

        using Fixture activation = new();
        activation.Replace("src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs", "spec.IsEip3855Enabled = true;", "spec.IsEip3855Enabled = false;");
        Assert.That(() => activation.Extract(), Throws.TypeOf<ExtractionException>());

        using Fixture deactivation = new();
        deactivation.Replace("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs", "spec.Name = \"Amsterdam\";",
            "spec.Name = \"Amsterdam\";\n        spec.IsEip3855Enabled = false;");
        Assert.That(() => deactivation.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Standard_and_zk_build_selector_mutations_fail_closed()
    {
        using Fixture standard = new();
        standard.Replace(PushOpcodeProfile.BuildTargetsPath, "'$(EnableZkEvm)' != 'true'", "'$(EnableZkEvm)' == 'true'");
        Assert.That(() => standard.Extract(), Throws.TypeOf<ExtractionException>());

        using Fixture zk = new();
        zk.Replace(PushOpcodeProfile.BuildTargetsPath, "<Compile Remove=\"**/*.std.cs\" />", "<Compile Remove=\"**/*.zkevm.cs\" />");
        Assert.That(() => zk.Extract(), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Arbitrary_admitted_source_change_fails_closed()
    {
        using Fixture fixture = new();
        fixture.Append(PushOpcodeProfile.TypeFlagsPath, "\n");
        Assert.That(() => fixture.Extract(),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("Complete-source admission fingerprint changed"));
    }

    private static void AssertIrRejected(byte[] candidate, IrDocument expected) =>
        Assert.That(() => PushOpcodeProfile.ValidateIr(PushOpcodeProfile.DeserializeIr(candidate), expected),
            Throws.TypeOf<ExtractionException>());

    private static void AssertManifestRejected(byte[] candidate, SourceManifest expected) =>
        Assert.That(() => PushOpcodeProfile.ValidateManifest(PushOpcodeProfile.DeserializeManifest(candidate), expected),
            Throws.TypeOf<ExtractionException>());

    private static byte[] CheckedBytes(string name) => File.ReadAllBytes(Path.Combine(
        FindRepoRoot(), PushOpcodeProfile.DefaultOutputRelativePath, name));

    private static JsonObject Parse(byte[] bytes) => JsonNode.Parse(bytes)!.AsObject();

    private static void Rename(JsonObject value, string from, string to)
    {
        value[to] = value[from]!.DeepClone();
        value.Remove(from);
    }

    private static IEnumerable<string> EnumerateScalarPaths(JsonNode node, string prefix = "")
    {
        if (node is JsonObject value)
        {
            foreach ((string property, JsonNode? child) in value)
            {
                if (child is null) continue;
                string path = string.IsNullOrEmpty(prefix) ? property : $"{prefix}/{property}";
                foreach (string nested in EnumerateScalarPaths(child, path)) yield return nested;
            }
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                if (array[index] is not JsonNode child) continue;
                foreach (string nested in EnumerateScalarPaths(child, $"{prefix}/{index}")) yield return nested;
            }
        }
        else if (node is JsonValue)
        {
            yield return prefix;
        }
    }

    private static void MutateScalarAtPath(JsonObject root, string path)
    {
        string[] segments = path.Split('/');
        JsonNode current = root;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            current = current is JsonObject value
                ? value[segments[index]]!
                : current.AsArray()[int.Parse(segments[index], NumberStyles.None, CultureInfo.InvariantCulture)]!;
        }
        string final = segments[^1];
        JsonValue scalar = current is JsonObject parentObject
            ? parentObject[final]!.AsValue()
            : current.AsArray()[int.Parse(final, NumberStyles.None, CultureInfo.InvariantCulture)]!.AsValue();
        JsonNode replacement;
        if (scalar.TryGetValue<bool>(out bool boolean)) replacement = JsonValue.Create(!boolean);
        else if (scalar.TryGetValue<int>(out int integer)) replacement = JsonValue.Create(integer + 1);
        else replacement = JsonValue.Create(scalar.GetValue<string>() + "#");
        if (current is JsonObject targetObject) targetObject[final] = replacement;
        else current.AsArray()[int.Parse(final, NumberStyles.None, CultureInfo.InvariantCulture)] = replacement;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, PushOpcodeProfile.InstructionPath))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Nethermind repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "push-opcode-fixtures", Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            string production = FindRepoRoot();
            foreach (string relativePath in PushOpcodeProfile.SourcePaths)
            {
                string target = Path.Combine(Root, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(production, relativePath), target);
            }
        }

        public string Root { get; }
        public string Output { get; }

        public void Replace(string relativePath, string original, string replacement)
        {
            string path = Path.Combine(Root, relativePath);
            string source = File.ReadAllText(path);
            Assert.That(source, Does.Contain(original));
            File.WriteAllText(path, source.Replace(original, replacement, StringComparison.Ordinal), new UTF8Encoding(false));
        }

        public void Append(string relativePath, string suffix) =>
            File.AppendAllText(Path.Combine(Root, relativePath), suffix, new UTF8Encoding(false));

        public void Extract() => PushOpcodeProfile.Extract(Root, Output, Path.Combine(Output, "PushOpcodeKernel.lean"));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "push-opcode-output", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
