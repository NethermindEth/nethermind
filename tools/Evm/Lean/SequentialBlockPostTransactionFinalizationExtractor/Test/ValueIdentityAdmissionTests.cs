// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor.Test;

public sealed partial class SequentialBlockPostTransactionFinalizationExtractorTests
{
    [Test]
    public void Unchanged_source_identities_and_normal_tail_dominance_are_admitted()
    {
        IrDocument document = ReadCheckedIr();
        SourceEntryAdapterIdentity adapter = document.SourceEntryAdapters.Single();
        Assert.That(adapter.HeaderBinding.CanonicalSyntax, Is.EqualTo("header=block.Header"));
        Assert.That(adapter.HeaderValueBinding.SymbolId, Is.EqualTo("global::Nethermind.Core.Block.Header"));
        Assert.That(adapter.PreservedValues.Select(static value => value.Name), Is.EqualTo(new[]
        {
            "header", "block", "spec", "blockTracer", "_stateProvider", "_systemContractHandler", "ReceiptsTracer",
        }));
        Assert.That(adapter.PreservedValues.Sum(static value => value.ReadPositions.Length), Is.EqualTo(48));
        Assert.That(() => Extractor.ValidateSerializedTailDominance(document), Throws.Nothing);

        ControlFlowIdentity flow = document.ControlFlows.Single(static flow => flow.Id == "block.processBlock");
        Dictionary<int, HashSet<int>> dominators = Extractor.ComputeEntryDominators(
            flow.ReachableBlocks[0], flow.ReachableBlocks, flow.Edges);
        Assert.That(flow.ExceptionHandlerEntries.Any(dominators.ContainsKey), Is.False,
            "The empty catch remains independently connected, not an ordinary entry to the normal-return proof.");
    }

    [Test]
    public void Disconnected_catch_cannot_erase_prior_normal_dominators([Values(false, true)] bool reachableHandler)
    {
        string[] edges = ["0->1", "1->2", "2->3", "4->3", "3->5"];
        if (reachableHandler) edges = [.. edges, "0->4"];
        Dictionary<int, HashSet<int>> dominators = Extractor.ComputeEntryDominators(0, [0, 1, 2, 3, 4, 5], edges);
        Assert.That(dominators.ContainsKey(4), Is.EqualTo(reachableHandler));
        Assert.That(dominators[5].Contains(1), Is.EqualTo(!reachableHandler));
        Assert.That(dominators[5].Contains(2), Is.EqualTo(!reachableHandler));
        Assert.That(dominators[5], Does.Contain(0).And.Contain(3).And.Contain(5));
    }

    [Test]
    public void Serialized_normal_entry_bypasses_fail_dominance_without_an_earlier_digest_failure(
        [Values("return", "catch")] string target)
    {
        IrDocument document = ReadCheckedIr();
        ControlFlowIdentity flow = document.ControlFlows.Single(static flow => flow.Id == "block.processBlock");
        int destination = target == "catch" ? flow.ExceptionHandlerEntries.Single() :
            document.Anchors.Single(static anchor => anchor.Id == "block.return-receipts").Binding.ControlFlowBlock;
        ControlFlowIdentity altered = flow with
        {
            Edges = [.. flow.Edges, $"{flow.ReachableBlocks[0]}->{destination}"],
        };
        IrDocument changed = document with
        {
            ControlFlows = document.ControlFlows.Select(item => item.Id == flow.Id ? altered : item).ToArray(),
        };
        Assert.That(() => Extractor.ValidateSerializedTailDominance(changed),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "Serialized normal-tail action 'block.post-transaction-commit' does not dominate the receipts return."));
    }

    [TestCase("header-clone", "Preserved identity 'header' has a competing definition.")]
    [TestCase("header-initializer", "Header identity must be the exact block.Header local initialization.")]
    [TestCase("header-ref-alias", "Preserved identity 'header' escapes through ref/in/out.")]
    [TestCase("header-out", "Preserved identity 'header' escapes through ref/in/out.")]
    [TestCase("header-ref", "Preserved identity 'header' escapes through ref/in/out.")]
    [TestCase("header-alias", "Preserved identity 'header' declaration or exact read/receiver contexts changed.")]
    [TestCase("block-replacement", "Preserved identity 'block' has a competing definition.")]
    [TestCase("spec-replacement", "Preserved identity 'spec' has a competing definition.")]
    [TestCase("tracer-replacement", "Preserved identity 'blockTracer' has a competing definition.")]
    [TestCase("handler-replacement", "Preserved identity '_systemContractHandler' has a competing definition.")]
    [TestCase("receipts-tracer-replacement", "Preserved identity 'ReceiptsTracer' has a competing definition.")]
    [TestCase("state-provider-alias", "Preserved identity '_stateProvider' declaration or exact read/receiver contexts changed.")]
    [TestCase("commit-spec-replacement", "Source-local helper 'CommitState' preservation body changed.")]
    [TestCase("roots-spec-replacement", "Source-local helper 'CommitStateAndStorageRoots' preservation body changed.")]
    [TestCase("state-root-header-replacement", "Source-local helper 'ComputeStateRoot' preservation body changed.")]
    [TestCase("account-block-replacement", "Source-local helper 'SetAccountChanges' preservation body changed.")]
    public void Preserved_identity_source_mutations_compile_before_exact_rejection(string mutation, string diagnostic)
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, Extractor.BlockProcessorPath);
        byte[] original = File.ReadAllBytes(path);
        string source = Encoding.UTF8.GetString(original).Replace("\r\n", "\n", StringComparison.Ordinal);
        string addition = mutation switch
        {
            "header-clone" => "header = header.Clone();",
            "header-ref-alias" => "ref BlockHeader headerAlias = ref header;",
            "header-out" => "System.Runtime.CompilerServices.Unsafe.SkipInit(out header);",
            "header-ref" => "System.Threading.Interlocked.Exchange(ref header, header.Clone());",
            "header-alias" => "BlockHeader headerAlias = header;",
            "block-replacement" => "block = block.WithReplacedHeader(block.Header.Clone());",
            "spec-replacement" => "spec = null!;",
            "tracer-replacement" => "blockTracer = null!;",
            "handler-replacement" => "_systemContractHandler = null!;",
            "receipts-tracer-replacement" => "ReceiptsTracer = new();",
            "state-provider-alias" => "IWorldState stateProviderAlias = _stateProvider;",
            _ => string.Empty,
        };
        string changed = mutation switch
        {
            "header-initializer" => source.Replace("BlockHeader header = block.Header;", "BlockHeader header = block.Header.Clone();", StringComparison.Ordinal),
            "commit-spec-replacement" => source.Replace("        _stateProvider.Commit(spec, commitRoots: false);",
                "        spec = null!;\n        _stateProvider.Commit(spec, commitRoots: false);", StringComparison.Ordinal),
            "roots-spec-replacement" => source.Replace("        _stateProvider.Commit(spec, commitRoots: true);",
                "        spec = null!;\n        _stateProvider.Commit(spec, commitRoots: true);", StringComparison.Ordinal),
            "state-root-header-replacement" => source.Replace("        header.StateRoot = _stateProvider.StateRoot;",
                "        header = header.Clone();\n        header.StateRoot = _stateProvider.StateRoot;", StringComparison.Ordinal),
            "account-block-replacement" => source.Replace("        => block.AccountChanges = _stateProvider.GetAccountChanges();",
                "    {\n        block = block.WithReplacedHeader(block.Header.Clone());\n        block.AccountChanges = _stateProvider.GetAccountChanges();\n    }", StringComparison.Ordinal),
            _ when addition.Length != 0 => source.Replace("        header.Hash = header.CalculateHash();",
                "        " + addition + "\n        header.Hash = header.CalculateHash();", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.That(changed, Is.Not.EqualTo(source));
        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal)
        {
            [Extractor.BlockProcessorPath] = Encoding.UTF8.GetBytes(changed),
        };
        Extractor.RequireCompilationForTest(root, overrides);
        using TemporaryDirectory output = new();
        Assert.That(() => Extractor.ExtractForTest(root, output.Path, overrides),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
        Assert.That(Directory.EnumerateFileSystemEntries(output.Path), Is.Empty);
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
    }

    [Test]
    public void Coordinated_header_binding_and_identity_ledger_redirections_fail_closed(
        [Values("initializer", "symbol", "receiver")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        SourceEntryAdapterIdentity adapter = document.SourceEntryAdapters.Single();
        PreservedValueIdentity identity = adapter.PreservedValues.Single(static value => value.Name == "header");
        string redirected = mutation == "receiver" ? "replacementHeader=block.Header" : "header=block.Header.Clone()";
        TypedBinding binding = mutation == "symbol"
            ? adapter.HeaderBinding with { SymbolId = adapter.HeaderBinding.SymbolId + "Redirected" }
            : adapter.HeaderBinding with
            {
                CanonicalSyntax = redirected,
                SyntaxSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(redirected))).ToLowerInvariant(),
            };
        PreservedValueIdentity alteredIdentity = mutation switch
        {
            "symbol" => identity with { SymbolId = binding.SymbolId },
            "receiver" => identity with
            {
                DefinitionCanonical = redirected,
                ReadContexts = identity.ReadContexts.Select(context => context.Replace("header", "replacementHeader", StringComparison.Ordinal)).ToArray(),
            },
            _ => identity with { DefinitionCanonical = redirected },
        };
        SourceEntryAdapterIdentity changedAdapter = adapter with
        {
            HeaderBinding = binding,
            PreservedValues = adapter.PreservedValues.Select(value => value.Name == "header" ? alteredIdentity : value).ToArray(),
        };
        Assert.That(() => Extractor.ValidateIrForTest(document with { SourceEntryAdapters = [changedAdapter] }),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "Preserved identity 'header' declaration or exact read/receiver contexts changed."));
    }

    [Test]
    public void Preserved_parameter_and_receiver_ir_redirections_fail_closed(
        [Values("block", "spec", "blockTracer", "_stateProvider", "_systemContractHandler", "ReceiptsTracer")] string name)
    {
        IrDocument document = ReadCheckedIr();
        SourceEntryAdapterIdentity adapter = document.SourceEntryAdapters.Single();
        SourceEntryAdapterIdentity changed = adapter with
        {
            PreservedValues = adapter.PreservedValues.Select(value => value.Name == name ? value with
            {
                SymbolId = value.SymbolId + "Redirected",
                DefinitionCanonical = value.DefinitionCanonical + "Redirected",
                ReadContexts = value.ReadContexts.Select(context => context.Replace(name, name + "Redirected", StringComparison.Ordinal)).ToArray(),
            } : value).ToArray(),
        };
        Assert.That(() => Extractor.ValidateIrForTest(document with { SourceEntryAdapters = [changed] }),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                $"Preserved identity '{name}' declaration or exact read/receiver contexts changed."));
    }

    [Test]
    public void Coordinated_helper_member_and_cfg_body_redirections_fail_closed(
        [Values("block.commit-no-roots", "block.commit-roots", "block.compute-state-root", "block.set-account-changes")] string id)
    {
        IrDocument document = ReadCheckedIr();
        MemberIdentity member = document.Members.Single(member => member.Id == id);
        string canonical = member.Binding.CanonicalSyntax.Replace("_stateProvider", "redirectedStateProvider", StringComparison.Ordinal);
        Assert.That(canonical, Is.Not.EqualTo(member.Binding.CanonicalSyntax));
        TypedBinding binding = member.Binding with
        {
            CanonicalSyntax = canonical,
            SyntaxSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(),
        };
        IrDocument changed = document with
        {
            Members = document.Members.Select(item => item.Id == id ? item with { Binding = binding } : item).ToArray(),
            ControlFlows = document.ControlFlows.Select(flow => flow.Id == id ? flow with { Binding = binding } : flow).ToArray(),
        };
        Assert.That(() => Extractor.ValidateIrForTest(changed),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "The source-local helper/member preservation mapping changed."));
    }
}
