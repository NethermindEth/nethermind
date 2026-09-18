// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.OrdinaryEvmCompletionExtractor.Test;

[TestFixture]
public class ArtifactTests
{
    private static IEnumerable<string> OwnSources => Artifacts.OwnFiles.Where(static path => path.EndsWith(".cs", StringComparison.Ordinal));

    [Test]
    public void Every_own_source_requires_a_manifest_entry([ValueSource(nameof(OwnSources))] string omitted)
    {
        string[] declared = Artifacts.OwnFiles.Where(path => path != omitted).ToArray();
        AdmissionException? error = Assert.Throws<AdmissionException>(() => Artifacts.RequireOwnSourceCoverage(OwnSources, declared));
        Assert.That(error!.Message, Is.EqualTo("own-tool.missing." + omitted));
    }

    [Test]
    public void A_new_default_compile_source_requires_explicit_admission()
    {
        AdmissionException? error = Assert.Throws<AdmissionException>(() => Artifacts.RequireOwnSourceCoverage(
            OwnSources.Append("NewLowering.cs"), Artifacts.OwnFiles));
        Assert.That(error!.Message, Is.EqualTo("own-tool.missing.NewLowering.cs"));
    }

    [TestCase("{\"x\":0,\"x\":1}")]
    [TestCase("{\"x\":0,\"X\":1}")]
    [TestCase("{\"items\":[{\"x\":0,\"X\":1}]}")]
    public void Duplicate_and_case_aliased_properties_are_rejected(string json) =>
        Assert.That(() => Artifacts.RejectDuplicateProperties(Encoding.UTF8.GetBytes(json)), Throws.TypeOf<AdmissionException>());

    [Test]
    public void Failed_replace_leaves_original_bytes_and_no_temporary()
    {
        string directory = Path.Combine("D:/tmp/formal-verify", "ordinary-completion-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "artifact.json");
        try
        {
            Artifacts.AtomicWrite(file, "old"u8.ToArray());
            Assert.That(() => Artifacts.AtomicWrite(file, "new"u8.ToArray(), () => throw new IOException("interruption")), Throws.TypeOf<IOException>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(File.ReadAllText(file), Is.EqualTo("old"));
                Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
            }
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
