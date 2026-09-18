// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor.Test;

public sealed partial class SequentialBlockPostTransactionFinalizationExtractorTests
{
    private static string ReplaceOnce(string source, string before, string after) =>
        ReplaceOnce(source, before, after, occurrence: 1);

    private static string PublicationDeclaration(string name) => name switch
    {
        "validateProcessedBlock" => "private void ValidateProcessedBlock(",
        "postValidation" => "protected virtual void PostValidation(",
        "storeTxReceipts" => "private void StoreTxReceipts(",
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static void AssertPublicationSourceRejected(string path, string changed, string diagnostic)
    {
        string root = FindRepoRoot();
        Assert.That(changed, Is.Not.EqualTo(File.ReadAllText(Path.Combine(root, path))));
        Dictionary<string, byte[]> overrides = new() { [path] = Encoding.UTF8.GetBytes(changed) };
        ProcessOneValidatedPublicationExtractor.RequireCompilationForTest(root, overrides);
        Assert.That(() => ProcessOneValidatedPublicationExtractor.AuditForTest(root, overrides),
            Throws.TypeOf<ExtractionException>().With.Message.Contains(diagnostic));
    }

    [Test]
    public void Publication_synchronous_helpers_reject_compile_valid_execution_changes(
        [Values("validateProcessedBlock", "postValidation", "storeTxReceipts")] string name,
        [Values("async", "conditional", "alias", "synchronized")] string mutation)
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), Extractor.BlockProcessorPath));
        string declaration = PublicationDeclaration(name);
        string replacement = mutation switch
        {
            "async" => declaration.Replace("void", "async void", StringComparison.Ordinal),
            "conditional" => "[System.Diagnostics.Conditional(\"PUBLICATION_NEVER_DEFINED\")]\n    " + declaration,
            "alias" => "[PublicationConditional(\"PUBLICATION_NEVER_DEFINED\")]\n    " + declaration,
            "synchronized" => "[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]\n    " + declaration,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        if (mutation == "alias") source = "using PublicationConditional = System.Diagnostics.ConditionalAttribute;\n" + source;
        AssertPublicationSourceRejected(Extractor.BlockProcessorPath, ReplaceOnce(source, declaration, replacement),
            "publication.synchronous." + name);
    }

    [Test]
    public void Publication_synchronous_helpers_reject_compile_valid_body_changes(
        [Values("validateProcessedBlock", "postValidation", "storeTxReceipts")] string name,
        [Values("extern", "iterator", "partial")] string mutation)
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), Extractor.BlockProcessorPath));
        string methodName = name switch { "validateProcessedBlock" => "ValidateProcessedBlock", "postValidation" => "PostValidation", _ => "StoreTxReceipts" };
        MethodDeclarationSyntax declaration = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == methodName);
        string parameters = declaration.ParameterList.ToString();
        string replacement = mutation switch
        {
            "extern" => $"private extern void {methodName}{parameters};",
            "iterator" => $"private System.Collections.Generic.IEnumerable<int> {methodName}{parameters} {{ yield break; }}",
            "partial" => $"partial void {methodName}{parameters};",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        source = source[..declaration.SpanStart] + replacement + source[declaration.Span.End..];
        AssertPublicationSourceRejected(Extractor.BlockProcessorPath, source, "publication.synchronous." + name);
    }

    [Test]
    public void Publication_property_operations_reject_compile_valid_identity_changes(
        [Values("AccountChanges", "ExecutionRequests", "GeneratedBlockAccessList", "EncodedBlockAccessList")] string property,
        [Values("target", "source", "receiver-conversion", "null")] string mutation)
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), Extractor.BlockProcessorPath));
        string before = $"suggestedBlock.{property} = processedBlock.{property}";
        string after = mutation switch
        {
            "target" => $"processedBlock.{property} = processedBlock.{property}",
            "source" => $"suggestedBlock.{property} = suggestedBlock.{property}",
            "receiver-conversion" => $"suggestedBlock.{property} = ((Block)processedBlock).{property}",
            "null" => $"suggestedBlock.{property} = null",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        AssertPublicationValueRejected(ReplaceOnce(source, before, after));
    }

    [Test]
    public void Publication_call_operations_reject_compile_valid_argument_changes(
        [Values("ProcessBlock", "ValidateProcessedBlock", "PostValidation", "StoreTxReceipts", "InsertDeferred", "validator")] string site,
        [Values("identity", "conversion")] string mutation)
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), Extractor.BlockProcessorPath));
        (string before, string after) = site switch
        {
            "ProcessBlock" => ("receipts = ProcessBlock(block,", "receipts = ProcessBlock(" + (mutation == "identity" ? "suggestedBlock" : "(Block)block") + ","),
            "ValidateProcessedBlock" => ("ValidateProcessedBlock(suggestedBlock, options, block, receipts)", "ValidateProcessedBlock(" + (mutation == "identity" ? "block" : "(Block)suggestedBlock") + ", options, block, receipts)"),
            "PostValidation" => ("PostValidation(suggestedBlock, block, receipts, options)", "PostValidation(" + (mutation == "identity" ? "block" : "(Block)suggestedBlock") + ", block, receipts, options)"),
            "StoreTxReceipts" => ("StoreTxReceipts(block, receipts, spec)", "StoreTxReceipts(" + (mutation == "identity" ? "suggestedBlock" : "(Block)block") + ", receipts, spec)"),
            "InsertDeferred" => ("receiptStorage.InsertDeferred(block, txReceipts, spec)", "receiptStorage.InsertDeferred(" + (mutation == "identity" ? "null!" : "(Block)block") + ", txReceipts, spec)"),
            "validator" => ("blockValidator.ValidateProcessedBlock(block, receipts, suggestedBlock, out string? error)", "blockValidator.ValidateProcessedBlock(" + (mutation == "identity" ? "suggestedBlock" : "(Block)block") + ", receipts, suggestedBlock, out string? error)"),
            _ => throw new ArgumentOutOfRangeException(nameof(site)),
        };
        AssertPublicationValueRejected(ReplaceOnce(source, before, after));
    }

    private static void AssertPublicationValueRejected(string changed)
    {
        string root = FindRepoRoot();
        Dictionary<string, byte[]> overrides = new() { [Extractor.BlockProcessorPath] = Encoding.UTF8.GetBytes(changed) };
        ProcessOneValidatedPublicationExtractor.RequireCompilationForTest(root, overrides);
        Assert.That(() => ProcessOneValidatedPublicationExtractor.AuditValueIdentitiesForTest(root, overrides),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("publication.values:"));
        Assert.That(() => ProcessOneValidatedPublicationExtractor.AuditForTest(root, overrides), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Publication_serialized_callable_and_compiler_admission_cannot_be_coordinately_weakened(
        [Values("async", "conditional", "inherited", "kind", "body", "base", "interface", "compiler", "support", "tokens")] string mutation)
    {
        string root = FindRepoRoot();
        ProcessOnePublicationIrDocument document = ProcessOneValidatedPublicationExtractor.AuditForTest(root);
        PublicationSynchronousCallable first = document.SynchronousCallables[0];
        ProcessOnePublicationIrDocument changed = mutation switch
        {
            "compiler" => document with { CompilerClosure = document.CompilerClosure with { Count = 433 } },
            "support" => document with { CompilerClosure = document.CompilerClosure with { SupportSources = [] } },
            "tokens" => document with { Sources = [document.Sources[0] with { SyntaxSha256 = new string('0', 64) }, .. document.Sources.Skip(1)] },
            _ => document with { SynchronousCallables = [mutation switch
            {
                "async" => first with { IsAsync = true },
                "conditional" => first with { HasConditionalAttribute = true },
                "inherited" => first with { HasInheritedConditionalAttribute = true },
                "kind" => first with { MethodKind = "LocalFunction" },
                "body" => first with { BodyKind = "none" },
                "base" => first with { BaseTypes = ["PublicationBase"] },
                "interface" => first with { Interfaces = ["IPublicationSentinel"] },
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            }, .. document.SynchronousCallables.Skip(1)] },
        };
        Assert.That(() => ProcessOneValidatedPublicationExtractor.ValidateAuditForTest(root, changed), Throws.TypeOf<ExtractionException>());
        Assert.That(() => ProcessOneValidatedPublicationExtractor.EmitLeanForTest(root, changed), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Publication_checked_artifacts_rederive_source_after_coordinated_repinning(
        [Values("shadow", "roles", "async", "directive", "offset")] string mutation)
    {
        string root = FindRepoRoot();
        ProcessOnePublicationIrDocument document = ProcessOneValidatedPublicationExtractor.AuditForTest(root);
        string source = File.ReadAllText(Path.Combine(root, Extractor.BlockProcessorPath));
        string changed = mutation switch
        {
            "shadow" => ReplaceOnce(source, "Block block = PrepareBlockForProcessing(suggestedBlock);",
                "void StoreTxReceipts(Block block, TxReceipt[] receipts, IReleaseSpec spec) { }\n        Block block = PrepareBlockForProcessing(suggestedBlock);"),
            "roles" => ReplaceOnce(source, "PostValidation(Block suggestedBlock, Block processedBlock", "PostValidation(Block processedBlock, Block suggestedBlock"),
            "async" => ReplaceOnce(source, "private void StoreTxReceipts(", "private async void StoreTxReceipts("),
            "directive" => "#if RELEASE\n// alternate source selection\n#endif\n" + source,
            _ => "\n" + source,
        };
        byte[] sourceBytes = Encoding.UTF8.GetBytes(changed);
        Dictionary<string, byte[]> overrides = new() { [Extractor.BlockProcessorPath] = sourceBytes };
        ProcessOneValidatedPublicationExtractor.RequireCompilationForTest(root, overrides);
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string syntax = string.Join(";", CSharpSyntaxTree.ParseText(changed).GetRoot().DescendantTokens().Select(token => $"{token.RawKind}:{token.Text}"));
        PublicationSourceIdentity[] sources = [document.Sources[0] with { Sha256 = Hash(sourceBytes), SyntaxSha256 = Hash(Encoding.UTF8.GetBytes(syntax)) }, .. document.Sources.Skip(1)];
        PublicationPinDocument pins = new(1, "process-one-publication-source-pinned",
            sources.Select(identity => new SourcePin(identity.Path, identity.Role, identity.Sha256)).ToArray());
        ProcessOnePublicationIrDocument changedDocument = document with
        {
            Sources = sources,
            SourceClosureSha256 = Hash(Encoding.UTF8.GetBytes(string.Join("\n", sources.Select(identity =>
                $"{identity.Path}|{identity.Role}|{identity.Sha256}|{identity.SyntaxSha256}")))),
        };
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        byte[] irBytes = JsonSerializer.SerializeToUtf8Bytes(changedDocument, options);
        string checkedDirectory = Path.Combine(root, Extractor.DefaultOutputPath);
        JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(checkedDirectory, ProcessOneValidatedPublicationExtractor.ManifestFileName)))!.AsObject();
        manifest["sources"] = JsonSerializer.SerializeToNode(sources, options);
        manifest["sourceClosureSha256"] = changedDocument.SourceClosureSha256;
        manifest["ir"]!["sha256"] = Hash(irBytes);
        using TemporaryDirectory output = new();
        File.WriteAllBytes(Path.Combine(output.Path, ProcessOneValidatedPublicationExtractor.IrFileName), irBytes);
        File.WriteAllText(Path.Combine(output.Path, ProcessOneValidatedPublicationExtractor.ManifestFileName), manifest.ToJsonString(options));
        File.Copy(Path.Combine(checkedDirectory, ProcessOneValidatedPublicationExtractor.LeanFileName),
            Path.Combine(output.Path, ProcessOneValidatedPublicationExtractor.LeanFileName));
        string diagnostic = mutation switch
        {
            "shadow" => "internal call does not target its exact member declaration",
            "roles" => "exact declaration parameter roles changed",
            "async" => "synchronous concrete void body",
            "directive" => "conditional compilation or source-selection",
            _ => "source identities drifted",
        };
        Assert.That(() => ProcessOneValidatedPublicationExtractor.ValidateCheckedInWithSourceOverridesForTest(root, output.Path, overrides, pins),
            Throws.TypeOf<ExtractionException>().With.Message.Contains(diagnostic));
    }

    [Test]
    public void Publication_callables_reject_compile_valid_inherited_and_dispatch_changes(
        [Values("conditional", "alias", "indirect", "synchronized", "plain", "base-only", "interface")] string mutation)
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), Extractor.BlockProcessorPath));
        if (mutation == "interface")
        {
            source = ReplaceOnce(source, ": IBlockProcessor", ": IBlockProcessor, IPublicationSentinel");
            source += "\npublic interface IPublicationSentinel { }";
        }
        else
        {
            source = ReplaceOnce(source, ": IBlockProcessor", ": PublicationBase, IBlockProcessor");
            if (mutation == "base-only") source += "\npublic class PublicationBase { }";
            else
            {
                source = ReplaceOnce(source, "protected virtual void PostValidation(", "protected override void PostValidation(");
                if (mutation == "alias") source = "using PublicationConditional = System.Diagnostics.ConditionalAttribute;\n" + source;
                string attribute = mutation switch
                {
                    "conditional" or "indirect" => "[System.Diagnostics.Conditional(\"PUBLICATION_NEVER_DEFINED\")]",
                    "alias" => "[PublicationConditional(\"PUBLICATION_NEVER_DEFINED\")]",
                    "synchronized" => "[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]",
                    _ => "",
                };
                string baseName = mutation == "indirect" ? "PublicationRoot" : "PublicationBase";
                source += $$"""

                    public class {{baseName}}
                    {
                        {{attribute}}
                        protected virtual void PostValidation(Block suggestedBlock, Block processedBlock, TxReceipt[] receipts, ProcessingOptions options) { }
                    }
                    """;
                if (mutation == "indirect") source += """

                    public class PublicationBase : PublicationRoot
                    {
                        protected override void PostValidation(Block suggestedBlock, Block processedBlock, TxReceipt[] receipts, ProcessingOptions options) { }
                    }
                    """;
            }
        }
        AssertPublicationSourceRejected(Extractor.BlockProcessorPath, source,
            mutation is "conditional" or "alias" or "indirect" ? "inherited ConditionalAttribute" : "exact callable lineage changed");
    }

    [Test]
    public void Publication_internal_edges_reject_compile_valid_local_shadowing(
        [Values("ValidateProcessedBlock", "StoreTxReceipts", "ProcessBlock")] string name)
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), Extractor.BlockProcessorPath));
        string local = name switch
        {
            "ValidateProcessedBlock" => "void ValidateProcessedBlock(Block suggested, ProcessingOptions flags, Block processed, TxReceipt[] receipts) { }",
            "StoreTxReceipts" => "void StoreTxReceipts(Block processed, TxReceipt[] receipts, IReleaseSpec spec) { }",
            "ProcessBlock" => "TxReceipt[] ProcessBlock(Block processed, IBlockTracer tracer, ProcessingOptions flags, IReleaseSpec spec, CancellationToken token) => Array.Empty<TxReceipt>();",
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        source = ReplaceOnce(source, "Block block = PrepareBlockForProcessing(suggestedBlock);",
            local + "\n        Block block = PrepareBlockForProcessing(suggestedBlock);");
        AssertPublicationSourceRejected(Extractor.BlockProcessorPath, source, "internal call does not target its exact member declaration");
    }

    [Test]
    public void Publication_parameter_roles_reject_compile_valid_same_type_swaps(
        [Values("postValidation", "validateProcessedBlock", "validator")] string name)
    {
        string path = name == "validator" ? "src/Nethermind/Nethermind.Consensus/Validators/BlockValidator.cs" : Extractor.BlockProcessorPath;
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), path));
        (string before, string after) = name switch
        {
            "postValidation" => ("PostValidation(Block suggestedBlock, Block processedBlock", "PostValidation(Block processedBlock, Block suggestedBlock"),
            "validateProcessedBlock" => ("ValidateProcessedBlock(Block suggestedBlock, ProcessingOptions options, Block block", "ValidateProcessedBlock(Block block, ProcessingOptions options, Block suggestedBlock"),
            "validator" => ("ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, out", "ValidateProcessedBlock(Block suggestedBlock, TxReceipt[] receipts, Block processedBlock, out"),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        AssertPublicationSourceRejected(path, ReplaceOnce(source, before, after), "exact declaration parameter roles changed");
    }

    [Test]
    public void Publication_rejects_compile_valid_conditional_source_selection()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), Extractor.BlockProcessorPath));
        source = ReplaceOnce(source, "        ValidateProcessedBlock(suggestedBlock, options, block, receipts);",
            "#if RELEASE\n        return (block, receipts);\n#else\n        ValidateProcessedBlock(suggestedBlock, options, block, receipts);\n#endif");
        AssertPublicationSourceRejected(Extractor.BlockProcessorPath, source, "conditional compilation or source-selection");
    }

    [Test]
    public void Finalization_rejects_compile_valid_conditional_source_selection(
        [Values("semantic", "support")] string boundary)
    {
        string root = FindRepoRoot();
        string path = boundary == "semantic" ? Extractor.BlockProcessorPath : Extractor.CompilerSupportSources[0].Path;
        string source = File.ReadAllText(Path.Combine(root, path));
        string changed = "#if RELEASE\n// alternate production source-selection branch\n#endif\n" + source;
        Dictionary<string, byte[]> overrides = new() { [path] = Encoding.UTF8.GetBytes(changed) };
        if (boundary == "support")
        {
            Extractor.AuditCompilerSupportForTest(root, overrides, compileOnly: true, enforcePins: false);
            Assert.That(() => Extractor.AuditCompilerSupportForTest(root, overrides, compileOnly: false, enforcePins: false),
                Throws.TypeOf<ExtractionException>().With.Message.Contains("conditional compilation or source-selection"));
        }
        else
        {
            Extractor.RequireCompilationForTest(root, overrides);
            using TemporaryDirectory output = new();
            Assert.That(() => Extractor.ExtractForTest(root, output.Path, overrides),
                Throws.TypeOf<ExtractionException>().With.Message.Contains("conditional compilation or source-selection"));
        }
    }
}
