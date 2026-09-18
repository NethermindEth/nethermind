// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;
using NUnitTest = NUnit.Framework.TestAttribute;
using NUnitTestFixture = NUnit.Framework.TestFixtureAttribute;

namespace Nethermind.Evm.Lean.ExtendedStackOpcodeExtractor.Test;

[NUnitTestFixture]
public class ExtendedStackOpcodeExtractorTests
{
    [NUnitTest]
    public void Extracts_exact_operational_profile_and_source_closure()
    {
        using Fixture fixture = new("profile");
        ExtractionResult result = fixture.Extract();
        IrDocument document = ExtendedStackOpcodeProfile.DeserializeIr(File.ReadAllBytes(result.IrPath));
        SourceManifest manifest = ExtendedStackOpcodeProfile.DeserializeManifest(
            File.ReadAllBytes(result.ManifestPath));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.OpcodeCount, Is.EqualTo(3));
            Assert.That(result.SpecializationCount, Is.EqualTo(12));
            Assert.That(result.AdmissionCount, Is.EqualTo(67));
            Assert.That(document.Opcodes.Select(static value => value.Instruction),
                Is.EqualTo(new[] { "DUPN", "SWAPN", "EXCHANGE" }));
            Assert.That(document.Opcodes.Select(static value => value.OpcodeByte),
                Is.EqualTo(new[] { 0xe6, 0xe7, 0xe8 }));
            Assert.That(document.Opcodes.Select(static value => value.FixedGas), Is.All.EqualTo(3));
            Assert.That(document.Opcodes.Select(static value => value.StackGrowth),
                Is.EqualTo(new[] { 1, 0, 0 }));
            Assert.That(document.ForkGate, Is.EqualTo("IReleaseSpec.IsEip8024Enabled"));
            Assert.That(document.DefaultHandler, Is.EqualTo("BadInstructionOpcode"));
            Assert.That(document.PcOrder,
                Is.EqualTo("opcode-pc-before-gas;valid-immediate-pc-before-stack"));
            Assert.That(document.GasOrder, Is.EqualTo("very-low-before-decode-and-stack"));
            Assert.That(document.FaultOrder, Does.StartWith("fork-disabled-before-gas"));
            Assert.That(document.StackLimit, Is.EqualTo(1024));
            Assert.That(document.Specializations.Select(static value => value.EnabledRoot)
                .Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(12));
            Assert.That(document.Specializations.Select(static value => value.DisabledRoot)
                .Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(4));
            Assert.That(manifest.Sources.Select(static value => value.Path),
                Is.Ordered.Using<string>(StringComparer.Ordinal));
            Assert.That(manifest.RawSources, Has.Count.EqualTo(1));
            Assert.That(manifest.Sources, Has.Count.EqualTo(40));
            Assert.That(manifest.Sources.Select(static value => value.Path),
                Does.Contain(ExtendedStackOpcodeProfile.ControlFlowInstructionsPath));
            Assert.That(manifest.RawSources[0].Path, Is.EqualTo(ExtendedStackOpcodeProfile.BuildTargetsPath));
            Assert.That(manifest.Admissions, Has.Count.EqualTo(result.AdmissionCount));
            Assert.That(manifest.Admissions.Select(static value => value.Key)
                .Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(result.AdmissionCount));
        }
    }

    [NUnitTest]
    public void Regeneration_is_byte_identical_for_every_artifact()
    {
        using Fixture first = new("determinism-a");
        using Fixture second = new("determinism-b");
        ExtractionResult left = first.Extract();
        ExtractionResult right = second.Extract();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(left.IrPath), Is.EqualTo(File.ReadAllBytes(right.IrPath)), "IR");
            Assert.That(File.ReadAllBytes(left.ManifestPath),
                Is.EqualTo(File.ReadAllBytes(right.ManifestPath)), "manifest");
            Assert.That(File.ReadAllBytes(left.LeanPath), Is.EqualTo(File.ReadAllBytes(right.LeanPath)), "Lean");
        }
    }

    [NUnitTest]
    public void Checked_in_artifacts_are_byte_identical_to_regeneration()
    {
        using Fixture fixture = new("checked-in");
        ExtractionResult result = fixture.Extract();
        string root = RepoRoot();
        string generated = Path.Combine(root, "tools", "Evm", "Lean", "ExtendedStackOpcodeExtractor", "Generated");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(Path.Combine(generated, ExtendedStackOpcodeProfile.IrFileName)),
                Is.EqualTo(File.ReadAllBytes(result.IrPath)), "checked-in IR");
            Assert.That(File.ReadAllBytes(Path.Combine(generated, ExtendedStackOpcodeProfile.ManifestFileName)),
                Is.EqualTo(File.ReadAllBytes(result.ManifestPath)), "checked-in manifest");
            Assert.That(File.ReadAllBytes(Path.Combine(generated, "ExtendedStackOpcodeKernel.lean")),
                Is.EqualTo(File.ReadAllBytes(result.LeanPath)), "checked-in Lean");
        }
    }

    [NUnitTest]
    public void Every_admitted_source_and_build_input_is_fail_closed()
    {
        using Fixture fixture = new("source-mutations");
        foreach (string path in ExtendedStackOpcodeProfile.InputPaths)
        {
            string original = fixture.Read(path);
            fixture.Write(path, original + "\n// mutation");
            Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>(), path);
            fixture.Write(path, original);
        }
    }

    [NUnitTest]
    public void Generated_lean_is_theorem_free_and_independent_of_handwritten_transition()
    {
        using Fixture fixture = new("generated-semantics");
        string generated = File.ReadAllText(fixture.Extract().LeanPath);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Contain("theorem "));
            Assert.That(generated, Does.Not.Contain("axiom "));
            Assert.That(generated, Does.Not.Contain("import Eip803x.Evm.ExtendedStack"));
            Assert.That(generated, Does.Not.Contain("ExtendedStack.duplicate"));
            Assert.That(generated, Does.Not.Contain("ExtendedStack.swapTop"));
            Assert.That(generated, Does.Not.Contain("ExtendedStack.exchange"));
            Assert.That(generated, Does.Contain("def immediateOrZero"));
            Assert.That(generated, Does.Contain("def duplicateWords"));
            Assert.That(generated, Does.Contain("def swapWords"));
            Assert.That(generated, Does.Contain("def exchangeWords"));
            Assert.That(generated, Does.Contain("def executePlanned"));
            Assert.That(generated, Does.Contain("if !eip8024Enabled"));
        }
    }

    [NUnitTest]
    public void Direct_emitter_rejects_uppercase_newline_and_malformed_hashes()
    {
        using Fixture fixture = new("emitter-hashes");
        IrDocument document = ExtendedStackOpcodeProfile.DeserializeIr(
            File.ReadAllBytes(fixture.Extract().IrPath));
        string valid = new('a', 64);
        string uppercase = valid.ToUpperInvariant();
        string newline = new string('a', 63) + "\n";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => ExtendedStackOpcodeLeanEmitter.Emit(document, "bad", valid),
                Throws.TypeOf<ExtractionException>());
            Assert.That(() => ExtendedStackOpcodeLeanEmitter.Emit(document, valid, "bad"),
                Throws.TypeOf<ExtractionException>());
            Assert.That(() => ExtendedStackOpcodeLeanEmitter.Emit(document, uppercase, valid),
                Throws.TypeOf<ExtractionException>());
            Assert.That(() => ExtendedStackOpcodeLeanEmitter.Emit(document, valid, uppercase),
                Throws.TypeOf<ExtractionException>());
            Assert.That(() => ExtendedStackOpcodeLeanEmitter.Emit(document, newline, valid),
                Throws.TypeOf<ExtractionException>());
            Assert.That(() => ExtendedStackOpcodeLeanEmitter.Emit(document, valid, newline),
                Throws.TypeOf<ExtractionException>());
        }
    }

    [NUnitTest]
    public void Ir_rejects_malformed_null_and_unknown_members_at_every_object_shape()
    {
        using Fixture fixture = new("ir-shape");
        byte[] bytes = File.ReadAllBytes(fixture.Extract().IrPath);
        JsonObject document = JsonNode.Parse(bytes)!.AsObject();

        Assert.That(() => ExtendedStackOpcodeProfile.DeserializeIr(null!),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => ExtendedStackOpcodeProfile.DeserializeIr("{"u8.ToArray()),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => ExtendedStackOpcodeProfile.DeserializeIr("null"u8.ToArray()),
            Throws.TypeOf<ExtractionException>());

        JsonObject topUnknown = Clone(document);
        topUnknown["unexpected"] = true;
        AssertIrRejected(topUnknown, "top unknown");
        AssertDuplicatePropertyRejected(bytes, null, "schemaVersion", "1", "top duplicate");
        AssertDuplicatePropertyRejected(bytes, "dispatchTables", "name", "\"NoTrace\"",
            "dispatch table duplicate");
        AssertDuplicatePropertyRejected(bytes, "opcodes", "name", "\"dupN\"", "opcode duplicate");
        AssertDuplicatePropertyRejected(bytes, "specializations", "opcode", "\"dupN\"",
            "specialization duplicate");
        AddUnknownAndReject(document, "dispatchTables", "dispatch table unknown");
        AddUnknownAndReject(document, "opcodes", "opcode unknown");
        AddUnknownAndReject(document, "specializations", "specialization unknown");

        foreach (string field in IrTopFields)
        {
            JsonObject mutation = Clone(document);
            mutation[field] = null;
            AssertIrRejected(mutation, $"top null {field}");
        }
        foreach (string collection in IrCollectionFields)
        {
            JsonObject mutation = Clone(document);
            mutation[collection]!.AsArray()[0] = null;
            AssertIrRejected(mutation, $"entry null {collection}");
        }
        NullEveryNestedField(document, "dispatchTables", DispatchFields);
        NullEveryNestedField(document, "opcodes", OpcodeFields);
        NullEveryNestedField(document, "specializations", SpecializationFields);
    }

    [NUnitTest]
    public void Ir_rejects_every_case_alias_omission_and_field_mutation()
    {
        using Fixture fixture = new("ir-fields");
        JsonObject document = JsonNode.Parse(File.ReadAllBytes(fixture.Extract().IrPath))!.AsObject();

        AssertEveryTopFieldRejected(document, IrTopFields, AssertIrRejected);
        AssertEveryNestedFieldRejected(document, "dispatchTables", DispatchFields, AssertIrRejected);
        AssertEveryNestedFieldRejected(document, "opcodes", OpcodeFields, AssertIrRejected);
        AssertEveryNestedFieldRejected(document, "specializations", SpecializationFields, AssertIrRejected);
    }

    [NUnitTest]
    public void Ir_rejects_duplicate_and_reordered_claim_collections()
    {
        using Fixture fixture = new("ir-duplicates");
        JsonObject document = JsonNode.Parse(File.ReadAllBytes(fixture.Extract().IrPath))!.AsObject();

        foreach (string collection in IrCollectionFields)
        {
            JsonObject empty = Clone(document);
            empty[collection] = new JsonArray();
            AssertIrRejected(empty, $"empty {collection}");

            JsonObject duplicate = Clone(document);
            JsonArray values = duplicate[collection]!.AsArray();
            values.Add(values[0]!.DeepClone());
            AssertIrRejected(duplicate, $"duplicate {collection}");

            if (document[collection]!.AsArray().Count > 1)
            {
                JsonObject reordered = Clone(document);
                JsonArray reorderedValues = reordered[collection]!.AsArray();
                JsonNode? first = reorderedValues[0]!.DeepClone();
                reorderedValues[0] = reorderedValues[1]!.DeepClone();
                reorderedValues[1] = first;
                AssertIrRejected(reordered, $"reordered {collection}");
            }
        }
    }

    [NUnitTest]
    public void Manifest_rejects_malformed_null_unknown_case_omission_and_field_mutation()
    {
        using Fixture fixture = new("manifest-shape");
        byte[] bytes = File.ReadAllBytes(fixture.Extract().ManifestPath);
        JsonObject document = JsonNode.Parse(bytes)!.AsObject();

        Assert.That(() => ExtendedStackOpcodeProfile.DeserializeManifest(null!),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => ExtendedStackOpcodeProfile.DeserializeManifest("{"u8.ToArray()),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => ExtendedStackOpcodeProfile.DeserializeManifest("null"u8.ToArray()),
            Throws.TypeOf<ExtractionException>());

        JsonObject topUnknown = Clone(document);
        topUnknown["unexpected"] = true;
        AssertManifestRejected(topUnknown, "top unknown");
        AssertDuplicatePropertyRejected(bytes, null,
            "schemaVersion", "1", "manifest top duplicate", manifest: true);
        AssertDuplicatePropertyRejected(bytes, "sources",
            "path", "\"duplicate\"", "source duplicate property", manifest: true);
        AssertDuplicatePropertyRejected(bytes, "rawSources",
            "path", "\"duplicate\"", "raw source duplicate property", manifest: true);
        AssertDuplicatePropertyRejected(bytes, "admissions",
            "key", "\"duplicate\"", "admission duplicate property", manifest: true);
        AddUnknownAndReject(document, "sources", "source unknown", manifest: true);
        AddUnknownAndReject(document, "rawSources", "raw unknown", manifest: true);
        AddUnknownAndReject(document, "admissions", "admission unknown", manifest: true);

        foreach (string field in ManifestTopFields)
        {
            JsonObject mutation = Clone(document);
            mutation[field] = null;
            AssertManifestRejected(mutation, $"top null {field}");
        }
        foreach (string collection in ManifestCollectionFields)
        {
            JsonObject mutation = Clone(document);
            mutation[collection]!.AsArray()[0] = null;
            AssertManifestRejected(mutation, $"entry null {collection}");
        }
        NullEveryNestedField(document, "sources", SourceFields, manifest: true);
        NullEveryNestedField(document, "rawSources", RawSourceFields, manifest: true);
        NullEveryNestedField(document, "admissions", AdmissionFields, manifest: true);

        AssertEveryTopFieldRejected(document, ManifestTopFields, AssertManifestRejected);
        AssertEveryNestedFieldRejected(document, "sources", SourceFields, AssertManifestRejected);
        AssertEveryNestedFieldRejected(document, "rawSources", RawSourceFields, AssertManifestRejected);
        AssertEveryNestedFieldRejected(document, "admissions", AdmissionFields, AssertManifestRejected);
    }

    [NUnitTest]
    public void Manifest_rejects_every_non_lowercase_or_newline_hash_and_duplicate_identity()
    {
        using Fixture fixture = new("manifest-hashes");
        JsonObject document = JsonNode.Parse(File.ReadAllBytes(fixture.Extract().ManifestPath))!.AsObject();
        string uppercase = new('A', 64);
        string newline = new string('a', 63) + "\n";
        string wrong = new('0', 64);

        foreach (string field in new[] { "irSha256", "leanSha256", "combinedSourceSha256" })
        {
            SetAndRejectManifest(document, field, uppercase, $"uppercase {field}");
            SetAndRejectManifest(document, field, newline, $"newline {field}");
            SetAndRejectManifest(document, field, wrong, $"wrong {field}");
        }
        foreach (string field in new[] { "sha256", "roslynSyntaxSha256" })
        {
            SetNestedAndRejectManifest(document, "sources", field, uppercase, $"uppercase source {field}");
            SetNestedAndRejectManifest(document, "sources", field, newline, $"newline source {field}");
            SetNestedAndRejectManifest(document, "sources", field, wrong, $"wrong source {field}");
        }
        SetNestedAndRejectManifest(document, "rawSources", "sha256", uppercase, "uppercase raw hash");
        SetNestedAndRejectManifest(document, "rawSources", "sha256", newline, "newline raw hash");
        SetNestedAndRejectManifest(document, "rawSources", "sha256", wrong, "wrong raw hash");
        foreach (string field in new[] { "templateSha256", "sourceSyntaxSha256" })
        {
            SetNestedAndRejectManifest(document, "admissions", field, uppercase,
                $"uppercase admission {field}");
            SetNestedAndRejectManifest(document, "admissions", field, newline,
                $"newline admission {field}");
            SetNestedAndRejectManifest(document, "admissions", field, wrong,
                $"wrong admission {field}");
        }

        foreach (string collection in ManifestCollectionFields)
        {
            JsonObject mutation = Clone(document);
            JsonArray values = mutation[collection]!.AsArray();
            values.Add(values[0]!.DeepClone());
            AssertManifestRejected(mutation, $"duplicate {collection}");
        }
    }

    private static readonly string[] IrTopFields =
    [
        "schemaVersion", "extractorVersion", "kernel", "root", "gasPolicy", "fork", "buildFlavor",
        "forkGate", "defaultHandler", "pcOrder", "gasOrder", "faultOrder", "stackLimit", "veryLowGas",
        "dispatchTables", "forkLineage", "openExtractionObligations", "opcodes", "specializations",
    ];

    private static readonly string[] IrCollectionFields =
        ["dispatchTables", "forkLineage", "openExtractionObligations", "opcodes", "specializations"];

    private static readonly string[] DispatchFields = ["name", "tracingFlag", "cancelableFlag"];

    private static readonly string[] OpcodeFields =
    [
        "name", "instruction", "opcodeByte", "handlerBody", "forwarder", "instructionMethod",
        "decoderMethod", "stackMethod", "dispatchAssignment", "gasClass", "fixedGas", "stackGrowth",
        "pcOrder", "gasOrder", "faultOrder", "activation",
    ];

    private static readonly string[] SpecializationFields =
    [
        "opcode", "dispatchTable", "tracingFlag", "cancelableFlag", "continuableFlag", "enabledRoot",
        "disabledRoot",
    ];

    private static readonly string[] ManifestTopFields =
    [
        "schemaVersion", "extractorVersion", "irSha256", "leanSha256", "combinedSourceSha256", "sources",
        "rawSources", "admissions",
    ];

    private static readonly string[] ManifestCollectionFields = ["sources", "rawSources", "admissions"];
    private static readonly string[] SourceFields = ["path", "sha256", "roslynSyntaxSha256"];
    private static readonly string[] RawSourceFields = ["path", "sha256"];
    private static readonly string[] AdmissionFields = ["key", "templateSha256", "sourceSyntaxSha256"];

    private static void AddUnknownAndReject(
        JsonObject document,
        string collection,
        string description,
        bool manifest = false)
    {
        JsonObject mutation = Clone(document);
        mutation[collection]!.AsArray()[0]!.AsObject()["unexpected"] = true;
        if (manifest) AssertManifestRejected(mutation, description); else AssertIrRejected(mutation, description);
    }

    private static void NullEveryNestedField(
        JsonObject document,
        string collection,
        IEnumerable<string> fields,
        bool manifest = false)
    {
        foreach (string field in fields)
        {
            JsonObject mutation = Clone(document);
            mutation[collection]!.AsArray()[0]!.AsObject()[field] = null;
            if (manifest) AssertManifestRejected(mutation, $"null {collection}.{field}");
            else AssertIrRejected(mutation, $"null {collection}.{field}");
        }
    }

    private static void AssertEveryTopFieldRejected(
        JsonObject document,
        IEnumerable<string> fields,
        Action<JsonObject, string> reject)
    {
        foreach (string field in fields)
        {
            JsonObject caseAlias = Clone(document);
            Rename(caseAlias, field, UppercaseFirst(field));
            reject(caseAlias, $"case alias {field}");

            JsonObject omitted = Clone(document);
            omitted.Remove(field);
            reject(omitted, $"omitted {field}");

            JsonObject changed = Clone(document);
            changed[field] = ChangedValue(changed[field]!);
            reject(changed, $"changed {field}");
        }
    }

    private static void AssertEveryNestedFieldRejected(
        JsonObject document,
        string collection,
        IEnumerable<string> fields,
        Action<JsonObject, string> reject)
    {
        foreach (string field in fields)
        {
            JsonObject caseAlias = Clone(document);
            Rename(caseAlias[collection]!.AsArray()[0]!.AsObject(), field, UppercaseFirst(field));
            reject(caseAlias, $"case alias {collection}.{field}");

            JsonObject omitted = Clone(document);
            omitted[collection]!.AsArray()[0]!.AsObject().Remove(field);
            reject(omitted, $"omitted {collection}.{field}");

            JsonObject changed = Clone(document);
            JsonObject nested = changed[collection]!.AsArray()[0]!.AsObject();
            nested[field] = ChangedValue(nested[field]!);
            reject(changed, $"changed {collection}.{field}");
        }
    }

    private static JsonNode ChangedValue(JsonNode value)
    {
        if (value is JsonArray) return new JsonArray();
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out _)) return JsonValue.Create(999)!;
        return JsonValue.Create("changed")!;
    }

    private static void SetAndRejectManifest(
        JsonObject document,
        string field,
        string value,
        string description)
    {
        JsonObject mutation = Clone(document);
        mutation[field] = value;
        AssertManifestRejected(mutation, description);
    }

    private static void SetNestedAndRejectManifest(
        JsonObject document,
        string collection,
        string field,
        string value,
        string description)
    {
        JsonObject mutation = Clone(document);
        mutation[collection]!.AsArray()[0]!.AsObject()[field] = value;
        AssertManifestRejected(mutation, description);
    }

    private static void AssertDuplicatePropertyRejected(
        byte[] bytes,
        string? collection,
        string property,
        string value,
        string description,
        bool manifest = false)
    {
        string json = Encoding.UTF8.GetString(bytes);
        int searchStart = collection is null
            ? 0
            : json.IndexOf($"\"{collection}\"", StringComparison.Ordinal);
        Assert.That(searchStart, Is.GreaterThanOrEqualTo(0), $"Missing collection for {description}.");
        int objectStart = json.IndexOf('{', searchStart);
        Assert.That(objectStart, Is.GreaterThanOrEqualTo(0), $"Missing object for {description}.");
        byte[] mutation = Encoding.UTF8.GetBytes(json.Insert(
            objectStart + 1,
            $"\"{property}\":{value},"));
        if (manifest)
        {
            Assert.That(() => ExtendedStackOpcodeProfile.DeserializeManifest(mutation),
                Throws.TypeOf<ExtractionException>(), description);
        }
        else
        {
            Assert.That(() => ExtendedStackOpcodeProfile.DeserializeIr(mutation),
                Throws.TypeOf<ExtractionException>(), description);
        }
    }

    private static JsonObject Clone(JsonObject document) => JsonNode.Parse(document.ToJsonString())!.AsObject();

    private static void Rename(JsonObject document, string original, string replacement)
    {
        JsonNode value = document[original]!.DeepClone();
        document.Remove(original);
        document[replacement] = value;
    }

    private static string UppercaseFirst(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private static void AssertIrRejected(JsonObject mutation, string description) => Assert.That(
        () => ExtendedStackOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(mutation.ToJsonString())),
        Throws.TypeOf<ExtractionException>(), description);

    private static void AssertManifestRejected(JsonObject mutation, string description) => Assert.That(
        () => ExtendedStackOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(mutation.ToJsonString())),
        Throws.TypeOf<ExtractionException>(), description);

    private sealed class Fixture : IDisposable
    {
        internal Fixture(string name)
        {
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
                "extended-stack-opcode-fixtures", name, Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            string production = RepoRoot();
            foreach (string path in ExtendedStackOpcodeProfile.InputPaths)
            {
                string source = Path.Combine(production, path.Replace('/', Path.DirectorySeparatorChar));
                Write(path, File.ReadAllText(source));
            }
        }

        internal string Root { get; }
        internal string Output { get; }

        internal ExtractionResult Extract() => ExtendedStackOpcodeProfile.Extract(
            Root,
            Output,
            Path.Combine(Output, "ExtendedStackOpcodeKernel.lean"));

        internal string Read(string relativePath) => File.ReadAllText(Path.Combine(
            Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        internal void Write(string relativePath, string contents)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName,
                    ExtendedStackOpcodeProfile.OpcodeHandlersPath.Replace('/', Path.DirectorySeparatorChar))))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
