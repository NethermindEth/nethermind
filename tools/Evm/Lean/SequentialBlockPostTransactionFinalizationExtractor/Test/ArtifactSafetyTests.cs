// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor.Test;

[TestFixture]
public sealed class ArtifactSafetyTests
{
    [TestCase("-- admitted axiom theorem sorry\ndef admission := \"axiom\"", false)]
    [TestCase("/- outer sorry /- theorem -/ admit -/\ndef admission := 1", false)]
    [TestCase("def admission := 1", false)]
    [TestCase("def broken : False := by sorry", true)]
    [TestCase("def broken : False := by admit", true)]
    [TestCase("axiom injected : False", true)]
    [TestCase("theorem injected : True := by trivial", true)]
    [TestCase("example : True := by trivial", true)]
    [TestCase("def broken : True := by native_decide", true)]
    public void Lean_tokens_exclude_comments_strings_and_identifier_substrings(string source, bool forbidden) =>
        Assert.That(ArtifactSafety.ContainsProofToken(source, generated: true), Is.EqualTo(forbidden));

    [TestCase("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [TestCase("{\"items\":[{\"symbol\":\"a\",\"symbol\":\"b\"}]}")]
    [TestCase("{\"items\":{\"nested\":{\"value\":1,\"value\":2}}}")]
    public void Duplicate_json_is_rejected_at_every_depth(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.That(() => ArtifactSafety.ValidateJson(document.RootElement),
            Throws.TypeOf<ExtractionException>().With.Message.StartsWith("Duplicate finalization JSON property:"));
    }

    [Test]
    public void Json_baseline_accepts_distinct_names()
    {
        using JsonDocument document = JsonDocument.Parse("{\"items\":[{\"symbol\":\"a\",\"value\":1}]}");
        Assert.That(() => ArtifactSafety.ValidateJson(document.RootElement), Throws.Nothing);
    }

    [Test]
    public void Atomic_failure_preserves_previous_bytes_and_removes_staging_file(
        [Values("SequentialBlockPostTransactionFinalization", "ProcessOneValidatedPublication")] string artifact,
        [Values(".ir.json", ".lean", ".source-manifest.json")] string extension)
    {
        string directory = Path.Combine(Path.GetTempPath(), "finalization-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, artifact + extension);
            byte[] original = Encoding.UTF8.GetBytes("previous accepted bytes");
            byte[] replacement = Encoding.UTF8.GetBytes("new artifact bytes");
            ArtifactSafety.AtomicWrite(path, original);
            Assert.That(() => ArtifactSafety.AtomicWrite(path, replacement,
                    () => throw new IOException("injected before atomic replacement")),
                Throws.TypeOf<IOException>().With.Message.EqualTo("injected before atomic replacement"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
            Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { path }));
            ArtifactSafety.AtomicWrite(path, replacement);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(replacement));
            Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { path }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
