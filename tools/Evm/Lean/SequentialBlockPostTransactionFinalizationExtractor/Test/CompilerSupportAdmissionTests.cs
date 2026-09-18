// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor.Test;

public sealed partial class SequentialBlockPostTransactionFinalizationExtractorTests
{
    [Test]
    public void Only_the_existing_executor_metrics_alias_is_admitted(
        [Values("rename", "qualification", "extra")] string mutation)
    {
        string root = FindRepoRoot();
        string path = "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs";
        string source = File.ReadAllText(Path.Combine(root, path));
        string changed = mutation switch
        {
            "rename" => source.Replace("using Metrics =", "using EvmMetrics =", StringComparison.Ordinal)
                .Replace("Metrics.", "EvmMetrics.", StringComparison.Ordinal),
            "qualification" => source.Replace("using Metrics = Nethermind.Evm.Metrics;", "using Metrics = global::Nethermind.Evm.Metrics;", StringComparison.Ordinal),
            "extra" => "using ExtraAlias = System.String;\n" + source,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Dictionary<string, byte[]> overrides = new() { [path] = Encoding.UTF8.GetBytes(changed) };
        Extractor.RequireCompilationForTest(root, overrides);
        using TemporaryDirectory output = new();
        Assert.That(() => Extractor.ExtractForTest(root, output.Path, overrides),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("unsupported alias binding"));
        Assert.That(Directory.EnumerateFileSystemEntries(output.Path), Is.Empty);
    }

    [Test]
    public void Compiler_support_is_a_distinct_exact_pinned_roster()
    {
        IrDocument document = ReadCheckedIr();
        Assert.That(document.CompilerClosure.SupportSources, Is.EqualTo(Extractor.CompilerSupportSources));
        Assert.That(document.CompilerClosure.SupportSources.Select(static item => item.Path)
            .Intersect(document.Sources.Select(static item => item.Path)), Is.Empty);
    }

    [Test]
    public void Compiler_support_roster_mutations_are_rejected(
        [Values("missing", "extra", "duplicate", "reordered", "digest", "semantic-tree")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        PublicationDependencyIdentity[] sources = document.CompilerClosure.SupportSources;
        PublicationDependencyIdentity[] changed = mutation switch
        {
            "missing" => sources[1..],
            "extra" => [.. sources, new("extra.cs", new string('0', 64))],
            "duplicate" => [sources[0], .. sources[0..^1]],
            "reordered" => sources.Reverse().ToArray(),
            "digest" => [sources[0] with { Sha256 = new string('0', 64) }, .. sources[1..]],
            "semantic-tree" => [sources[0] with { Path = Extractor.BlockProcessorPath }, .. sources[1..]],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.That(() => Extractor.ValidateIrForTest(document with
        {
            CompilerClosure = document.CompilerClosure with { SupportSources = changed },
        }), Throws.TypeOf<ExtractionException>().With.Message.EqualTo("The deterministic compiler-reference closure changed."));
    }

    [Test]
    public void Compile_valid_support_overloads_and_callbacks_cannot_change_admission(
        [Values("overload", "reentry", "callback", "metadata-callback")] string mutation)
    {
        string root = FindRepoRoot();
        string path = Extractor.CompilerSupportSources[0].Path;
        string source = File.ReadAllText(Path.Combine(root, path));
        string added = mutation switch
        {
            "overload" => "private void CommitState(object spec) { }",
            "reentry" => "private void SupportReentry(IReleaseSpec spec) { CommitState(spec); }",
            "callback" => "private void SupportCallback() { System.Action callback = () => { }; callback(); }",
            "metadata-callback" => "private void SupportCallback() { System.Array.Sort(new[] { 1, 2 }, (a, b) => a.CompareTo(b)); }",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        string changed = source.Replace("public partial class BlockProcessor\n{", "public partial class BlockProcessor\n{\n" + added,
            StringComparison.Ordinal);
        if (changed == source) changed = source.Replace("public partial class BlockProcessor\r\n{", "public partial class BlockProcessor\r\n{\r\n" + added,
            StringComparison.Ordinal);
        Assert.That(changed, Is.Not.EqualTo(source));
        Dictionary<string, byte[]> overrides = new() { [path] = Encoding.UTF8.GetBytes(changed) };
        Extractor.AuditCompilerSupportForTest(root, overrides, compileOnly: true, enforcePins: false);
        Assert.That(() => Extractor.AuditCompilerSupportForTest(root, overrides, compileOnly: false, enforcePins: true),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Finalization compiler-support identity or classification changed."));
        if (mutation != "overload")
            Assert.That(() => Extractor.AuditCompilerSupportForTest(root, overrides, compileOnly: false, enforcePins: false),
                Throws.TypeOf<ExtractionException>().With.Message.Contains(mutation == "reentry" ? "re-enters" : "callback"));
    }
}
