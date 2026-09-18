// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.StackRearrangementOpcodeExtractor.Test;

[TestFixture]
public sealed class StackRearrangementOpcodeExtractorTests
{
    [Test]
    public void Production_sources_extract_deterministically_with_exact_cardinality()
    {
        using Fixture first = new("first");
        using Fixture second = new("second");
        ExtractionResult firstResult = first.Extract();
        ExtractionResult secondResult = second.Extract();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.OpcodeCount, Is.EqualTo(33));
            Assert.That(firstResult.SpecializationCount, Is.EqualTo(132));
            Assert.That(firstResult.AdmissionCount, Is.EqualTo(49));
            Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.IrPath)));
            Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.ManifestPath)));
            Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.LeanPath)));
        }
    }

    [Test]
    public void Serialized_ir_has_four_exact_roots_for_every_opcode()
    {
        using Fixture fixture = new("roots");
        ExtractionResult result = fixture.Extract();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(result.IrPath));
        JsonElement opcodes = document.RootElement.GetProperty("opcodes");
        JsonElement specializations = document.RootElement.GetProperty("specializations");
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        HashSet<string> tables = new(StringComparer.Ordinal);
        HashSet<string> roots = new(StringComparer.Ordinal);
        foreach (JsonElement specialization in specializations.EnumerateArray())
        {
            string opcode = specialization.GetProperty("opcode").GetString()!;
            counts[opcode] = counts.GetValueOrDefault(opcode) + 1;
            tables.Add(specialization.GetProperty("dispatchTable").GetString()!);
            roots.Add(specialization.GetProperty("closedRoot").GetString()!);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opcodes.GetArrayLength(), Is.EqualTo(33));
            Assert.That(specializations.GetArrayLength(), Is.EqualTo(132));
            Assert.That(counts, Has.Count.EqualTo(33));
            Assert.That(counts.Values, Is.All.EqualTo(4));
            Assert.That(tables, Is.EquivalentTo(new[] { "NoTrace", "NoTraceCancelable", "Traced", "TracedCancelable" }));
            Assert.That(roots, Has.Count.EqualTo(132));
            Assert.That(document.RootElement.GetProperty("stackLimit").GetInt32(), Is.EqualTo(1024));
            Assert.That(document.RootElement.GetProperty("popGas").GetInt32(), Is.EqualTo(2));
            Assert.That(document.RootElement.GetProperty("rearrangementGas").GetInt32(), Is.EqualTo(3));
        }
    }

    [Test]
    public void Admission_identities_are_owner_path_resource_qualified_and_duplicate_rejected()
    {
        using Fixture fixture = new("admissions");
        ExtractionResult result = fixture.Extract();
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        string[] keys = manifest.RootElement.GetProperty("admissions").EnumerateArray()
            .Select(static admission => admission.GetProperty("key").GetString()!).ToArray();
        ExtractionException duplicate = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.ValidateAdmissionIdentities(
            [
                new("same", new string('a', 64), new string('b', 64)),
                new("same", new string('c', 64), new string('d', 64)),
            ]))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys, Has.Length.EqualTo(49));
            Assert.That(keys.Distinct(StringComparer.Ordinal).ToArray(), Has.Length.EqualTo(keys.Length));
            Assert.That(keys, Has.Some.Contains(":Nethermind.Evm:ProductionClosure:VirtualMachineOpcodeHandlers:VirtualMachine/1:"));
            Assert.That(keys, Has.Some.Contains(":Nethermind.Evm.GasPolicy:ProductionClosure:GasTags:compilation-unit/0:type:BaseGasCost/0/0/"));
            Assert.That(keys, Has.Some.Contains(":Nethermind.Evm.GasPolicy:ProductionClosure:GasTags:compilation-unit/0:type:VeryLowGasCost/0/0/"));
            Assert.That(keys, Has.Some.Contains("ProductionClosure"));
            Assert.That(duplicate.Message, Does.Contain("Duplicate owner/path/resource-qualified"));
        }
    }

    [Test]
    public void Serialized_ir_rejects_unknown_malformed_null_and_nested_null_values()
    {
        using Fixture fixture = new("strict-ir");
        ExtractionResult result = fixture.Extract();
        byte[] checkedBytes = File.ReadAllBytes(result.IrPath);
        string json = Encoding.UTF8.GetString(checkedBytes);
        string unknown = json.Insert(json.IndexOf('{') + 1, "\n  \"unexpected\": true,");

        ExtractionException unknownException = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(unknown)))!;
        JsonObject nestedUnknown = JsonNode.Parse(checkedBytes)!.AsObject();
        nestedUnknown["opcodes"]!.AsArray()[0]!.AsObject()["unexpected"] = true;
        ExtractionException nestedUnknownException = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(nestedUnknown.ToJsonString())))!;
        JsonObject tableUnknown = JsonNode.Parse(checkedBytes)!.AsObject();
        tableUnknown["dispatchTables"]!.AsArray()[0]!.AsObject()["unexpected"] = true;
        ExtractionException tableUnknownException = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(tableUnknown.ToJsonString())))!;
        JsonObject specializationUnknown = JsonNode.Parse(checkedBytes)!.AsObject();
        specializationUnknown["specializations"]!.AsArray()[0]!.AsObject()["unexpected"] = true;
        ExtractionException specializationUnknownException = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(specializationUnknown.ToJsonString())))!;
        ExtractionException malformed = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeIr("{"u8.ToArray()))!;
        ExtractionException empty = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeIr(null!))!;
        ExtractionException nullDocument = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeIr("null"u8.ToArray()))!;

        JsonObject nestedNull = JsonNode.Parse(checkedBytes)!.AsObject();
        nestedNull["opcodes"]!.AsArray()[0]!["handlerBody"] = null;
        ExtractionException nestedException = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(nestedNull.ToJsonString())))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unknownException.Message, Does.Contain("unknown members"));
            Assert.That(nestedUnknownException.Message, Does.Contain("unknown members"));
            Assert.That(tableUnknownException.Message, Does.Contain("unknown members"));
            Assert.That(specializationUnknownException.Message, Does.Contain("unknown members"));
            Assert.That(malformed.Message, Does.Contain("malformed"));
            Assert.That(empty.Message, Does.Contain("empty"));
            Assert.That(nullDocument.Message, Does.Contain("empty"));
            Assert.That(nestedException.Message, Does.Contain("null"));
        }
    }

    [Test]
    public void Serialized_ir_rejects_every_top_and_nested_case_alias()
    {
        using Fixture fixture = new("strict-ir-case");
        JsonObject document = JsonNode.Parse(File.ReadAllBytes(fixture.Extract().IrPath))!.AsObject();

        foreach (string field in TopIrFields)
        {
            JsonObject mutation = Clone(document);
            Rename(mutation, field, UppercaseFirst(field));
            AssertIrRejected(mutation, $"top case alias {field}");
        }
        foreach (string field in DispatchTableFields)
        {
            JsonObject mutation = Clone(document);
            Rename(mutation["dispatchTables"]!.AsArray()[0]!.AsObject(), field, UppercaseFirst(field));
            AssertIrRejected(mutation, $"dispatch table case alias {field}");
        }
        foreach (string field in DescriptorFields)
        {
            JsonObject mutation = Clone(document);
            Rename(mutation["opcodes"]!.AsArray()[0]!.AsObject(), field, UppercaseFirst(field));
            AssertIrRejected(mutation, $"descriptor case alias {field}");
        }
        foreach (string field in SpecializationFields)
        {
            JsonObject mutation = Clone(document);
            Rename(mutation["specializations"]!.AsArray()[0]!.AsObject(), field, UppercaseFirst(field));
            AssertIrRejected(mutation, $"specialization case alias {field}");
        }
    }

    [Test]
    public void Serialized_ir_rejects_every_omitted_required_top_and_nested_field()
    {
        using Fixture fixture = new("strict-ir-omitted");
        JsonObject document = JsonNode.Parse(File.ReadAllBytes(fixture.Extract().IrPath))!.AsObject();

        foreach (string field in TopIrFields)
        {
            JsonObject mutation = Clone(document);
            mutation.Remove(field);
            AssertIrRejected(mutation, $"omitted top field {field}");
        }
        foreach (string field in DispatchTableFields)
        {
            JsonObject mutation = Clone(document);
            mutation["dispatchTables"]!.AsArray()[0]!.AsObject().Remove(field);
            AssertIrRejected(mutation, $"omitted dispatch table field {field}");
        }
        foreach (string field in DescriptorFields)
        {
            JsonObject mutation = Clone(document);
            mutation["opcodes"]!.AsArray()[0]!.AsObject().Remove(field);
            AssertIrRejected(mutation, $"omitted descriptor field {field}");
        }
        foreach (string field in SpecializationFields)
        {
            JsonObject mutation = Clone(document);
            mutation["specializations"]!.AsArray()[0]!.AsObject().Remove(field);
            AssertIrRejected(mutation, $"omitted specialization field {field}");
        }
    }

    [Test]
    public void Serialized_ir_rejects_null_for_every_collection_entry_and_nested_field()
    {
        using Fixture fixture = new("strict-ir-nulls");
        JsonObject document = JsonNode.Parse(File.ReadAllBytes(fixture.Extract().IrPath))!.AsObject();

        foreach (string field in TopIrFields)
        {
            JsonObject mutation = Clone(document);
            mutation[field] = null;
            AssertIrRejected(mutation, $"null top field {field}");
        }
        foreach (string collection in IrCollectionFields)
        {
            JsonObject mutation = Clone(document);
            mutation[collection]!.AsArray()[0] = null;
            AssertIrRejected(mutation, $"null entry in {collection}");
        }
        foreach (string field in DispatchTableFields)
        {
            JsonObject mutation = Clone(document);
            mutation["dispatchTables"]!.AsArray()[0]!.AsObject()[field] = null;
            AssertIrRejected(mutation, $"null dispatch table field {field}");
        }
        foreach (string field in DescriptorFields)
        {
            JsonObject mutation = Clone(document);
            mutation["opcodes"]!.AsArray()[0]!.AsObject()[field] = null;
            AssertIrRejected(mutation, $"null descriptor field {field}");
        }
        foreach (string field in SpecializationFields)
        {
            JsonObject mutation = Clone(document);
            mutation["specializations"]!.AsArray()[0]!.AsObject()[field] = null;
            AssertIrRejected(mutation, $"null specialization field {field}");
        }
    }

    [Test]
    public void Every_claim_relevant_ir_field_is_revalidated_after_round_trip()
    {
        using Fixture fixture = new("field-mutations");
        ExtractionResult result = fixture.Extract();
        JsonObject document = JsonNode.Parse(File.ReadAllBytes(result.IrPath))!.AsObject();

        string[] topStringFields =
        [
            "extractorVersion", "kernel", "root", "gasPolicy", "fork", "buildFlavor", "diClosure", "forkClosure",
            "pcOrder", "gasOrder", "faultOrder",
        ];
        string[] topNumericFields = ["schemaVersion", "stackLimit", "popGas", "rearrangementGas"];
        foreach (string field in topStringFields)
        {
            JsonObject mutation = Clone(document);
            mutation[field] = "arbitrary";
            AssertIrRejected(mutation, $"top field {field}");
        }
        foreach (string field in topNumericFields)
        {
            JsonObject mutation = Clone(document);
            mutation[field] = 999;
            AssertIrRejected(mutation, $"top numeric field {field}");
        }

        foreach (string field in new[] { "name", "tracingFlag", "cancelableFlag" })
        {
            JsonObject mutation = Clone(document);
            mutation["dispatchTables"]!.AsArray()[0]!.AsObject()[field] = "arbitrary";
            AssertIrRejected(mutation, $"dispatch table field {field}");
        }
        foreach (string field in new[]
        {
            "name", "instruction", "handlerBody", "handlerRoot", "dispatchAssignment", "gasClass", "stackEffect",
            "pcOrder", "gasOrder", "faultOrder", "activation", "fork", "gasPolicy", "vmRoot",
        })
        {
            JsonObject mutation = Clone(document);
            mutation["opcodes"]!.AsArray()[0]!.AsObject()[field] = "arbitrary";
            AssertIrRejected(mutation, $"descriptor string field {field}");
        }
        foreach (string field in new[] { "opcodeByte", "fixedGas", "stackInputs", "stackGrowth", "operandDepth" })
        {
            JsonObject mutation = Clone(document);
            mutation["opcodes"]!.AsArray()[0]!.AsObject()[field] = 999;
            AssertIrRejected(mutation, $"descriptor numeric field {field}");
        }
        foreach (string field in new[]
        {
            "opcode", "dispatchTable", "tracingFlag", "cancelableFlag", "continuableFlag", "closedRoot", "handlerRoot",
        })
        {
            JsonObject mutation = Clone(document);
            mutation["specializations"]!.AsArray()[0]!.AsObject()[field] = "arbitrary";
            AssertIrRejected(mutation, $"specialization field {field}");
        }
        foreach (string collection in new[] { "dispatchTables", "forkLineage", "openExtractionObligations", "opcodes", "specializations" })
        {
            JsonObject mutation = Clone(document);
            mutation[collection] = new JsonArray();
            AssertIrRejected(mutation, $"empty collection {collection}");
        }
        foreach (string collection in new[] { "forkLineage", "openExtractionObligations" })
        {
            JsonObject mutation = Clone(document);
            mutation[collection]!.AsArray()[0] = "arbitrary";
            AssertIrRejected(mutation, $"collection entry {collection}");
        }
    }

    [Test]
    public void Compile_valid_production_and_build_selection_mutations_are_rejected()
    {
        using Fixture fixture = new("source-mutations");
        fixture.Replace(StackRearrangementOpcodeProfile.GasCostPath,
            "public const ulong VeryLow = 3;", "public const ulong VeryLow = 4;");
        Assert.That(() => fixture.Extract(), Throws.TypeOf<ExtractionException>());

        using Fixture gasTagFixture = new("gas-tag-source-mutation");
        gasTagFixture.Replace(StackRearrangementOpcodeProfile.GasTagsPath,
            "public readonly struct BaseGasCost : IGasCost { public static ulong GasCost => GasCostOf.Base; }",
            "public readonly struct BaseGasCost : IGasCost { public static ulong GasCost => GasCostOf.VeryLow; }");
        Assert.That(() => gasTagFixture.Extract(), Throws.TypeOf<ExtractionException>());

        using Fixture instructionFixture = new("instruction-source-mutation");
        instructionFixture.Replace(StackRearrangementOpcodeProfile.InstructionPath,
            "POP = 0x50", "POP = 0x51");
        Assert.That(() => instructionFixture.Extract(), Throws.TypeOf<ExtractionException>());

        using Fixture buildFixture = new("build-mutation");
        buildFixture.Replace(StackRearrangementOpcodeProfile.BuildTargetsPath,
            "'$(EnableZkEvm)' != 'true'", "'$(EnableZkEvm)' == 'false'");
        ExtractionException exception = Assert.Throws<ExtractionException>(() => buildFixture.Extract())!;
        Assert.That(exception.Message, Does.Contain("build-target"));
    }

    [Test]
    public void Generated_lean_is_theorem_free_and_consumes_descriptor_plan()
    {
        using Fixture fixture = new("lean");
        ExtractionResult result = fixture.Extract();
        string generated = File.ReadAllText(result.LeanPath);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Contain("theorem "));
            Assert.That(generated, Does.Not.Contain("axiom "));
            Assert.That(generated, Does.Not.Contain("import Eip803x.Evm.StackRearrangement"));
            Assert.That(generated, Does.Not.Contain("Eip803x.Evm.StackRearrangement."));
            Assert.That(generated, Does.Contain("def descriptor"));
            Assert.That(generated, Does.Contain("def specializations"));
            Assert.That(generated, Does.Contain("def plan"));
            Assert.That(generated, Does.Contain("inductive Operation"));
            Assert.That(generated, Does.Contain("def applyOperation"));
            Assert.That(generated, Does.Contain("executePlanned"));
        }
    }

    [Test]
    public void Generated_lean_digest_interpolation_rejects_invalid_hashes()
    {
        using Fixture fixture = new("lean-digests");
        ExtractionResult result = fixture.Extract();
        IrDocument document = StackRearrangementOpcodeProfile.DeserializeIr(File.ReadAllBytes(result.IrPath));
        string validHash = new('a', 64);
        string uppercaseHash = validHash.ToUpperInvariant();
        string newlineHash = new string('a', 63) + "\n";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => StackRearrangementOpcodeLeanEmitter.Emit(document, "not-a-sha256", validHash),
                Throws.TypeOf<ExtractionException>());
            Assert.That(
                () => StackRearrangementOpcodeLeanEmitter.Emit(document, validHash, "not-a-sha256"),
                Throws.TypeOf<ExtractionException>());
            Assert.That(
                () => StackRearrangementOpcodeLeanEmitter.Emit(document, uppercaseHash, validHash),
                Throws.TypeOf<ExtractionException>());
            Assert.That(
                () => StackRearrangementOpcodeLeanEmitter.Emit(document, validHash, uppercaseHash),
                Throws.TypeOf<ExtractionException>());
            Assert.That(
                () => StackRearrangementOpcodeLeanEmitter.Emit(document, newlineHash, validHash),
                Throws.TypeOf<ExtractionException>());
            Assert.That(
                () => StackRearrangementOpcodeLeanEmitter.Emit(document, validHash, newlineHash),
                Throws.TypeOf<ExtractionException>());
        }
    }

    [Test]
    public void Serialized_manifest_hashes_require_lowercase_hex_without_newlines()
    {
        using Fixture fixture = new("manifest-hash-format");
        JsonObject document = JsonNode.Parse(File.ReadAllBytes(fixture.Extract().ManifestPath))!.AsObject();
        string uppercaseHash = new('A', 64);
        string newlineHash = new string('a', 63) + "\n";

        foreach (string field in new[] { "irSha256", "leanSha256", "combinedSourceSha256" })
        {
            JsonObject mutation = Clone(document);
            mutation[field] = uppercaseHash;
            AssertManifestRejected(mutation, $"uppercase manifest hash {field}");

            mutation = Clone(document);
            mutation[field] = newlineHash;
            AssertManifestRejected(mutation, $"newline manifest hash {field}");
        }
        foreach (string field in new[] { "sha256", "roslynSyntaxSha256" })
        {
            JsonObject mutation = Clone(document);
            mutation["sources"]!.AsArray()[0]!.AsObject()[field] = uppercaseHash;
            AssertManifestRejected(mutation, $"uppercase source hash {field}");

            mutation = Clone(document);
            mutation["sources"]!.AsArray()[0]!.AsObject()[field] = newlineHash;
            AssertManifestRejected(mutation, $"newline source hash {field}");
        }

        JsonObject rawMutation = Clone(document);
        rawMutation["rawSources"]!.AsArray()[0]!.AsObject()["sha256"] = uppercaseHash;
        AssertManifestRejected(rawMutation, "uppercase raw source hash");
        rawMutation = Clone(document);
        rawMutation["rawSources"]!.AsArray()[0]!.AsObject()["sha256"] = newlineHash;
        AssertManifestRejected(rawMutation, "newline raw source hash");

        foreach (string field in new[] { "templateSha256", "sourceSyntaxSha256" })
        {
            JsonObject mutation = Clone(document);
            mutation["admissions"]!.AsArray()[0]!.AsObject()[field] = uppercaseHash;
            AssertManifestRejected(mutation, $"uppercase admission hash {field}");

            mutation = Clone(document);
            mutation["admissions"]!.AsArray()[0]!.AsObject()[field] = newlineHash;
            AssertManifestRejected(mutation, $"newline admission hash {field}");
        }
    }

    [Test]
    public void Serialized_manifest_rejects_unknown_malformed_top_level_null_and_nested_null_values()
    {
        using Fixture fixture = new("strict-manifest");
        ExtractionResult result = fixture.Extract();
        byte[] checkedBytes = File.ReadAllBytes(result.ManifestPath);
        JsonObject document = JsonNode.Parse(checkedBytes)!.AsObject();

        JsonObject unknown = Clone(document);
        unknown["unexpected"] = true;
        ExtractionException unknownException = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(unknown.ToJsonString())))!;
        JsonObject nestedUnknown = Clone(document);
        nestedUnknown["sources"]!.AsArray()[0]!.AsObject()["unexpected"] = true;
        ExtractionException nestedUnknownException = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(nestedUnknown.ToJsonString())))!;
        JsonObject rawUnknown = Clone(document);
        rawUnknown["rawSources"]!.AsArray()[0]!.AsObject()["unexpected"] = true;
        ExtractionException rawUnknownException = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(rawUnknown.ToJsonString())))!;
        JsonObject admissionUnknown = Clone(document);
        admissionUnknown["admissions"]!.AsArray()[0]!.AsObject()["unexpected"] = true;
        ExtractionException admissionUnknownException = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(admissionUnknown.ToJsonString())))!;
        ExtractionException malformed = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeManifest("{"u8.ToArray()))!;
        ExtractionException empty = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeManifest(null!))!;
        ExtractionException nullDocument = Assert.Throws<ExtractionException>(() =>
            StackRearrangementOpcodeProfile.DeserializeManifest("null"u8.ToArray()))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unknownException.Message, Does.Contain("unknown members"));
            Assert.That(nestedUnknownException.Message, Does.Contain("unknown members"));
            Assert.That(rawUnknownException.Message, Does.Contain("unknown members"));
            Assert.That(admissionUnknownException.Message, Does.Contain("unknown members"));
            Assert.That(malformed.Message, Does.Contain("malformed"));
            Assert.That(empty.Message, Does.Contain("empty"));
            Assert.That(nullDocument.Message, Does.Contain("empty"));
        }

        foreach (string field in ManifestTopFields)
        {
            JsonObject mutation = Clone(document);
            mutation[field] = null;
            AssertManifestRejected(mutation, $"null manifest top field {field}");
        }
        foreach (string collection in ManifestCollectionFields)
        {
            JsonObject mutation = Clone(document);
            mutation[collection]!.AsArray()[0] = null;
            AssertManifestRejected(mutation, $"null manifest entry {collection}");
        }
        foreach (string field in SourceIdentityFields)
        {
            JsonObject mutation = Clone(document);
            mutation["sources"]!.AsArray()[0]!.AsObject()[field] = null;
            AssertManifestRejected(mutation, $"null source identity field {field}");
        }
        foreach (string field in RawIdentityFields)
        {
            JsonObject mutation = Clone(document);
            mutation["rawSources"]!.AsArray()[0]!.AsObject()[field] = null;
            AssertManifestRejected(mutation, $"null raw identity field {field}");
        }
        foreach (string field in AdmissionIdentityFields)
        {
            JsonObject mutation = Clone(document);
            mutation["admissions"]!.AsArray()[0]!.AsObject()[field] = null;
            AssertManifestRejected(mutation, $"null admission identity field {field}");
        }
    }

    [Test]
    public void Serialized_manifest_rejects_every_case_alias_and_omitted_required_field()
    {
        using Fixture fixture = new("strict-manifest-shape");
        JsonObject document = JsonNode.Parse(File.ReadAllBytes(fixture.Extract().ManifestPath))!.AsObject();

        foreach (string field in ManifestTopFields)
        {
            JsonObject mutation = Clone(document);
            Rename(mutation, field, UppercaseFirst(field));
            AssertManifestRejected(mutation, $"manifest top case alias {field}");

            mutation = Clone(document);
            mutation.Remove(field);
            AssertManifestRejected(mutation, $"omitted manifest top field {field}");
        }
        foreach (string field in SourceIdentityFields)
        {
            JsonObject mutation = Clone(document);
            Rename(mutation["sources"]!.AsArray()[0]!.AsObject(), field, UppercaseFirst(field));
            AssertManifestRejected(mutation, $"source case alias {field}");

            mutation = Clone(document);
            mutation["sources"]!.AsArray()[0]!.AsObject().Remove(field);
            AssertManifestRejected(mutation, $"omitted source field {field}");
        }
        foreach (string field in RawIdentityFields)
        {
            JsonObject mutation = Clone(document);
            Rename(mutation["rawSources"]!.AsArray()[0]!.AsObject(), field, UppercaseFirst(field));
            AssertManifestRejected(mutation, $"raw source case alias {field}");

            mutation = Clone(document);
            mutation["rawSources"]!.AsArray()[0]!.AsObject().Remove(field);
            AssertManifestRejected(mutation, $"omitted raw source field {field}");
        }
        foreach (string field in AdmissionIdentityFields)
        {
            JsonObject mutation = Clone(document);
            Rename(mutation["admissions"]!.AsArray()[0]!.AsObject(), field, UppercaseFirst(field));
            AssertManifestRejected(mutation, $"admission case alias {field}");

            mutation = Clone(document);
            mutation["admissions"]!.AsArray()[0]!.AsObject().Remove(field);
            AssertManifestRejected(mutation, $"omitted admission field {field}");
        }
    }

    [Test]
    public void Every_manifest_field_and_identity_hash_is_revalidated_after_round_trip()
    {
        using Fixture fixture = new("manifest-mutations");
        JsonObject document = JsonNode.Parse(File.ReadAllBytes(fixture.Extract().ManifestPath))!.AsObject();

        foreach (string field in new[] { "extractorVersion", "irSha256", "leanSha256", "combinedSourceSha256" })
        {
            JsonObject mutation = Clone(document);
            mutation[field] = "0".PadLeft(64, '0');
            AssertManifestRejected(mutation, $"manifest top field {field}");
        }
        JsonObject schemaMutation = Clone(document);
        schemaMutation["schemaVersion"] = 999;
        AssertManifestRejected(schemaMutation, "manifest schema version");

        foreach (string field in SourceIdentityFields)
        {
            JsonObject mutation = Clone(document);
            mutation["sources"]!.AsArray()[0]!.AsObject()[field] = field == "path" ? "arbitrary" :
                "0".PadLeft(64, '0');
            AssertManifestRejected(mutation, $"source identity field {field}");
        }
        foreach (string field in RawIdentityFields)
        {
            JsonObject mutation = Clone(document);
            mutation["rawSources"]!.AsArray()[0]!.AsObject()[field] = field == "path" ? "arbitrary" :
                "0".PadLeft(64, '0');
            AssertManifestRejected(mutation, $"raw source identity field {field}");
        }
        foreach (string field in AdmissionIdentityFields)
        {
            JsonObject mutation = Clone(document);
            mutation["admissions"]!.AsArray()[0]!.AsObject()[field] = field == "key" ? "arbitrary" :
                "0".PadLeft(64, '0');
            AssertManifestRejected(mutation, $"admission identity field {field}");
        }
        foreach (string collection in ManifestCollectionFields)
        {
            JsonObject mutation = Clone(document);
            mutation[collection] = new JsonArray();
            AssertManifestRejected(mutation, $"empty manifest collection {collection}");
        }
    }

    [Test]
    public void Duplicate_source_raw_and_admission_identities_are_rejected()
    {
        using Fixture fixture = new("manifest-duplicates");
        JsonObject document = JsonNode.Parse(File.ReadAllBytes(fixture.Extract().ManifestPath))!.AsObject();

        JsonObject duplicateSource = Clone(document);
        duplicateSource["sources"]!.AsArray()[1] = duplicateSource["sources"]!.AsArray()[0]!.DeepClone();
        AssertManifestRejected(duplicateSource, "duplicate source identity");

        JsonObject duplicateRaw = Clone(document);
        duplicateRaw["rawSources"]!.AsArray().Add(duplicateRaw["rawSources"]!.AsArray()[0]!.DeepClone());
        AssertManifestRejected(duplicateRaw, "duplicate raw source identity");

        JsonObject duplicateAdmission = Clone(document);
        duplicateAdmission["admissions"]!.AsArray()[1] = duplicateAdmission["admissions"]!.AsArray()[0]!.DeepClone();
        AssertManifestRejected(duplicateAdmission, "duplicate admission identity");
    }

    private static readonly string[] TopIrFields =
    [
        "schemaVersion", "extractorVersion", "kernel", "root", "gasPolicy", "fork", "buildFlavor",
        "diClosure", "forkClosure", "pcOrder", "gasOrder", "faultOrder", "stackLimit", "popGas",
        "rearrangementGas", "dispatchTables", "forkLineage", "openExtractionObligations", "opcodes",
        "specializations",
    ];

    private static readonly string[] IrCollectionFields =
        ["dispatchTables", "forkLineage", "openExtractionObligations", "opcodes", "specializations"];

    private static readonly string[] DispatchTableFields = ["name", "tracingFlag", "cancelableFlag"];

    private static readonly string[] DescriptorFields =
    [
        "name", "instruction", "opcodeByte", "handlerBody", "handlerRoot", "dispatchAssignment", "gasClass",
        "fixedGas", "stackInputs", "stackGrowth", "operandDepth", "stackEffect", "pcOrder", "gasOrder",
        "faultOrder", "activation", "fork", "gasPolicy", "vmRoot",
    ];

    private static readonly string[] SpecializationFields =
        ["opcode", "dispatchTable", "tracingFlag", "cancelableFlag", "continuableFlag", "closedRoot", "handlerRoot"];

    private static readonly string[] ManifestTopFields =
        ["schemaVersion", "extractorVersion", "irSha256", "leanSha256", "combinedSourceSha256", "sources", "rawSources", "admissions"];

    private static readonly string[] ManifestCollectionFields = ["sources", "rawSources", "admissions"];

    private static readonly string[] SourceIdentityFields = ["path", "sha256", "roslynSyntaxSha256"];

    private static readonly string[] RawIdentityFields = ["path", "sha256"];

    private static readonly string[] AdmissionIdentityFields = ["key", "templateSha256", "sourceSyntaxSha256"];

    private static JsonObject Clone(JsonObject document) =>
        JsonNode.Parse(document.ToJsonString())!.AsObject();

    private static void Rename(JsonObject document, string original, string replacement)
    {
        JsonNode? value = document[original]?.DeepClone();
        Assert.That(value, Is.Not.Null, $"Expected serialized property '{original}'.");
        document.Remove(original);
        document[replacement] = value;
    }

    private static string UppercaseFirst(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private static void AssertIrRejected(JsonObject mutation, string description) =>
        Assert.That(
            () => StackRearrangementOpcodeProfile.DeserializeIr(Encoding.UTF8.GetBytes(mutation.ToJsonString())),
            Throws.TypeOf<ExtractionException>(),
            description);

    private static void AssertManifestRejected(JsonObject mutation, string description) =>
        Assert.That(
            () => StackRearrangementOpcodeProfile.DeserializeManifest(Encoding.UTF8.GetBytes(mutation.ToJsonString())),
            Throws.TypeOf<ExtractionException>(),
            description);

    private sealed class Fixture : IDisposable
    {
        internal Fixture(string name)
        {
            Root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
                "stack-rearrangement-fixtures", name, Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "generated");
            string production = RepoRoot();
            foreach (string path in StackRearrangementOpcodeProfile.InputPaths)
                Write(path, File.ReadAllText(Path.Combine(production, path.Replace('/', Path.DirectorySeparatorChar))));
        }

        internal string Root { get; }
        internal string Output { get; }

        internal ExtractionResult Extract() => StackRearrangementOpcodeProfile.Extract(
            Root, Output, Path.Combine(Output, "StackRearrangementOpcodeKernel.lean"));

        internal void Replace(string relativePath, string before, string after)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string source = File.ReadAllText(path);
            Assert.That(source, Does.Contain(before), "Mutation target must exist.");
            Write(relativePath, source.Replace(before, after, StringComparison.Ordinal));
        }

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
                    StackRearrangementOpcodeProfile.OpcodeHandlersPath.Replace('/', Path.DirectorySeparatorChar))))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
