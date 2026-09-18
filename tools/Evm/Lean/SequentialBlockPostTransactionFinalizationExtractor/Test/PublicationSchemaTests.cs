// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor.Test;

public sealed partial class SequentialBlockPostTransactionFinalizationExtractorTests
{
    private const string PublicationSchemaDirectory =
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Schema";
    private const string PublicationIrSchema = "process-one-validated-publication.ir.schema.json";
    private const string PublicationManifestSchema = "process-one-validated-publication.source-manifest.schema.json";

    [Test]
    public void Publication_schemas_pin_the_same_complete_dependency_roster()
    {
        string directory = Path.Combine(FindRepoRoot(), PublicationSchemaDirectory);
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, PublicationIrSchema)));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, PublicationManifestSchema)));
        JsonElement irDependencies = ir.RootElement.GetProperty("properties").GetProperty("dependencies");
        JsonElement manifestDependencies = manifest.RootElement.GetProperty("properties").GetProperty("dependencies");
        string?[] paths = irDependencies.GetProperty("prefixItems").EnumerateArray()
            .Select(static item => item.GetProperty("const").GetString()).ToArray();
        using (Assert.EnterMultipleScope())
        {
            foreach (JsonElement dependencies in new[] { irDependencies, manifestDependencies })
            {
                Assert.That(dependencies.GetProperty("minItems").GetInt32(), Is.EqualTo(52));
                Assert.That(dependencies.GetProperty("maxItems").GetInt32(), Is.EqualTo(52));
                Assert.That(dependencies.GetProperty("prefixItems").GetArrayLength(), Is.EqualTo(52));
                Assert.That(dependencies.GetProperty("items").GetBoolean(), Is.False);
            }
            Assert.That(paths.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(52));
            Assert.That(manifestDependencies.GetProperty("prefixItems").EnumerateArray()
                .Select(static item => item.GetProperty("allOf")[1].GetProperty("properties")
                    .GetProperty("path").GetProperty("const").GetString()), Is.EqualTo(paths));
            Assert.That(paths, Does.Contain(PublicationSchemaDirectory + "/" + PublicationManifestSchema));
        }
    }

    [Test]
    public void Publication_schemas_accept_current_serialized_documents(
        [Values("live-ir", "checked-ir", "checked-manifest")] string document)
    {
        string json = document == "live-ir"
            ? JsonSerializer.Serialize(ProcessOneValidatedPublicationExtractor.AuditForTest(FindRepoRoot()),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            : ReadPublicationArtifact(document == "checked-manifest" ? "manifest" : "ir").ToJsonString();
        Assert.That(PublicationSchemaErrors(json, document == "checked-manifest"), Is.Empty);
    }

    [Test]
    public void Publication_schemas_reject_structural_and_roster_drift(
        [Values("ir", "manifest")] string kind,
        [Values("header", "source-path", "source-extra", "source-null", "sources-short",
            "dependency-path", "dependency-null", "dependencies-short", "dependencies-long", "dependencies-reordered",
            "compiler-extra", "compiler-null", "support-short", "callable-id", "callable-null", "callable-extra",
            "flow-id", "flow-extra", "flow-null", "flows-short", "dataflow-id", "premise-id", "root-extra",
            "root-null", "missing-root-field", "mutation-roster", "obligation-roster", "wrong-kind")] string mutation)
    {
        JsonObject document = ReadPublicationArtifact(kind);
        Assert.That(PublicationSchemaErrors(document.ToJsonString(), kind == "manifest"), Is.Empty,
            "The unmodified emitted artifact must conform before a negative control can be evaluated.");
        JsonArray dependencies = document["dependencies"]!.AsArray();
        switch (mutation)
        {
            case "header": document["acceptanceState"] = "static-draft"; break;
            case "source-path": document["sources"]![0]!["path"] = "unadmitted.cs"; break;
            case "source-extra": document["sources"]![0]!["unexpected"] = true; break;
            case "source-null": document["sources"]![0] = null; break;
            case "sources-short": document["sources"]!.AsArray().RemoveAt(0); break;
            case "dependency-path":
                if (kind == "manifest") dependencies[0]!["path"] = "unadmitted.lean";
                else dependencies[0] = "unadmitted.lean";
                break;
            case "dependency-null": dependencies[0] = null; break;
            case "dependencies-short": dependencies.RemoveAt(0); break;
            case "dependencies-long": dependencies.Add(dependencies[0]!.DeepClone()); break;
            case "dependencies-reordered":
                JsonNode first = dependencies[0]!.DeepClone();
                dependencies[0] = dependencies[1]!.DeepClone();
                dependencies[1] = first;
                break;
            case "compiler-extra": document["compilerClosure"]!["unexpected"] = true; break;
            case "compiler-null": document["compilerClosure"] = null; break;
            case "support-short": document["compilerClosure"]!["supportSources"]!.AsArray().RemoveAt(0); break;
            case "callable-id": document["synchronousCallables"]![0]!["id"] = "unadmitted"; break;
            case "callable-null": document["synchronousCallables"]![0] = null; break;
            case "callable-extra": document["synchronousCallables"]![0]!["unexpected"] = true; break;
            case "flow-id": document["controlFlows"]![0]!["id"] = "unadmitted"; break;
            case "flow-extra": document["controlFlows"]![0]!["unexpected"] = true; break;
            case "flow-null": document["controlFlows"]![0] = null; break;
            case "flows-short": document["controlFlows"]!.AsArray().RemoveAt(0); break;
            case "dataflow-id": document["dataFlows"]![0]!["id"] = "unadmitted"; break;
            case "premise-id": document["adapterPremises"]![0]!["id"] = "unadmitted"; break;
            case "root-extra": document["unexpected"] = true; break;
            case "root-null": document["sourceClosureSha256"] = null; break;
            case "missing-root-field": document.Remove("compilerClosure"); break;
            case "mutation-roster": document["mutationChecks"]![0] = "weakened mutation"; break;
            case "obligation-roster": document["openObligations"]![0] = "weakened obligation"; break;
            case "wrong-kind": document["sources"] = new JsonObject(); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.That(PublicationSchemaErrors(document.ToJsonString(), kind == "manifest"), Is.Not.Empty);
    }

    [Test]
    public void Publication_manifest_schema_rejects_invalid_artifact_and_dependency_identities(
        [Values("ir", "lean", "dependency")] string identity,
        [Values("path", "hash", "extra", "null", "missing")] string mutation)
    {
        JsonObject document = ReadPublicationArtifact("manifest");
        Assert.That(PublicationSchemaErrors(document.ToJsonString(), manifest: true), Is.Empty);
        JsonObject target = identity == "dependency" ? document["dependencies"]![0]!.AsObject() : document[identity]!.AsObject();
        switch (mutation)
        {
            case "path": target["path"] = "unadmitted"; break;
            case "hash": target["sha256"] = "invalid"; break;
            case "extra": target["unexpected"] = true; break;
            case "null": target["sha256"] = null; break;
            case "missing": target.Remove("sha256"); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.That(PublicationSchemaErrors(document.ToJsonString(), manifest: true), Is.Not.Empty);
    }

    [Test]
    public void Publication_schema_evaluator_rejects_unimplemented_keywords()
    {
        using JsonDocument value = JsonDocument.Parse("{}");
        using JsonDocument schema = JsonDocument.Parse("{\"unimplementedKeyword\":true}");
        Assert.That(() => ValidatePublicationSchema(value.RootElement, schema.RootElement,
                new Dictionary<string, JsonElement>(), PublicationIrSchema, "$", []),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Unsupported publication schema keyword"));
    }

    private static JsonObject ReadPublicationArtifact(string kind)
    {
        string name = kind == "manifest" ? ProcessOneValidatedPublicationExtractor.ManifestFileName : ProcessOneValidatedPublicationExtractor.IrFileName;
        return JsonNode.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(),
            ProcessOneValidatedPublicationExtractor.DefaultOutputPath, name)))!.AsObject();
    }

    private static List<string> PublicationSchemaErrors(string json, bool manifest)
    {
        string directory = Path.Combine(FindRepoRoot(), PublicationSchemaDirectory);
        using JsonDocument irSchema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, PublicationIrSchema)));
        using JsonDocument manifestSchema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, PublicationManifestSchema)));
        using JsonDocument document = JsonDocument.Parse(json);
        Dictionary<string, JsonElement> schemas = new(StringComparer.Ordinal)
        {
            [PublicationIrSchema] = irSchema.RootElement,
            [PublicationManifestSchema] = manifestSchema.RootElement,
        };
        string name = manifest ? PublicationManifestSchema : PublicationIrSchema;
        List<string> errors = [];
        ValidatePublicationSchema(document.RootElement, schemas[name], schemas, name, "$", errors);
        return errors;
    }

    // This evaluator deliberately rejects schema features it does not implement, so extending
    // either schema cannot silently turn its conformance tests into a partial validation.
    private static void ValidatePublicationSchema(JsonElement value, JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> schemas, string schemaName, string path, List<string> errors)
    {
        if (schema.ValueKind is JsonValueKind.True) return;
        if (schema.ValueKind is JsonValueKind.False) { errors.Add(path + ": forbidden item"); return; }
        foreach (JsonProperty keyword in schema.EnumerateObject())
        {
            if (keyword.Name is not ("$schema" or "title" or "$defs" or "$ref" or "allOf" or "properties" or
                "required" or "additionalProperties" or "type" or "const" or "minimum" or "minLength" or
                "pattern" or "minItems" or "maxItems" or "prefixItems" or "items" or "uniqueItems"))
                throw new InvalidOperationException("Unsupported publication schema keyword: " + keyword.Name);
        }
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            string[] parts = reference.GetString()!.Split('#', 2);
            string targetName = parts[0].Length == 0 ? schemaName : parts[0];
            JsonElement target = schemas[targetName];
            foreach (string part in parts[1].Split('/', StringSplitOptions.RemoveEmptyEntries))
                target = target.GetProperty(part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            ValidatePublicationSchema(value, target, schemas, targetName, path, errors);
        }
        if (schema.TryGetProperty("allOf", out JsonElement allOf))
            foreach (JsonElement branch in allOf.EnumerateArray())
                ValidatePublicationSchema(value, branch, schemas, schemaName, path, errors);
        if (schema.TryGetProperty("const", out JsonElement constant) && !PublicationJsonEqual(value, constant))
            errors.Add(path + ": constant mismatch");
        if (schema.TryGetProperty("type", out JsonElement type))
        {
            bool matches = type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                _ => throw new InvalidOperationException("Unsupported publication schema type: " + type.GetString()),
            };
            if (!matches) { errors.Add(path + ": type mismatch"); return; }
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement required))
                foreach (JsonElement name in required.EnumerateArray())
                    if (!value.TryGetProperty(name.GetString()!, out _)) errors.Add(path + ": missing " + name.GetString());
            bool hasProperties = schema.TryGetProperty("properties", out JsonElement properties);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (hasProperties && properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                    ValidatePublicationSchema(property.Value, propertySchema, schemas, schemaName, path + "." + property.Name, errors);
                else if (schema.TryGetProperty("additionalProperties", out JsonElement additional))
                    ValidatePublicationSchema(property.Value, additional, schemas, schemaName, path + "." + property.Name, errors);
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            int length = value.GetArrayLength();
            if (schema.TryGetProperty("minItems", out JsonElement minimum) && length < minimum.GetInt32()) errors.Add(path + ": too few items");
            if (schema.TryGetProperty("maxItems", out JsonElement maximum) && length > maximum.GetInt32()) errors.Add(path + ": too many items");
            int prefixLength = schema.TryGetProperty("prefixItems", out JsonElement prefix) ? prefix.GetArrayLength() : 0;
            for (int index = 0; index < length; index++)
            {
                if (index < prefixLength)
                    ValidatePublicationSchema(value[index], prefix[index], schemas, schemaName, path + "[" + index + "]", errors);
                else if (schema.TryGetProperty("items", out JsonElement items))
                    ValidatePublicationSchema(value[index], items, schemas, schemaName, path + "[" + index + "]", errors);
                if (schema.TryGetProperty("uniqueItems", out JsonElement unique) && unique.GetBoolean())
                    for (int earlier = 0; earlier < index; earlier++)
                        if (PublicationJsonEqual(value[index], value[earlier])) errors.Add(path + ": duplicate item");
            }
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out JsonElement minimum) && text.EnumerateRunes().Count() < minimum.GetInt32())
                errors.Add(path + ": string is too short");
            if (schema.TryGetProperty("pattern", out JsonElement pattern) &&
                !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                errors.Add(path + ": pattern mismatch");
        }
        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("minimum", out JsonElement numericMinimum) &&
            value.GetDecimal() < numericMinimum.GetDecimal()) errors.Add(path + ": below minimum");
    }

    private static bool PublicationJsonEqual(JsonElement left, JsonElement right) =>
        JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()));
}
