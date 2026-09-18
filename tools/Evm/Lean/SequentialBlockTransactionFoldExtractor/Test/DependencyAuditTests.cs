// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SequentialBlockTransactionFoldExtractor.Test;

[TestFixture]
[NonParallelizable]
public sealed class DependencyAuditTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private string _root = null!;

    [OneTimeSetUp]
    public void Admit_complete_unmutated_dependency()
    {
        _root = SequentialBlockTransactionFoldExtractorTests.FindRepoRoot();
        Assert.DoesNotThrow(() => ReceiptDependencyAudit.ReadValidated(_root));
    }

    [TestCase("schema", "fold.receipt.pins:")]
    [TestCase("static-draft", "fold.receipt.pins:")]
    [TestCase("status-null", "fold.receipt.pins:")]
    [TestCase("empty", "fold.receipt.pins:")]
    [TestCase("missing", "fold.receipt.pins:")]
    [TestCase("duplicate", "fold.receipt.pins:")]
    [TestCase("unexpected", "fold.receipt.pins:")]
    [TestCase("reordered", "fold.receipt.pins:")]
    [TestCase("null-array", "fold.receipt.pins:")]
    [TestCase("null-entry", "fold.receipt.pins:")]
    [TestCase("null-sha", "fold.receipt.pins:")]
    [TestCase("bad-sha", "fold.receipt.pins:")]
    [TestCase("stale-sha", "fold.receipt.pinsHash:")]
    [TestCase("unknown-property", "fold.json:")]
    [TestCase("missing-property", "fold.json:")]
    [TestCase("duplicate-property", "fold.json:")]
    [TestCase("malformed", "fold.json:")]
    [TestCase("null-document", "fold.json:")]
    public void Frozen_dependency_admission_rejects_drift(string mutation, string diagnostic)
    {
        byte[] baseline = Read(ReceiptDependencyAudit.PinsPath);
        Assert.DoesNotThrow(() => ReceiptDependencyAudit.ValidatePinsForTest(baseline));
        JsonObject value = JsonNode.Parse(baseline)!.AsObject();
        JsonArray artifacts = value["artifacts"]!.AsArray();
        switch (mutation)
        {
            case "schema": value["schemaVersion"] = 0; break;
            case "static-draft": value["status"] = "static-draft"; break;
            case "status-null": value["status"] = null; break;
            case "empty": artifacts.Clear(); break;
            case "missing": artifacts.RemoveAt(0); break;
            case "duplicate": artifacts.Add(artifacts[0]!.DeepClone()); break;
            case "unexpected": artifacts[0]!["path"] = "unexpected"; break;
            case "reordered": (artifacts[0], artifacts[1]) = (artifacts[1]!.DeepClone(), artifacts[0]!.DeepClone()); break;
            case "null-array": value["artifacts"] = null; break;
            case "null-entry": artifacts[0] = null; break;
            case "null-sha": artifacts[0]!["sha256"] = null; break;
            case "bad-sha": artifacts[0]!["sha256"] = "not-a-hash"; break;
            case "stale-sha": artifacts[0]!["sha256"] = new string('0', 64); break;
            case "unknown-property": value["unexpected"] = true; break;
            case "missing-property": value.Remove("status"); break;
        }
        byte[] changed = mutation switch
        {
            "duplicate-property" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(baseline).Replace(
                "\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"schemaVersion\": 1,", StringComparison.Ordinal)),
            "malformed" => Encoding.UTF8.GetBytes("{"),
            "null-document" => Encoding.UTF8.GetBytes("null"),
            _ => Serialize(value),
        };
        ExtractionException? exception = Assert.Throws<ExtractionException>(() =>
            ReceiptDependencyAudit.ValidatePinsForTest(changed));
        Assert.That(exception!.Message, Does.StartWith(diagnostic));
    }

    [TestCase("schema", "The receipt-terminal manifest header changed.")]
    [TestCase("extractor", "The receipt-terminal manifest header changed.")]
    [TestCase("compiler", "The receipt-terminal manifest header changed.")]
    [TestCase("empty-sources", "The receipt-terminal manifest source or member closure changed.")]
    [TestCase("missing-source", "The receipt-terminal manifest source or member closure changed.")]
    [TestCase("duplicate-source", "The receipt-terminal manifest source or member closure changed.")]
    [TestCase("source-hash", "The receipt-terminal source identity at index 0 changed.")]
    [TestCase("binding-hash", "The receipt-terminal binding source identity at index 0 changed.")]
    [TestCase("delegated-proof", "The receipt-terminal package does not identity-pin the existing accounting kernel and refinement.")]
    [TestCase("delegated-empty", "The receipt-terminal package does not identity-pin the existing accounting kernel and refinement.")]
    [TestCase("artifact-hash", "The receipt-terminal manifest is not the exact source-derived identity.")]
    [TestCase("unknown-property", "The receipt-terminal manifest is invalid:")]
    [TestCase("null-source", "The receipt-terminal manifest contains null entries.")]
    [TestCase("duplicate-property", "The serialized receipt-terminal manifest contains duplicate property 'schemaVersion'.")]
    public void Complete_upstream_validator_rejects_manifest_mutations(string mutation, string diagnostic)
    {
        byte[] manifest = Read(Extractor.ReceiptSourceManifestPath);
        byte[] ir = Read(ReceiptDependencyAudit.ReceiptRoot + "Generated/ReceiptTerminalFoldKernel.ir.json");
        byte[] lean = Read(ReceiptDependencyAudit.ReceiptRoot + "Generated/ReceiptTerminalFoldKernel.lean");
        JsonObject value = JsonNode.Parse(manifest)!.AsObject();
        JsonArray sources = value["sources"]!.AsArray();
        switch (mutation)
        {
            case "schema": value["schemaVersion"] = 7; break;
            case "extractor": value["extractorVersion"] = "1.7.0"; break;
            case "compiler": value["compilerVersion"] = "0.0.0.0"; break;
            case "empty-sources": sources.Clear(); break;
            case "missing-source": sources.RemoveAt(0); break;
            case "duplicate-source": sources.Add(sources[0]!.DeepClone()); break;
            case "source-hash": sources[0]!["sha256"] = new string('0', 64); break;
            case "binding-hash": value["bindingSources"]![0]!["sha256"] = new string('0', 64); break;
            case "delegated-proof": value["accountingKernel"]!["refinementSha256"] = new string('0', 64); break;
            case "delegated-empty": value["accountingKernel"]!["leanDependencies"] = new JsonArray(); break;
            case "artifact-hash": value["ir"]!["sha256"] = new string('0', 64); break;
            case "unknown-property": value["unexpected"] = true; break;
            case "null-source": sources[0] = null; break;
        }
        byte[] changed = mutation == "duplicate-property"
            ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace(
                "\"schemaVersion\": 9,", "\"schemaVersion\": 9, \"schemaVersion\": 9,", StringComparison.Ordinal))
            : Serialize(value);
        ExtractionException? exception = Assert.Throws<ExtractionException>(() =>
            ReceiptDependencyAudit.ValidateUpstreamArtifactsForTest(_root, changed, ir, lean));
        Assert.That(exception!.Message, Does.StartWith("fold.receipt.upstream: " + diagnostic));
    }

    [TestCase("ir", "The serialized receipt-terminal IR contains duplicate property 'schemaVersion'.")]
    [TestCase("lean", "The generated receipt-terminal Lean artifact differs from exact typed IR emission.")]
    public void Complete_upstream_validator_checks_artifact_bytes(string artifact, string diagnostic)
    {
        byte[] manifest = Read(Extractor.ReceiptSourceManifestPath);
        byte[] ir = Read(ReceiptDependencyAudit.ReceiptRoot + "Generated/ReceiptTerminalFoldKernel.ir.json");
        byte[] lean = Read(ReceiptDependencyAudit.ReceiptRoot + "Generated/ReceiptTerminalFoldKernel.lean");
        if (artifact == "ir")
            ir = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ir).Replace(
                "\"schemaVersion\": 9,", "\"schemaVersion\": 9, \"schemaVersion\": 9,", StringComparison.Ordinal));
        else
            lean = Encoding.UTF8.GetBytes("-- drift\n" + Encoding.UTF8.GetString(lean));
        ExtractionException? exception = Assert.Throws<ExtractionException>(() =>
            ReceiptDependencyAudit.ValidateUpstreamArtifactsForTest(_root, manifest, ir, lean));
        Assert.That(exception!.Message, Does.StartWith("fold.receipt.upstream: " + diagnostic));
    }

    private byte[] Read(string relativePath) => File.ReadAllBytes(Path.Combine(_root, relativePath));

    private static byte[] Serialize(JsonNode value) =>
        Encoding.UTF8.GetBytes(value.ToJsonString(JsonOptions).ReplaceLineEndings("\n") + "\n");
}
