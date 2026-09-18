// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.OrdinaryTransactionRefundAdapterExtractor.Test;

[TestFixture]
public class ArtifactTests
{
    [TestCase("{\"field\":1,\"field\":2}")]
    [TestCase("{\"field\":1,\"Field\":2}")]
    [TestCase("{\"outer\":[{\"x\":0,\"X\":1}]}")]
    public void Duplicate_and_case_aliased_JSON_fields_are_rejected(string json) =>
        Assert.That(() => Artifacts.RejectDuplicateProperties(Encoding.UTF8.GetBytes(json)), Throws.TypeOf<AdmissionException>());

    [Test]
    public void Interrupted_single_file_publication_preserves_old_bytes_and_cleans_its_temporary()
    {
        using ArtifactDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "artifact.json");
        byte[] old = Encoding.UTF8.GetBytes("old");
        Artifacts.AtomicWrite(path, old);
        Assert.That(() => Artifacts.AtomicWrite(path, Encoding.UTF8.GetBytes("new"),
            () => throw new IOException("simulated pre-replace failure")), Throws.TypeOf<IOException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(old));
            Assert.That(Directory.EnumerateFiles(temporary.Path).Select(System.IO.Path.GetFileName), Is.EqualTo(new[] { "artifact.json" }));
        }
    }

    [Test]
    public void Interrupted_triplet_keeps_manifest_last_and_removes_unpublished_temporary([Range(0, 2)] int failAt)
    {
        using ArtifactDirectory temporary = new();
        byte[] old = Encoding.UTF8.GetBytes("old");
        byte[] next = Encoding.UTF8.GetBytes("new");
        string[] names = [Artifacts.Name + ".ir.json", Artifacts.Name + ".lean", Artifacts.Name + ".source-manifest.json"];
        Artifacts.PublishTriplet(temporary.Path, old, old, old);
        List<string> attempts = [];
        Assert.That(() => Artifacts.PublishTriplet(temporary.Path, next, next, next, name =>
        {
            attempts.Add(name);
            if (attempts.Count - 1 == failAt) throw new IOException("simulated interrupted triplet publication");
        }), Throws.TypeOf<IOException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attempts, Is.EqualTo(names.Take(failAt + 1)));
            Assert.That(Directory.EnumerateFiles(temporary.Path).Select(System.IO.Path.GetFileName), Is.EquivalentTo(names));
            for (int index = 0; index < names.Length; index++)
                Assert.That(File.ReadAllBytes(System.IO.Path.Combine(temporary.Path, names[index])), Is.EqualTo(index < failAt ? next : old), names[index]);
            Assert.That(File.ReadAllBytes(System.IO.Path.Combine(temporary.Path, names[2])), Is.EqualTo(old));
        }
    }

    [Test]
    public void Checked_artifacts_match_fresh_compiled_source_and_canonical_emission() =>
        Assert.DoesNotThrow(() => Artifacts.Check(AdmissionTests.Root(),
            System.IO.Path.Combine(AdmissionTests.Root(), Artifacts.Package, "Generated")));

    [TestCaseSource(nameof(CoordinatedMutations))]
    public void Coordinated_IR_and_artifact_hash_edits_cannot_replace_live_source_admission(string name, Action<JsonObject> change)
    {
        string root = AdmissionTests.Root();
        string generated = System.IO.Path.Combine(root, Artifacts.Package, "Generated");
        Artifacts.Check(root, generated);
        using ArtifactDirectory temporary = new();
        JsonObject ir = JsonNode.Parse(File.ReadAllBytes(System.IO.Path.Combine(generated, Artifacts.Name + ".ir.json")))!.AsObject();
        change(ir);
        JsonObject closure = new()
        {
            ["admission"] = ir["admission"]!.DeepClone(), ["dependencies"] = ir["dependencies"]!.DeepClone(),
        };
        ir["sourceClosureSha256"] = CompilerReferences.Hash(Artifacts.Serialize(closure));
        byte[] mutated = Artifacts.Serialize(ir);
        JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(System.IO.Path.Combine(generated, Artifacts.Name + ".source-manifest.json")))!.AsObject();
        manifest["ir"]!["sha256"] = CompilerReferences.Hash(mutated);
        manifest["sourceClosureSha256"] = ir["sourceClosureSha256"]!.DeepClone();
        manifest["dependencies"] = ir["dependencies"]!.DeepClone();
        Artifacts.AtomicWrite(System.IO.Path.Combine(temporary.Path, Artifacts.Name + ".ir.json"), mutated);
        Artifacts.AtomicWrite(System.IO.Path.Combine(temporary.Path, Artifacts.Name + ".lean"),
            File.ReadAllBytes(System.IO.Path.Combine(generated, Artifacts.Name + ".lean")));
        Artifacts.AtomicWrite(System.IO.Path.Combine(temporary.Path, Artifacts.Name + ".source-manifest.json"), Artifacts.Serialize(manifest));
        Assert.That(() => Artifacts.Check(root, temporary.Path), Throws.TypeOf<AdmissionException>(), name);
    }

    private static IEnumerable<TestCaseData> CoordinatedMutations()
    {
        yield return Mutation("erase_members", ir => ir["admission"]!["admission"]!["members"] = new JsonArray());
        yield return Mutation("erase_compiler_support", ir => ir["admission"]!["admission"]!["sources"]!.AsArray().RemoveAt(20));
        yield return Mutation("reverse_halt_order", ir =>
        {
            JsonArray stages = ir["admission"]!["plan"]!["haltStages"]!.AsArray();
            stages[0] = "clearExecution";
            stages[2] = "refundState";
        });
        yield return Mutation("replace_source_expression", ir => ir["admission"]!["plan"]!["expressions"]![0]!["canonicalSyntax"] = "0");
        yield return Mutation("repin_source", ir => ir["admission"]!["admission"]!["sources"]![0]!["sha256"] = new string('0', 64));
        yield return Mutation("repin_reference", ir => ir["admission"]!["admission"]!["references"]![0]!["sha256"] = new string('0', 64));
        yield return Mutation("repin_upstream", ir => ir["dependencies"]!.AsArray()[^1]!["sha256"] = new string('0', 64));
        yield return Mutation("drop_upstream", ir => ir["dependencies"]!.AsArray().RemoveAt(ir["dependencies"]!.AsArray().Count - 1));
    }

    private static TestCaseData Mutation(string name, Action<JsonObject> change) =>
        new TestCaseData(name, change).SetName("refund_artifact_" + name);

    private sealed class ArtifactDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("refund-artifact-").FullName;

        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(Path)) File.Delete(file);
            Directory.Delete(Path);
        }
    }
}
