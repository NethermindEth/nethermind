// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor.Test;

public sealed partial class SequentialBlockPostTransactionFinalizationExtractorTests
{
    [Test]
    public void Preservation_covers_the_complete_admitted_source_local_closure()
    {
        IrDocument document = ReadCheckedIr();
        Assert.That(document.HelperPreservation.Select(static helper => helper.Member), Is.EqualTo(new[]
        {
            "AccumulateBlockBloom", "ApplyMinerReward", "ApplyMinerRewards", "CalculateBlooms", "CalculateReceiptsRoot",
            "CommitState", "CommitStateAndStorageRoots", "ComputeStateRoot", "CountLogs", "CreateBlockExecutionContext",
            "SetAccountChanges", "ShouldCalculateReceiptsInBackground", "ShouldComputeStateRoot", "TraceMinerReward",
        }));
        Assert.That(document.HelperPreservation.Single(static helper => helper.Member == "ShouldCalculateReceiptsInBackground").Path,
            Is.EqualTo(Extractor.BlockProcessorStandardPath));
        Assert.That(document.HelperPreservation.Single(static helper => helper.Member == "CalculateBlooms").CanonicalSyntax,
            Does.Contain("static(i,receipts)=>{receipts[i].CalculateBloom();returnreceipts;}"));
        Assert.That(() => Extractor.ValidateIrForTest(document), Throws.Nothing);
    }

    [TestCase("handler-rebind", "Pinned source changes preserved receiver identity: _systemContractHandler.")]
    [TestCase("tracer-rebind", "Pinned source changes preserved receiver identity: ReceiptsTracer.")]
    [TestCase("handler-transitive", "Pinned source changes preserved receiver identity: _systemContractHandler.")]
    [TestCase("tracer-transitive", "Pinned source changes preserved receiver identity: ReceiptsTracer.")]
    [TestCase("handler-ref-alias", "Pinned source exposes preserved identity through ref/in/out.")]
    [TestCase("handler-ref-call", "Pinned source exposes preserved identity through ref/in/out.")]
    [TestCase("handler-alias", "Source-local helper 'ApplyMinerRewards' preservation body changed.")]
    [TestCase("tracer-alias", "Source-local helper 'ApplyMinerRewards' preservation body changed.")]
    [TestCase("world-alias", "Source-local helper 'ApplyMinerRewards' preservation body changed.")]
    [TestCase("block-rebind", "Source-local helper 'ApplyMinerRewards' preservation body changed.")]
    [TestCase("spec-out", "Pinned source exposes preserved identity through ref/in/out.")]
    [TestCase("receipt-index", "Pinned source writes returned receipt projection: Index.")]
    [TestCase("receipt-index-compound", "Pinned source writes returned receipt projection: Index.")]
    [TestCase("receipt-index-alias", "Pinned source writes returned receipt projection: Index.")]
    [TestCase("receipt-logs", "Pinned source writes returned receipt projection: Logs.")]
    [TestCase("receipt-logs-alias", "Pinned source writes returned receipt projection: Logs.")]
    [TestCase("receipt-element", "Pinned source replaces a receipt array element.")]
    [TestCase("receipt-element-alias", "Pinned source replaces a receipt array element.")]
    [TestCase("receipt-ref-alias", "Pinned source exposes preserved identity through ref/in/out.")]
    [TestCase("receipt-element-out", "Pinned source exposes preserved identity through ref/in/out.")]
    [TestCase("receipt-array-out", "Pinned source exposes preserved identity through ref/in/out.")]
    [TestCase("receipt-array-alias", "Source-local helper 'CalculateBlooms' preservation body changed.")]
    [TestCase("receipt-external-clear", "Source-local helper 'CalculateBlooms' preservation body changed.")]
    [TestCase("count-logs-index", "Pinned source writes returned receipt projection: Index.")]
    [TestCase("receipt-root-index", "Pinned source writes returned receipt projection: Index.")]
    [TestCase("accumulate-logs", "Pinned source writes returned receipt projection: Logs.")]
    public void Helper_preservation_mutations_compile_before_named_rejection(string mutation, string diagnostic)
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, Extractor.BlockProcessorPath);
        byte[] original = File.ReadAllBytes(path);
        string source = Encoding.UTF8.GetString(original).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string rewards = "        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");";
        const string reward = "        if (_logger.IsTrace) TraceMinerReward(reward);";
        const string blooms = "        using MetricsTimer<BloomsTimeSink> _ = new();";
        (string marker, string addition) = mutation switch
        {
            "handler-rebind" => (rewards, "_systemContractHandler = _balSystemContractHandler.Value;"),
            "tracer-rebind" => (rewards, "ReceiptsTracer = new();"),
            "handler-transitive" => (reward, "_systemContractHandler = _balSystemContractHandler.Value;"),
            "tracer-transitive" => (reward, "ReceiptsTracer = new();"),
            "handler-ref-alias" => (rewards, "ref ISystemContractHandler alias = ref _systemContractHandler;"),
            "handler-ref-call" => (rewards, "System.Threading.Interlocked.Exchange(ref _systemContractHandler, _balSystemContractHandler.Value);"),
            "handler-alias" => (rewards, "ISystemContractHandler alias = _systemContractHandler;"),
            "tracer-alias" => (rewards, "BlockReceiptsTracer alias = ReceiptsTracer;"),
            "world-alias" => (rewards, "IWorldState alias = _stateProvider;"),
            "block-rebind" => (rewards, "block = block.WithReplacedHeader(block.Header.Clone());"),
            "spec-out" => (rewards, "System.Runtime.CompilerServices.Unsafe.SkipInit(out spec);"),
            "receipt-index" => (blooms, "if (receipts.Length != 0) receipts[0].Index = 123;"),
            "receipt-index-compound" => (blooms, "if (receipts.Length != 0) receipts[0].Index += 123;"),
            "receipt-index-alias" => (blooms, "if (receipts.Length != 0) { TxReceipt alias = receipts[0]; alias.Index = 123; }"),
            "receipt-logs" => (blooms, "if (receipts.Length != 0) receipts[0].Logs = [];"),
            "receipt-logs-alias" => (blooms, "if (receipts.Length != 0) { TxReceipt alias = receipts[0]; alias.Logs = []; }"),
            "receipt-element" => (blooms, "if (receipts.Length != 0) receipts[0] = new TxReceipt(receipts[0]);"),
            "receipt-element-alias" => (blooms, "TxReceipt[] alias = receipts; if (alias.Length != 0) alias[0] = new();"),
            "receipt-ref-alias" => (blooms, "if (receipts.Length != 0) { ref TxReceipt alias = ref receipts[0]; }"),
            "receipt-element-out" => (blooms, "if (receipts.Length != 0) System.Runtime.CompilerServices.Unsafe.SkipInit(out receipts[0]);"),
            "receipt-array-out" => (blooms, "System.Runtime.CompilerServices.Unsafe.SkipInit(out receipts);"),
            "receipt-array-alias" => (blooms, "TxReceipt[] alias = receipts;"),
            "receipt-external-clear" => (blooms, "Array.Clear(receipts);"),
            "count-logs-index" => ("        int count = 0;", "if (receipts.Length != 0) receipts[0].Index = 123;"),
            "receipt-root-index" => ("        using MetricsTimer<ReceiptsRootTimeSink> _ = new();", "if (receipts.Length != 0) receipts[0].Index = 123;"),
            "accumulate-logs" => ("        Bloom blockBloom = new();", "if (receipts.Length != 0) receipts[0].Logs = [];"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.That(source.Split(marker, StringSplitOptions.None), Has.Length.EqualTo(2));
        string changed = source.Replace(marker, "        " + addition + "\n" + marker, StringComparison.Ordinal);
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
    public void Coordinated_helper_body_and_digest_mutations_do_not_redefine_preservation(
        [Values("handler", "tracer", "index", "logs", "element", "ref")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        string member = mutation is "handler" or "tracer" ? "ApplyMinerRewards" : "CalculateBlooms";
        HelperPreservationIdentity helper = document.HelperPreservation.Single(helper => helper.Member == member);
        string addition = mutation switch
        {
            "handler" => "_systemContractHandler=_balSystemContractHandler.Value;",
            "tracer" => "ReceiptsTracer=new();",
            "index" => "if(receipts.Length!=0)receipts[0].Index=123;",
            "logs" => "if(receipts.Length!=0)receipts[0].Logs=[];",
            "element" => "if(receipts.Length!=0)receipts[0]=new();",
            _ => "System.Runtime.CompilerServices.Unsafe.SkipInit(outreceipts);",
        };
        int body = helper.CanonicalSyntax.IndexOf('{');
        string canonical = helper.CanonicalSyntax.Insert(body + 1, addition);
        HelperPreservationIdentity changed = helper with
        {
            CanonicalSyntax = canonical,
            SyntaxSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(),
        };
        IrDocument altered = document with
        {
            HelperPreservation = document.HelperPreservation.Select(item => item.Member == member ? changed : item).ToArray(),
        };
        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
            $"Source-local helper '{member}' preservation body changed."));
    }

    [TestCase("missing", "The complete source-local helper preservation roster is required.")]
    [TestCase("duplicate", "The exact source-local helper preservation roster changed.")]
    [TestCase("order", "The exact source-local helper preservation roster changed.")]
    [TestCase("path", "The exact source-local helper preservation roster changed.")]
    [TestCase("symbol", "The exact source-local helper preservation roster changed.")]
    public void Helper_preservation_roster_is_exact(string mutation, string diagnostic)
    {
        IrDocument document = ReadCheckedIr();
        HelperPreservationIdentity[] helpers = document.HelperPreservation;
        HelperPreservationIdentity[] changed = mutation switch
        {
            "missing" => helpers.Skip(1).ToArray(),
            "duplicate" => [helpers[0], helpers[0], .. helpers.Skip(2)],
            "order" => helpers.Reverse().ToArray(),
            "path" => [helpers[0] with { Path = Extractor.BlockProcessorStandardPath }, .. helpers.Skip(1)],
            "symbol" => [helpers[0] with { SymbolId = helpers[0].SymbolId + "Redirected" }, .. helpers.Skip(1)],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.That(() => Extractor.ValidateIrForTest(document with { HelperPreservation = changed }),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
    }

    [Test]
    public void Instrumentation_preservation_binds_all_five_implicit_sink_callbacks()
    {
        IrDocument document = ReadCheckedIr();
        Assert.That(document.Instrumentation.Sinks.Select(static sink => sink.Name), Is.EqualTo(new[]
        {
            "BloomsTimeSink", "CommitTimeSink", "ReceiptsRootTimeSink", "StateRootTimeSink", "StorageMerkleTimeSink",
        }));
        foreach (InstrumentationSinkIdentity sink in document.Instrumentation.Sinks)
        {
            Assert.That(sink.CanonicalSyntax, Does.Contain("IsEnabled=>ExecutionMetricsFlag.IsActive"));
            Assert.That(document.HelperPreservation.Single(helper => helper.SymbolId == sink.OwnerSymbol).CanonicalSyntax,
                Does.Contain(sink.ResourceCanonical));
        }
        Assert.That(() => Extractor.ValidateIrForTest(document), Throws.Nothing);
    }

    [Test]
    public void Implicit_sink_callback_mutations_compile_before_preservation_rejection(
        [Values("BloomsTimeSink", "CommitTimeSink", "ReceiptsRootTimeSink", "StateRootTimeSink", "StorageMerkleTimeSink")] string sink,
        [Values("callback-reentry", "getter-reentry", "static-constructor", "initializer-reentry", "capture", "conversion", "dispose")] string mutation)
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, Extractor.BlockProcessorPath)).Replace("\r\n", "\n", StringComparison.Ordinal);
        StructDeclarationSyntax declaration = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<StructDeclarationSyntax>().Single(type => type.Identifier.ValueText == sink);
        string original = declaration.ToString();
        const string reentry = "InstrumentationProcessor.CommitState(InstrumentationSpec);";
        string changed;
        if (mutation == "callback-reentry")
        {
            MethodDeclarationSyntax method = declaration.Members.OfType<MethodDeclarationSyntax>().Single();
            string body = method.Body?.ToString()[1..^1] ?? method.ExpressionBody!.Expression + ";";
            changed = original.Replace(method.ToString(), "public static void AddTicks(long ticks) { " + reentry + body + " }", StringComparison.Ordinal);
        }
        else if (mutation == "getter-reentry")
        {
            changed = original.Replace("public static bool IsEnabled => ExecutionMetricsFlag.IsActive;",
                "public static bool IsEnabled { get { " + reentry + " return ExecutionMetricsFlag.IsActive; } }", StringComparison.Ordinal);
        }
        else
        {
            string addition = mutation switch
            {
                "static-constructor" => "static " + sink + "() { " + reentry + " }",
                "initializer-reentry" => "private static readonly bool Initialized = Initialize(); private static bool Initialize() { " + reentry + " return true; }",
                "capture" => "private static readonly BlockProcessor CapturedProcessor = InstrumentationProcessor;",
                "conversion" => "public static implicit operator long(" + sink + " value) { " + reentry + " return 0; }",
                "dispose" => "public void Dispose() { " + reentry + " }",
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };
            changed = original.Insert(original.LastIndexOf('}'), addition);
        }
        Assert.That(changed, Is.Not.EqualTo(original));
        string mutated = source[..declaration.SpanStart] + changed + source[declaration.Span.End..];
        mutated = CaptureInstrumentationReceiver(mutated);
        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal) { [Extractor.BlockProcessorPath] = Encoding.UTF8.GetBytes(mutated) };
        Extractor.RequireCompilationForTest(root, overrides);
        using TemporaryDirectory output = new();
        Assert.That(() => Extractor.ExtractForTest(root, output.Path, overrides),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo($"Instrumentation sink '{sink}' preservation declaration changed."));
    }

    [Test]
    public void Explicit_generic_sink_activations_compile_before_callback_rejection(
        [Values("default-dispose", "constructor", "constructed-dispose", "default-local", "target-typed-constructor", "known-sink", "nested-generic", "tuple-generic")] string mutation)
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, Extractor.BlockProcessorPath)).Replace("\r\n", "\n", StringComparison.Ordinal);
        string extra = mutation switch
        {
            "default-dispose" => "default(MetricsTimer<ExtraSink>).Dispose();",
            "constructor" => "new MetricsTimer<ExtraSink>();",
            "constructed-dispose" => "new MetricsTimer<ExtraSink>().Dispose();",
            "default-local" => "MetricsTimer<ExtraSink> timer = default; timer.Dispose();",
            "target-typed-constructor" => "MetricsTimer<ExtraSink> timer = new();",
            "known-sink" => "default(MetricsTimer<BloomsTimeSink>).Dispose();",
            "tuple-generic" => "new System.Collections.Generic.List<(ExtraSink Sink, int Value)>();",
            _ => "new System.Collections.Generic.List<System.Collections.Generic.List<ExtraSink>>();",
        };
        const string marker = "        TransactionsExecuted?.Invoke();";
        Assert.That(source.Split(marker, StringSplitOptions.None), Has.Length.EqualTo(2));
        string changed = AddUnboundInstrumentationSink(source.Replace(marker, marker + "\n        " + extra, StringComparison.Ordinal));
        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal) { [Extractor.BlockProcessorPath] = Encoding.UTF8.GetBytes(changed) };
        Extractor.RequireCompilationForTest(root, overrides);
        using TemporaryDirectory output = new();
        Assert.That(() => Extractor.ExtractForTest(root, output.Path, overrides), Throws.TypeOf<ExtractionException>()
            .With.Message.Contains("unbound source-bearing generic callback edge"));
    }

    [Test]
    public void Whole_tree_timer_activations_compile_before_global_roster_rejection([Values("default-dispose", "constructor")] string mutation)
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, Extractor.BlockProcessorPath)).Replace("\r\n", "\n", StringComparison.Ordinal);
        string extra = mutation == "default-dispose" ? "default(MetricsTimer<ExtraSink>).Dispose();" : "new MetricsTimer<ExtraSink>();";
        string changed = AddUnboundInstrumentationSink(source.Replace("    private void CommitState(IReleaseSpec spec)",
            "    private static void ExtraTimerActivation() { " + extra + " }\n\n    private void CommitState(IReleaseSpec spec)", StringComparison.Ordinal));
        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal) { [Extractor.BlockProcessorPath] = Encoding.UTF8.GetBytes(changed) };
        Extractor.RequireCompilationForTest(root, overrides);
        using TemporaryDirectory output = new();
        Assert.That(() => Extractor.ExtractForTest(root, output.Path, overrides), Throws.TypeOf<ExtractionException>()
            .With.Message.EqualTo("An explicit timer construction, default, invocation, or receiver is outside the exact instrumentation activation roster."));
    }

    private static string AddUnboundInstrumentationSink(string source) => CaptureInstrumentationReceiver(
        source.Replace("    private void CommitState(IReleaseSpec spec)", """
            private readonly struct ExtraSink : IMetricSink
            {
                public static void AddTicks(long ticks) => InstrumentationProcessor.CommitState(InstrumentationSpec);
                public static bool IsEnabled { get { InstrumentationProcessor.CommitState(InstrumentationSpec); return true; } }
            }

            private void CommitState(IReleaseSpec spec)
        """, StringComparison.Ordinal));

    private static string CaptureInstrumentationReceiver(string source) => source.Replace("    private void CommitState(IReleaseSpec spec)",
        "    private static BlockProcessor InstrumentationProcessor = null!;\n    private static IReleaseSpec InstrumentationSpec = null!;\n\n    private void CommitState(IReleaseSpec spec)", StringComparison.Ordinal)
        .Replace("            receipts = ProcessBlock(block, blockTracer, options, spec, token);",
            "            InstrumentationProcessor = this;\n            InstrumentationSpec = spec;\n            receipts = ProcessBlock(block, blockTracer, options, spec, token);", StringComparison.Ordinal);

    [Test]
    public void Coordinated_timer_activation_artifacts_cannot_admit_extra_routes(
        [Values("extra-default-dispose", "extra-constructor", "coordinated-default-route", "coordinated-owner", "omitted", "duplicate")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        InstrumentationBoundaryIdentity boundary = document.Instrumentation;
        InstrumentationActivationIdentity activation = boundary.Activations[0];
        if (mutation is "extra-default-dispose" or "extra-constructor")
        {
            activation = activation with
            {
                OperationKind = mutation == "extra-default-dispose" ? "Invocation" : "ObjectCreation",
                ResourceCanonical = mutation == "extra-default-dispose" ? "default(MetricsTimer<ExtraSink>).Dispose()" : "newMetricsTimer<ExtraSink>()",
                WrapperType = activation.WrapperType.Replace("BloomsTimeSink", "ExtraSink", StringComparison.Ordinal),
                SinkTypeSymbol = activation.SinkTypeSymbol.Replace("BloomsTimeSink", "ExtraSink", StringComparison.Ordinal),
                TargetSymbol = mutation == "extra-default-dispose" ? activation.TargetSymbol.Replace("..ctor()", ".Dispose()", StringComparison.Ordinal) : activation.TargetSymbol,
            };
            boundary = boundary with { Activations = [.. boundary.Activations, activation] };
        }
        else if (mutation == "coordinated-default-route")
        {
            string canonical = "default(MetricsTimer<BloomsTimeSink>).Dispose()";
            boundary = boundary with
            {
                Sinks = [boundary.Sinks[0] with { ResourceCanonical = canonical }, .. boundary.Sinks.Skip(1)],
                Activations = [activation with { ResourceCanonical = canonical, OperationKind = "Invocation" }, .. boundary.Activations.Skip(1)],
            };
        }
        else if (mutation == "coordinated-owner")
        {
            boundary = boundary with
            {
                Sinks = [boundary.Sinks[0] with { OwnerSymbol = boundary.Sinks[1].OwnerSymbol }, .. boundary.Sinks.Skip(1)],
                Activations = [activation with { OwnerSymbol = boundary.Sinks[1].OwnerSymbol }, .. boundary.Activations.Skip(1)],
            };
        }
        else
        {
            boundary = boundary with
            {
                Activations = mutation == "omitted" ? boundary.Activations.Skip(1).ToArray() : [activation, activation, .. boundary.Activations.Skip(2)],
            };
        }
        string diagnostic = mutation.StartsWith("coordinated-", StringComparison.Ordinal)
            ? "The exact instrumentation sink/callback/site binding roster changed."
            : "The complete exact instrumentation activation roster changed.";
        Assert.That(() => Extractor.ValidateIrForTest(document with { Instrumentation = boundary }),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
    }

    [Test]
    public void Coordinated_sink_declaration_and_digest_mutations_cannot_redefine_preservation(
        [Values("callback", "getter", "initializer", "capture", "conversion", "dispose")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        InstrumentationSinkIdentity sink = document.Instrumentation.Sinks[0];
        string addition = mutation switch
        {
            "callback" => "private static void Reenter()=>InstrumentationProcessor.CommitState(InstrumentationSpec);",
            "getter" => "private static bool Captured=>InstrumentationProcessor!=null;",
            "initializer" => "static BloomsTimeSink(){InstrumentationProcessor.CommitState(InstrumentationSpec);}",
            "capture" => "private static BlockProcessor Captured=InstrumentationProcessor;",
            "conversion" => "public static implicit operator long(BloomsTimeSink value)=>0;",
            _ => "public void Dispose(){InstrumentationProcessor.CommitState(InstrumentationSpec);}",
        };
        string canonical = sink.CanonicalSyntax.Insert(sink.CanonicalSyntax.Length - 1, addition);
        InstrumentationSinkIdentity changed = sink with
        {
            CanonicalSyntax = canonical,
            SyntaxSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(),
        };
        IrDocument altered = document with
        {
            Instrumentation = document.Instrumentation with { Sinks = [changed, .. document.Instrumentation.Sinks.Skip(1)] },
        };
        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
            "Instrumentation sink 'BloomsTimeSink' preservation declaration changed."));
    }

    [Test]
    public void Instrumentation_roster_and_bindings_are_exact(
        [Values("missing", "duplicate", "order", "owner", "getter", "callback", "constructor", "dispose", "wrapper")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        InstrumentationSinkIdentity[] sinks = document.Instrumentation.Sinks;
        InstrumentationSinkIdentity[] changed = mutation switch
        {
            "missing" => sinks.Skip(1).ToArray(),
            "duplicate" => [sinks[0], sinks[0], .. sinks.Skip(2)],
            "order" => sinks.Reverse().ToArray(),
            "owner" => [sinks[0] with { OwnerSymbol = sinks[1].OwnerSymbol }, .. sinks.Skip(1)],
            "getter" => [sinks[0] with { EnabledValueSymbol = sinks[0].EnabledPropertySymbol }, .. sinks.Skip(1)],
            "callback" => [sinks[0] with { AddTicksCalls = [sinks[1].AddTicksSymbol] }, .. sinks.Skip(1)],
            "constructor" => [sinks[0] with { ConstructorSymbol = sinks[1].AddTicksSymbol }, .. sinks.Skip(1)],
            "dispose" => [sinks[0] with { DisposeSymbol = sinks[1].AddTicksSymbol }, .. sinks.Skip(1)],
            _ => sinks,
        };
        InstrumentationBoundaryIdentity boundary = document.Instrumentation with { Sinks = changed };
        if (mutation == "wrapper") boundary = boundary with { Wrapper = boundary.Wrapper with { Sha256 = new('0', 64) } };
        string diagnostic = mutation is "missing" or "wrapper"
            ? "The complete pinned instrumentation wrapper and sink roster is required."
            : "The exact instrumentation sink/callback/site binding roster changed.";
        Assert.That(() => Extractor.ValidateIrForTest(document with { Instrumentation = boundary }),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
    }

    [Test]
    public void Complete_entry_preservation_includes_every_explicit_and_implicit_callback_site()
    {
        IrDocument document = ReadCheckedIr();
        Assert.That(document.EntryPreservation.CanonicalSyntax, Does.StartWith("{BlockBodybody=block.Body;"));
        Assert.That(document.EntryPreservation.CanonicalSyntax, Does.EndWith("header.Hash=header.CalculateHash();returnreceipts;}"));
        Assert.That(document.EntryPreservation.CanonicalSyntax, Does.Not.Contain("{...}"));
        Assert.That(() => Extractor.ValidateIrForTest(document), Throws.Nothing);
    }

    [Test]
    public void Metadata_comparer_and_delegate_callbacks_compile_before_rejection(
        [Values("array-comparer", "comparer-alias", "nongeneric-comparer", "constructor-comparer", "method-group", "delegate-alias")] string mutation)
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, Extractor.BlockProcessorPath)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal)
        {
            [Extractor.BlockProcessorPath] = Encoding.UTF8.GetBytes(AddMetadataCallback(source, mutation)),
        };
        Extractor.RequireCompilationForTest(root, overrides);
        using TemporaryDirectory output = new();
        string diagnostic = mutation is "method-group" or "delegate-alias"
            ? "un-audited delegate creation"
            : "ProcessBlock complete executable body/callback roster changed.";
        Assert.That(() => Extractor.ExtractForTest(root, output.Path, overrides),
            Throws.TypeOf<ExtractionException>().With.Message.Contains(diagnostic));
    }

    private static string AddMetadataCallback(string source, string mutation)
    {
        string callback = mutation switch
        {
            "array-comparer" => "Array.Sort(new[] { 1, 2 }, this);",
            "comparer-alias" => "System.Collections.Generic.IComparer<int> comparer = this; Array.Sort(new[] { 1, 2 }, comparer);",
            "nongeneric-comparer" => "Array.Sort((Array)new[] { 1, 2 }, (System.Collections.IComparer)this);",
            "constructor-comparer" => "new System.Collections.Generic.SortedSet<int>(new[] { 1, 2 }, this);",
            "method-group" => "Array.Sort<int>(new[] { 1, 2 }, Compare);",
            "delegate-alias" => "Comparison<int> comparison = Compare; Array.Sort(new[] { 1, 2 }, comparison);",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        source = source.Replace("    : IBlockProcessor\n", "    : IBlockProcessor, System.Collections.Generic.IComparer<int>, System.Collections.IComparer\n", StringComparison.Ordinal)
            .Replace("        TransactionsExecuted?.Invoke();", "        TransactionsExecuted?.Invoke();\n        " + callback, StringComparison.Ordinal)
            .Replace("    private void CommitState(IReleaseSpec spec)", """
                public int Compare(int x, int y) { InstrumentationProcessor.CommitState(InstrumentationSpec); return 0; }
                int System.Collections.IComparer.Compare(object? x, object? y) { InstrumentationProcessor.CommitState(InstrumentationSpec); return 0; }

                private void CommitState(IReleaseSpec spec)
            """, StringComparison.Ordinal);
        return CaptureInstrumentationReceiver(source);
    }

    [Test]
    public void Coordinated_entry_body_and_digest_cannot_admit_metadata_callbacks(
        [Values("call", "constructor", "interpolation")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        string addition = mutation switch
        {
            "call" => "Array.Sort(new[]{1,2},this);",
            "constructor" => "newSystem.Collections.Generic.SortedSet<int>(new[]{1,2},this);",
            _ => "stringformatted=$\"{this}\";",
        };
        string canonical = document.EntryPreservation.CanonicalSyntax.Insert(1, addition);
        HelperPreservationIdentity changed = document.EntryPreservation with
        {
            CanonicalSyntax = canonical,
            SyntaxSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(),
        };
        Assert.That(() => Extractor.ValidateIrForTest(document with { EntryPreservation = changed }),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("ProcessBlock complete executable body/callback roster changed."));
    }

    [Test]
    public void Checked_artifacts_rederive_live_admission_after_coordinated_source_pin_and_artifact_mutations(
        [Values("entry-callback", "helper-body", "sink-body", "timer-activation", "source-offset")] string mutation)
    {
        string root = FindRepoRoot();
        IrDocument document = ReadCheckedIr();
        string source = File.ReadAllText(Path.Combine(root, Extractor.BlockProcessorPath)).Replace("\r\n", "\n", StringComparison.Ordinal);
        string changed = mutation switch
        {
            "entry-callback" => AddMetadataCallback(source, "array-comparer"),
            "helper-body" => source.Replace("        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                "        int extraLocal = 0;\n        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");", StringComparison.Ordinal),
            "sink-body" => source.Replace("public static bool IsEnabled => ExecutionMetricsFlag.IsActive;",
                "public static bool IsEnabled => ExecutionMetricsFlag.IsActive && true;", StringComparison.Ordinal),
            "timer-activation" => AddUnboundInstrumentationSink(source.Replace("    private void CommitState(IReleaseSpec spec)",
                "    private static void ExtraTimerActivation() { default(MetricsTimer<ExtraSink>).Dispose(); }\n\n    private void CommitState(IReleaseSpec spec)", StringComparison.Ordinal)),
            _ => "\n" + source,
        };
        Assert.That(changed, Is.Not.EqualTo(source));
        byte[] changedBytes = Encoding.UTF8.GetBytes(changed);
        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal) { [Extractor.BlockProcessorPath] = changedBytes };
        Extractor.RequireCompilationForTest(root, overrides);
        string hash = Convert.ToHexString(SHA256.HashData(changedBytes)).ToLowerInvariant();
        string canonical = string.Concat(CSharpSyntaxTree.ParseText(changed).GetRoot().DescendantTokens().Select(static token => token.Text));
        string syntaxHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        SourcePinDocument pins = new(1, document.Sources.Select(identity => new SourcePin(identity.Path, identity.Role,
            identity.Path == Extractor.BlockProcessorPath ? hash : identity.Sha256)).ToArray());
        using TemporaryDirectory output = new();
        string originalDirectory = Path.Combine(root, Extractor.DefaultOutputPath);
        JsonObject ir = JsonNode.Parse(File.ReadAllBytes(Path.Combine(originalDirectory, Extractor.ArtifactName + ".ir.json")))!.AsObject();
        JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(originalDirectory, Extractor.ArtifactName + ".source-manifest.json")))!.AsObject();
        ir["sources"]![0]!["sha256"] = hash;
        ir["sources"]![0]!["syntaxSha256"] = syntaxHash;
        byte[] irBytes = Encoding.UTF8.GetBytes(ir.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        manifest["sources"] = ir["sources"]!.DeepClone();
        manifest["irSha256"] = Convert.ToHexString(SHA256.HashData(irBytes)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(output.Path, Extractor.ArtifactName + ".ir.json"), irBytes);
        File.WriteAllText(Path.Combine(output.Path, Extractor.ArtifactName + ".source-manifest.json"), manifest.ToJsonString());
        File.Copy(Path.Combine(originalDirectory, Extractor.ArtifactName + ".lean"), Path.Combine(output.Path, Extractor.ArtifactName + ".lean"));
        string diagnostic = mutation switch
        {
            "entry-callback" => "ProcessBlock complete executable body/callback roster changed.",
            "helper-body" => "Source-local helper 'ApplyMinerRewards' preservation body changed.",
            "sink-body" => "Instrumentation sink 'BloomsTimeSink' preservation declaration changed.",
            "timer-activation" => "An explicit timer construction, default, invocation, or receiver is outside the exact instrumentation activation roster.",
            _ => "Checked-in post-transaction finalization IR differs from live source admission.",
        };
        Assert.That(() => Extractor.ValidateCheckedInWithSourceOverridesForTest(root, output.Path, overrides, pins),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
    }
}
