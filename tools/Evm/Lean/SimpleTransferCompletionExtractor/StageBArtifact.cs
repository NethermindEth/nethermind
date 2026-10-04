// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal sealed record StageBArtifactDocument(int SchemaVersion, string ArtifactKind, StageBPrefixProgram Program);
internal sealed record StageBArtifactFile(string Path, string Sha256);
internal sealed record StageBArtifactManifest(int SchemaVersion, string ArtifactKind, string PrefixIntegrity,
    StageBArtifactFile Ir, StageBArtifactFile Lean, StageBArtifactFile Syntax);
internal sealed record StageBArtifactResult(string IrPath, string LeanPath, string ManifestPath,
    string IrSha256, string LeanSha256, string ManifestSha256);

internal static class StageBArtifact
{
    internal const int SchemaVersion = 1;
    internal const string ArtifactKind = "stage-b-prefix-program-data";
    internal const string ExpectedPrefixIntegrity = "7e53ced6f9a6e008ab21357359cd94c7af660977807b9c2f7a298daad36f4e7a";
    internal const string IrFileName = "PrefixProgram.ir.json";
    internal const string LeanFileName = "PrefixProgram.lean";
    internal const string ManifestFileName = "PrefixProgram.source-manifest.json";
    internal const string SyntaxRelativePath = "tools/Evm/Lean/SimpleTransferCompletionExtractor/StageB/Syntax.lean";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    internal static StageBArtifactResult Extract(string repoRoot, string outputDirectory)
    {
        StageBPrefixProgram program = StageBPrefixCompiler.Compile(StageBLowering.Build(repoRoot));
        return Publish(repoRoot, outputDirectory, program);
    }

    internal static void ValidateExisting(string repoRoot, string outputDirectory)
    {
        byte[] ir = ReadRequired(Path.Combine(outputDirectory, IrFileName));
        byte[] lean = ReadRequired(Path.Combine(outputDirectory, LeanFileName));
        byte[] manifest = ReadRequired(Path.Combine(outputDirectory, ManifestFileName));
        byte[] syntax = ReadRequired(Path.Combine(repoRoot, SyntaxRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        ValidateBundle(repoRoot, ir, lean, manifest, syntax);
    }

    internal static void ValidateBundle(string repoRoot, byte[] ir, byte[] lean, byte[] manifest, byte[] syntax)
    {
        StageBArtifactDocument document = ParseAndValidate(ir);
        StageBArtifactManifest metadata = ParseManifestAndValidate(manifest);
        if (metadata.PrefixIntegrity != document.Program.Integrity || metadata.Ir.Path != IrFileName ||
            metadata.Lean.Path != LeanFileName || metadata.Syntax.Path != SyntaxRelativePath ||
            metadata.Ir.Sha256 != Sha256(ir) || metadata.Lean.Sha256 != Sha256(lean) || metadata.Syntax.Sha256 != Sha256(syntax))
            throw new ExtractionException("Stage-B artifact bundle hashes or paths changed.");
        byte[] currentSyntax = ReadRequired(Path.Combine(repoRoot, SyntaxRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!syntax.AsSpan().SequenceEqual(currentSyntax))
            throw new ExtractionException("Stage-B syntax artifact drifted.");

        string scratch = Path.Combine(Path.GetTempPath(), "stage-b-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            StageBArtifactResult fresh = Extract(repoRoot, scratch);
            Compare(IrFileName, ir, fresh.IrPath);
            Compare(LeanFileName, lean, fresh.LeanPath);
            Compare(ManifestFileName, manifest, fresh.ManifestPath);
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }

        static void Compare(string name, byte[] supplied, string freshPath)
        {
            if (!supplied.AsSpan().SequenceEqual(File.ReadAllBytes(freshPath)))
                throw new ExtractionException("Stage-B generated artifact drifted: " + name + ".");
        }
    }

    internal static StageBArtifactResult Publish(string repoRoot, string outputDirectory, StageBPrefixProgram program)
    {
        ValidateProgram(program);
        Directory.CreateDirectory(outputDirectory);
        StageBArtifactDocument document = new(SchemaVersion, ArtifactKind, program);
        byte[] ir = Terminated(JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
        string irSha = Sha256(ir);
        byte[] lean = Encoding.UTF8.GetBytes(StageBLeanDataEmitter.Emit(program, irSha));
        string leanSha = Sha256(lean);
        string syntaxPath = Path.Combine(repoRoot, SyntaxRelativePath.Replace('/', Path.DirectorySeparatorChar));
        byte[] syntax = File.ReadAllBytes(syntaxPath);
        StageBArtifactManifest manifest = new(SchemaVersion, ArtifactKind, program.Integrity,
            new(IrFileName, irSha), new(LeanFileName, leanSha), new(SyntaxRelativePath, Sha256(syntax)));
        byte[] manifestBytes = Terminated(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        string irPath = Path.Combine(outputDirectory, IrFileName);
        string leanPath = Path.Combine(outputDirectory, LeanFileName);
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        WriteAtomic(irPath, ir);
        WriteAtomic(leanPath, lean);
        WriteAtomic(manifestPath, manifestBytes);
        return new(irPath, leanPath, manifestPath, irSha, leanSha, Sha256(manifestBytes));
    }

    internal static StageBArtifactDocument ParseAndValidate(byte[] serialized)
    {
        RejectDuplicateProperties(serialized);
        StageBArtifactDocument document;
        try
        {
            document = JsonSerializer.Deserialize<StageBArtifactDocument>(serialized, JsonOptions)
                ?? throw new ExtractionException("Stage-B artifact is null.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException("Stage-B artifact schema changed: " + exception.Message);
        }
        if (document.SchemaVersion != SchemaVersion || document.ArtifactKind != ArtifactKind || document.Program is null)
            throw new ExtractionException("Stage-B artifact header changed.");
        ValidateProgram(document.Program);
        byte[] canonical = Terminated(JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
        if (!serialized.AsSpan().SequenceEqual(canonical)) throw new ExtractionException("Stage-B artifact is not canonical.");
        return document;
    }

    private static StageBArtifactManifest ParseManifestAndValidate(byte[] serialized)
    {
        RejectDuplicateProperties(serialized);
        StageBArtifactManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<StageBArtifactManifest>(serialized, JsonOptions)
                ?? throw new ExtractionException("Stage-B artifact manifest is null.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException("Stage-B artifact manifest schema changed: " + exception.Message);
        }
        if (manifest.SchemaVersion != SchemaVersion || manifest.ArtifactKind != ArtifactKind || manifest.PrefixIntegrity != ExpectedPrefixIntegrity ||
            manifest.Ir is null || manifest.Lean is null || manifest.Syntax is null)
            throw new ExtractionException("Stage-B artifact manifest header changed.");
        byte[] canonical = Terminated(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        if (!serialized.AsSpan().SequenceEqual(canonical)) throw new ExtractionException("Stage-B artifact manifest is not canonical.");
        return manifest;
    }

    internal static void ValidateProgram(StageBPrefixProgram program)
    {
        if (program.Integrity != ExpectedPrefixIntegrity || StageBPrefixIntegrity.Compute(program) != ExpectedPrefixIntegrity)
            throw new ExtractionException(
                $"Stage-B serialized prefix is not the independently pinned program (expected {ExpectedPrefixIntegrity}, got {program.Integrity}).");
        if (program.Functions is null || program.Initializers is null || program.Members is null || program.Types is null ||
            program.CalleeBeforeCaller is null || program.CompilerSources is null || program.CompilerReferences is null ||
            program.ExternalPremises is null || program.AcceptedStages is null || program.Refund is null)
            throw new ExtractionException("Stage-B serialized prefix omitted a required program field.");
        if (program.Functions.Length == 0 || program.FuelBound <= 0 ||
            program.Functions.Select(static function => function.Signature.Symbol).Distinct(StringComparer.Ordinal).Count() != program.Functions.Length ||
            !program.Functions.Any(function => function.Signature.Symbol == program.Entry))
            throw new ExtractionException("Stage-B serialized prefix structure changed.");
        foreach (StageBPrefixFunction function in program.Functions)
        {
            if (function.Signature is null || function.Bindings is null || function.Captures is null || function.Regions is null ||
                function.EntryFacts is null || function.Blocks is null || function.Calls is null || function.CaptureModes is null ||
                function.BlockBounds is null || function.Blocks.Length == 0)
                throw new ExtractionException("Stage-B serialized function omitted a required field.");
            foreach (StageBPrefixBlock block in function.Blocks)
            {
                if (block.Operations is null) throw new ExtractionException("Stage-B serialized block omitted operations.");
                foreach (StageBPrefixNode node in block.BranchValue is null ? block.Operations : [.. block.Operations, block.BranchValue])
                    ValidateNode(node);
            }
        }
    }

    private static void ValidateNode(StageBPrefixNode node)
    {
        if (node.Binding is null || node.AdditionalTargets is null || node.Children is null)
            throw new ExtractionException("Stage-B serialized node omitted a required field.");
        if (node.Call is { } call && (call.Target is null || call.Arguments is null || call.Target.Member is null))
            throw new ExtractionException("Stage-B serialized call omitted a required field.");
        foreach (StageBPrefixNode child in node.Children) ValidateNode(child);
    }

    private static void RejectDuplicateProperties(byte[] serialized)
    {
        using JsonDocument document = JsonDocument.Parse(serialized, new JsonDocumentOptions { MaxDepth = 2048 });
        Visit(document.RootElement);
        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new ExtractionException("Stage-B artifact contains a duplicate property.");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in element.EnumerateArray()) Visit(item);
        }
    }

    private static byte[] Terminated(byte[] value) => value.Length != 0 && value[^1] == (byte)'\n' ? value : [.. value, (byte)'\n'];
    private static byte[] ReadRequired(string path) => File.Exists(path)
        ? File.ReadAllBytes(path)
        : throw new ExtractionException("Stage-B artifact is missing: " + Path.GetFileName(path) + ".");

    private static void WriteAtomic(string path, byte[] value)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, value);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static string Sha256(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
}

internal static class StageBLeanDataEmitter
{
    internal static string FinalizeEntry(StageBMember signature, StageBBlock entry) =>
        "-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited\n" +
        "-- SPDX-License-Identifier: LGPL-3.0-only\n\n" +
        "import SimpleTransferCompletionExtractor.StageB.Syntax\n\n" +
        "namespace SimpleTransferCompletionExtractor.StageB.Finalize.Generated\n\n" +
        "def signature : Member := " + Member(signature) + "\n\n" +
        "def sourceEntry : SourceBlock := " + SourceBlock(entry) + "\n\n" +
        "def entryBlock : Nat := 0\ndef entryOperation : Nat := 0\n\n" +
        "end SimpleTransferCompletionExtractor.StageB.Finalize.Generated\n";

    internal static string Emit(StageBPrefixProgram program, string irSha256)
    {
        StringBuilder builder = new();
        builder.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        builder.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        builder.AppendLine("-- Generated Stage-B program data. Do not edit.");
        builder.AppendLine();
        builder.AppendLine("import SimpleTransferCompletionExtractor.StageB.Syntax");
        builder.AppendLine();
        builder.AppendLine("set_option maxHeartbeats 2000000");
        builder.AppendLine();
        builder.AppendLine("namespace SimpleTransferCompletionExtractor.StageB.Generated");
        builder.AppendLine();
        builder.Append("def sourceIrSha256 : String := ").Append(Quote(irSha256)).AppendLine();
        builder.Append("def compilerSourcesData : List SimpleTransferCompletionExtractor.StageB.SourcePin := ").Append(List(program.CompilerSources, Source)).AppendLine();
        builder.Append("def compilerReferencesData : List SimpleTransferCompletionExtractor.StageB.ReferencePin := ").Append(List(program.CompilerReferences, Reference)).AppendLine();
        builder.Append("def acceptedStagesData : List SimpleTransferCompletionExtractor.StageB.StagePin := ").Append(List(program.AcceptedStages, Stage)).AppendLine();
        builder.Append("def membersData : List SimpleTransferCompletionExtractor.StageB.Member := ").Append(List(program.Members, Member)).AppendLine();
        builder.Append("def typesData : List SimpleTransferCompletionExtractor.StageB.TypeInfo := ").Append(List(program.Types, Type)).AppendLine();
        for (int index = 0; index < program.Functions.Length; index++)
            builder.Append("def function").Append(index).Append(" : SimpleTransferCompletionExtractor.StageB.Function := ").Append(Function(program.Functions[index])).AppendLine();
        builder.Append("def refundCallData : SimpleTransferCompletionExtractor.StageB.Call := ").Append(Call(program.Refund.Call)).AppendLine();
        for (int index = 0; index < program.Refund.OrderedOperands.Length; index++)
            builder.Append("def refundOperand").Append(index).Append(" : SimpleTransferCompletionExtractor.StageB.Node := ").Append(Node(program.Refund.OrderedOperands[index])).AppendLine();
        builder.Append("def refundPendingOperation : SimpleTransferCompletionExtractor.StageB.SourceTerm := ").Append(SourceTerm(program.Refund.Continuation.PendingOperation)).AppendLine();
        for (int index = 0; index < program.Refund.Continuation.SourceBlocks.Length; index++)
            builder.Append("def refundSourceBlock").Append(index).Append(" : SimpleTransferCompletionExtractor.StageB.SourceBlock := ").Append(SourceBlock(program.Refund.Continuation.SourceBlocks[index])).AppendLine();
        builder.Append("def feeHelperSignature : SimpleTransferCompletionExtractor.StageB.Member := ").Append(Member(program.PostRefundFeeHelper.Signature)).AppendLine();
        builder.Append("def feeHelperEntryBlock : Nat := ").Append(program.PostRefundFeeHelper.Entry.Block).AppendLine();
        builder.Append("def feeHelperEntryOperation : Nat := ").Append(program.PostRefundFeeHelper.Entry.Operation).AppendLine();
        for (int index = 0; index < program.PostRefundFeeHelper.SourceBlocks.Length; index++)
            builder.Append("def feeHelperSourceBlock").Append(index).Append(" : SimpleTransferCompletionExtractor.StageB.SourceBlock := ").Append(SourceBlock(program.PostRefundFeeHelper.SourceBlocks[index])).AppendLine();
        builder.Append("def refundData : SimpleTransferCompletionExtractor.StageB.RefundSuspension := { call := refundCallData, orderedOperands := [")
            .Append(string.Join(", ", Enumerable.Range(0, program.Refund.OrderedOperands.Length).Select(static index => "refundOperand" + index)))
            .Append("], continuation := { functionSymbol := ").Append(Quote(program.Refund.Continuation.Function))
            .Append(", block := ").Append(program.Refund.Continuation.Block).Append(", operation := ").Append(program.Refund.Continuation.Operation)
            .Append(", callPath := ").Append(IntList(program.Refund.Continuation.CallPath)).Append(", pendingOperation := refundPendingOperation, sourceBlocks := [")
            .Append(string.Join(", ", Enumerable.Range(0, program.Refund.Continuation.SourceBlocks.Length).Select(static index => "refundSourceBlock" + index)))
            .AppendLine("] } }");
        builder.AppendLine("def program : SimpleTransferCompletionExtractor.StageB.Program :=");
        builder.Append("  { schemaVersion := ").Append(StageBArtifact.SchemaVersion).AppendLine();
        builder.Append("    entry := ").Append(Quote(program.Entry)).AppendLine();
        builder.Append("    policyType := ").Append(Quote(program.PolicyType)).AppendLine();
        builder.Append("    scope := ").Append(Quote(program.Scope)).AppendLine();
        builder.Append("    prefixIntegrity := ").Append(Quote(program.Integrity)).AppendLine();
        builder.AppendLine("    compilerSources := compilerSourcesData");
        builder.AppendLine("    compilerReferences := compilerReferencesData");
        builder.Append("    externalPremises := ").Append(StringList(program.ExternalPremises)).AppendLine();
        builder.AppendLine("    acceptedStages := acceptedStagesData");
        builder.Append("    functions := [").Append(string.Join(", ", Enumerable.Range(0, program.Functions.Length).Select(static index => "function" + index))).AppendLine("]");
        builder.Append("    initializers := ").Append(StringList(program.Initializers)).AppendLine();
        builder.AppendLine("    members := membersData");
        builder.AppendLine("    types := typesData");
        builder.AppendLine("    refund := refundData");
        builder.Append("    calleeBeforeCaller := ").Append(StringList(program.CalleeBeforeCaller)).AppendLine();
        builder.Append("    fuelBound := ").Append(program.FuelBound).AppendLine(" }");
        builder.AppendLine();
        builder.AppendLine("end SimpleTransferCompletionExtractor.StageB.Generated");
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Source(SourceIdentity value) =>
        $"{{ path := {Quote(value.Path)}, role := {Quote(value.Role)}, sha256 := {Quote(value.Sha256)} }}";
    private static string Reference(ReferenceIdentity value) =>
        $"{{ path := {Quote(value.Path)}, assemblyName := {Quote(value.AssemblyName)}, sha256 := {Quote(value.Sha256)}, mvid := {Quote(value.Mvid)}, selected := {Bool(value.Selected)} }}";
    private static string Stage(StageBStage value) =>
        $"{{ name := {Quote(value.Name)}, theoremName := {Quote(value.Theorem)}, entryPoints := {StringList(value.EntryPoints)}, sourceClosure := {List(value.SourceClosure, Dependency)}, closureSha256 := {Quote(StageBPrefixIntegrity.StageClosure(value))} }}";
    private static string Dependency(StageBStageDependency value) =>
        $"{{ symbol := {Quote(value.Symbol)}, path := {Quote(value.Path)}, sourceSha256 := {Quote(value.SourceSha256)} }}";

    private static string Function(StageBPrefixFunction value) =>
        $"{{ signature := {Member(value.Signature)}, entryBlock := {value.Entry.Block}, entryOperationProvenance := {value.Entry.Operation}, bindings := {List(value.Bindings, BindingTuple)}, captures := {List(value.Captures, Capture)}, regions := {List(value.Regions, Region)}, entryFacts := {StringList(value.EntryFacts)}, captureModes := {List(value.CaptureModes, CaptureMode)}, blocks := {List(value.Blocks, Block)}, calls := {StringList(value.Calls)}, blockBounds := {List(value.BlockBounds, BlockBound)}, fuelBound := {value.FuelBound}, mayReturn := {Bool(value.MayReturn)} }}";
    private static string BindingTuple(StageBBinding value) =>
        $"({Quote(value.Symbol)}, {Quote(value.Type)}, {Ref(value.RefKind)}, {value.Region})";
    private static string Capture(StageBCapture value) =>
        $"{{ id := {value.Id}, region := {value.Region}, typeName := {Quote(value.Type)} }}";
    private static string Region(StageBRegion value) =>
        $"{{ id := {value.Id}, parent := {value.Parent}, kind := {Quote(value.Kind)}, firstBlock := {value.FirstBlock}, lastBlock := {value.LastBlock}, locals := {StringList(value.Locals)}, captures := {IntList(value.Captures)}, exceptionType := {Quote(value.ExceptionType)} }}";
    private static string CaptureMode(StageBPrefixCaptureMode value) => $"({value.Capture}, {Mode(value.Mode)})";
    private static string BlockBound(StageBPrefixBlockBound value) => $"{{ block := {value.Block}, fuel := {value.Fuel} }}";
    private static string Block(StageBPrefixBlock value) =>
        $"{{ ordinal := {value.Ordinal}, operations := {List(value.Operations, Node)}, branchValue := {Option(value.BranchValue, Node)}, condition := {Condition(value.ConditionKind)}, exit := {Exit(value.Exit)}, fallThrough := {Option(value.FallThrough, Edge)}, conditional := {Option(value.Conditional, Edge)} }}";
    private static string Edge(StageBEdge value) =>
        $"{{ destination := {value.Destination}, semantics := {Quote(value.Semantics)}, leavingRegions := {IntList(value.LeavingRegions)}, enteringRegions := {IntList(value.EnteringRegions)}, finallyRegions := {IntList(value.FinallyRegions)} }}";

    private static string Node(StageBPrefixNode value) =>
        $".mk {Operation(value.Kind)} {Quote(value.Type)} {Quote(value.Symbol)} {Quote(value.Operator)} {Quote(value.Constant)} {Quote(value.Conversion)} ({Literal(value.Constant)}) {Primitive(value)} {Bool(value.Implicit)} {Bool(value.Completes)} {Mode(value.Mode)} {Binding(value.Binding)} ({Option(value.Call, Call)}) {List(value.AdditionalTargets, Target)} {List(value.Children, Node)}";
    private static string Binding(StageBTermBinding value) =>
        $"{{ capture := {value.Capture}, refKind := {Ref(value.RefKind)}, isRef := {Bool(value.IsRef)}, argumentMode := {ArgumentMode(value.ArgumentMode)}, callTarget := {Quote(value.Call?.Target ?? "")}, callReceiverChild := {value.Call?.ReceiverChild ?? -1}, callArgumentChildren := {IntList(value.Call?.ArgumentChildren ?? [])}, hasCall := {Bool(value.Call is not null)}, additionalMembers := {StringList(value.AdditionalMembers)} }}";
    private static string Call(StageBPrefixCall value) =>
        $"{{ target := {Target(value.Target)}, receiverChild := {value.ReceiverChild}, arguments := {List(value.Arguments, Argument)} }}";
    private static string Argument(StageBPrefixArgument value) =>
        $"{{ child := {value.Child}, ordinal := {value.Ordinal}, mode := {ArgumentMode(value.Mode)}, implicit := {Bool(value.Implicit)}, kind := {Quote(value.Kind)} }}";
    private static string Target(StageBPrefixTarget value) =>
        $"{{ kind := {TargetKind(value.Kind)}, member := {Member(value.Member)}, body := {Quote(value.Body)}, requestKind := {RequestKind(value.RequestKind)}, primitive := {Primitive(value.Member)}, fieldName := {Quote(value.Member.Name)} }}";
    private static string Parameter(StageBParameter value) =>
        $"{{ symbol := {Quote(value.Symbol)}, name := {Quote(value.Name)}, ordinal := {value.Ordinal}, typeName := {Quote(value.Type)}, refKind := {Ref(value.RefKind)}, optional := {Bool(value.Optional)} }}";
    private static string Member(StageBMember value) =>
        $"{{ symbol := {Quote(value.Symbol)}, definition := {Quote(value.Definition)}, name := {Quote(value.Name)}, declaringType := {Quote(value.DeclaringType)}, kind := {MemberKind(value.Kind)}, receiver := {Receiver(value.Receiver)}, typeName := {Quote(value.Type)}, refKind := {Ref(value.RefKind)}, parameters := {List(value.Parameters, Parameter)} }}";
    private static string Type(StageBType value) =>
        $"{{ symbol := {Quote(value.Symbol)}, kind := {TypeKind(value.Kind)}, elementType := {Quote(value.ElementType)}, typeArguments := {StringList(value.TypeArguments)}, fields := {List(value.Fields, Field)} }}";
    private static string Field(StageBFieldLayout value) =>
        $"{{ symbol := {Quote(value.Symbol)}, typeName := {Quote(value.Type)}, readOnly := {Bool(value.ReadOnly)}, associatedProperty := {Quote(value.AssociatedProperty)} }}";

    private static string Refund(StageBPrefixSuspension value) =>
        $"{{ call := {Call(value.Call)}, orderedOperands := {List(value.OrderedOperands, Node)}, continuation := {Continuation(value.Continuation)} }}";
    private static string Continuation(StageBPrefixContinuation value) =>
        $"{{ functionSymbol := {Quote(value.Function)}, block := {value.Block}, operation := {value.Operation}, callPath := {IntList(value.CallPath)}, pendingOperation := {SourceTerm(value.PendingOperation)}, sourceBlocks := {List(value.SourceBlocks, SourceBlock)} }}";
    private static string SourceBlock(StageBBlock value) =>
        $"{{ ordinal := {value.Ordinal}, kind := {Quote(value.Kind)}, reachable := {Bool(value.Reachable)}, conditionKind := {Quote(value.ConditionKind)}, operations := {List(value.Operations, SourceTerm)}, branchValue := {Option(value.BranchValue, SourceTerm)}, containsExcludedOperations := {Bool(value.ContainsExcludedOperations)}, fallThrough := {Option(value.FallThrough, Edge)}, conditional := {Option(value.Conditional, Edge)} }}";
    private static string SourceTerm(StageBTerm value) =>
        $"SimpleTransferCompletionExtractor.StageB.SourceTerm.mk {Quote(value.Kind.ToString())} {Quote(value.Type)} {Quote(value.Symbol)} {Quote(value.Operator)} {Quote(value.Constant)} {Quote(value.ArgumentKind)} {Quote(value.RefKind)} {Quote(value.Conversion)} {Bool(value.Implicit)} ({value.ParameterOrdinal}) ({Option(value.Binding, Binding)}) {List(value.Children, SourceTerm)}";

    private static string Literal(string value)
    {
        if (value.Length == 0) return ".none";
        if (value == "null") return ".null";
        if (value == "bool:true") return ".boolean true";
        if (value == "bool:false") return ".boolean false";
        int separator = value.IndexOf(':');
        if (separator < 0) throw new ExtractionException("Stage-B Lean literal changed: " + value + ".");
        string payload = value[(separator + 1)..];
        if (value.StartsWith("System.UInt", StringComparison.Ordinal) || value.StartsWith("System.Byte:", StringComparison.Ordinal)) return ".unsigned " + payload;
        if (value.StartsWith("System.Int", StringComparison.Ordinal) || value.StartsWith("System.SByte:", StringComparison.Ordinal)) return ".signed " + payload;
        if (value.StartsWith("System.String:", StringComparison.Ordinal) || value.StartsWith("System.Char:", StringComparison.Ordinal) ||
            value.StartsWith("string:", StringComparison.Ordinal) || value.StartsWith("char:", StringComparison.Ordinal)) return ".text " + Quote(payload);
        throw new ExtractionException("Stage-B Lean literal changed: " + value + ".");
    }

    private static string Primitive(StageBPrefixNode node) => node.Kind == StageBOperationKind.Unary && node.Operator == "Not;checked=False;lifted=False"
        ? ".boolNot" : node.Call is null ? ".unsupported" : Primitive(node.Call.Target.Member);

    private static string Primitive(StageBMember member) => member.Symbol switch
    {
        "global::Nethermind.Core.Address global::Nethermind.Core.Transaction.SenderAddress" => ".txSender",
        "global::Nethermind.Core.Address global::Nethermind.Core.Transaction.To" => ".txRecipient",
        "global::Nethermind.Int256.UInt256 global::Nethermind.Core.Transaction.Value" => ".txValue",
        "ref readonly global::Nethermind.Int256.UInt256 global::Nethermind.Core.Transaction.ValueRef" => ".txValueRef",
        "global::System.ReadOnlyMemory<byte> global::Nethermind.Core.Transaction.Data" => ".txData",
        "ulong global::Nethermind.Core.Transaction.GasLimit" => ".txGasLimit",
        "global::Nethermind.Core.AuthorizationTuple[] global::Nethermind.Core.Transaction.AuthorizationList" => ".txAuthorizationList",
        "bool global::Nethermind.Core.Specs.IReleaseSpec.IsEip8037Enabled" => ".specEip8037",
        "bool global::Nethermind.Core.Specs.IReleaseSpec.IsEip7708Enabled" => ".specEip7708",
        "bool global::Nethermind.Evm.Tracing.State.IStateTracer.IsTracingState" => ".tracerState",
        "bool global::Nethermind.Evm.Tracing.ITxTracer.IsTracingActions" => ".tracerActions",
        "bool global::Nethermind.Evm.Tracing.ITxTracer.IsTracingCode" => ".tracerCode",
        "bool global::Nethermind.Evm.Tracing.ITxTracer.IsTracingLogs" => ".tracerLogs",
        "bool global::Nethermind.Evm.Tracing.ITxTracer.IsTracingAccess" => ".tracerAccess",
        "global::Nethermind.Evm.State.IWorldState global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>.WorldState" => ".processorWorld",
        "global::Nethermind.Evm.ICodeInfoRepository global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>._codeInfoRepository" => ".processorCodeRepository",
        "bool global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>._isCodeOverridable" => ".processorCodeOverridable",
        "global::Nethermind.Logging.ILogger global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>.Logger" => ".processorLogger",
        "bool global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase.ForceSimpleTransferDisabled" => ".forceSimpleTransferDisabled",
        "ulong global::Nethermind.Core.Eip7825Constants.DefaultTxGasLimitCap" => ".defaultTxGasLimitCap",
        "long global::Nethermind.Core.GasCostOf.NewAccountState" => ".newAccountState",
        "global::Nethermind.Int256.UInt256 global::Nethermind.Int256.UInt256.Zero" => ".uint256Zero",
        "global::Nethermind.Evm.Tracing.ITxTracer global::Nethermind.Evm.Tracing.NullTxTracer.Instance" => ".nullTracer",
        "bool global::Nethermind.Int256.UInt256.IsZero" => ".uint256IsZero",
        "bool global::Nethermind.Evm.CodeAnalysis.CodeInfo.IsEmpty" => ".codeInfoIsEmpty",
        "bool global::Nethermind.Core.Address.operator ==(global::Nethermind.Core.Address a, global::Nethermind.Core.Address b)" => ".addressEquality",
        "global::Nethermind.Evm.CodeAnalysis.CodeInfo global::Nethermind.Evm.ICodeInfoRepository.GetCachedCodeInfo(global::Nethermind.Core.Address codeSource, bool followDelegation, global::Nethermind.Core.Specs.IReleaseSpec vmSpec, out global::Nethermind.Core.Address delegationAddress)" => ".codeLookup",
        "bool global::Nethermind.Evm.State.IReadOnlyStateProvider.IsDeadAccount(global::Nethermind.Core.Address address)" => ".isDeadAccount",
        "void global::Nethermind.Evm.State.IWorldState.SubtractFromBalance(global::Nethermind.Core.Address address, in global::Nethermind.Int256.UInt256 balanceChange, global::Nethermind.Core.Specs.IReleaseSpec spec, out global::Nethermind.Int256.UInt256 oldBalance)" => ".subtractBalance",
        "bool global::Nethermind.Evm.State.IWorldState.AddToBalanceAndCreateIfNotExists(global::Nethermind.Core.Address address, in global::Nethermind.Int256.UInt256 balanceChange, global::Nethermind.Core.Specs.IReleaseSpec spec, out global::Nethermind.Int256.UInt256 oldBalance)" => ".addBalance",
        "void global::Nethermind.Evm.State.IWorldState.Commit(global::Nethermind.Core.Specs.IReleaseSpec releaseSpec, global::Nethermind.Evm.Tracing.State.IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)" => ".commit",
        "void global::Nethermind.Evm.Metrics.IncrementEmptyCalls()" => ".trace",
        "bool global::System.Enum.HasFlag(global::System.Enum flag)" => ".enumHasFlag",
        "global::Nethermind.Int256.UInt256 global::Nethermind.Int256.UInt256.Min(in global::Nethermind.Int256.UInt256 a, in global::Nethermind.Int256.UInt256 b)" => ".uint256Min",
        "global::Nethermind.Core.Collections.JournalCollection<global::Nethermind.Core.LogEntry>.JournalCollection()" => ".journalConstructor",
        "TGasPolicy global::Nethermind.Evm.GasPolicy.IntrinsicGas<TGasPolicy>.FloorGas" or
        "TGasPolicy global::Nethermind.Evm.GasPolicy.IntrinsicGas<TGasPolicy>.Standard" or
        "ulong global::Nethermind.Evm.GasPolicy.EthereumGasPolicy.Value" or
        "long global::Nethermind.Evm.GasPolicy.EthereumGasPolicy.StateReservoir" or
        "long global::Nethermind.Evm.GasPolicy.EthereumGasPolicy.StateGasUsed" or
        "long global::Nethermind.Evm.GasPolicy.EthereumGasPolicy.StateGasSpill" or
        "long global::Nethermind.Evm.GasPolicy.EthereumGasPolicy.StateGasSpillRefunded" or
        "global::Nethermind.Evm.GasPolicy.StateGasChargeOutcome global::Nethermind.Evm.GasPolicy.StateGasChargeResult.Outcome" or
        "ulong global::Nethermind.Evm.GasPolicy.StateGasChargeResult.Value" or
        "long global::Nethermind.Evm.GasPolicy.StateGasChargeResult.StateReservoir" or
        "long global::Nethermind.Evm.GasPolicy.StateGasChargeResult.StateGasUsed" or
        "long global::Nethermind.Evm.GasPolicy.StateGasChargeResult.StateGasSpill" or
        "long global::Nethermind.Evm.GasPolicy.StateGasChargeResult.StateGasSpillRefunded" or
        "global::System.ReadOnlyMemory<byte> global::Nethermind.Evm.TransactionSubstate.Output" or
        "long global::Nethermind.Evm.TransactionSubstate.Refund" or
        "global::Nethermind.Core.Collections.JournalSet<global::Nethermind.Core.Address> global::Nethermind.Evm.TransactionSubstate._destroyList" or
        "global::Nethermind.Core.Collections.JournalCollection<global::Nethermind.Core.LogEntry> global::Nethermind.Evm.TransactionSubstate._logs" or
        "bool global::Nethermind.Evm.TransactionSubstate.ShouldRevert" or
        "global::Nethermind.Evm.EvmExceptionType global::Nethermind.Evm.TransactionSubstate.EvmExceptionType" or
        "string global::Nethermind.Evm.TransactionSubstate.Error" or
        "global::Nethermind.Logging.ILogger global::Nethermind.Evm.TransactionSubstate._logger" or
        "bool global::Nethermind.Evm.TransactionProcessing.TransactionResult.TransactionExecuted" or
        "global::Nethermind.Evm.TransactionProcessing.TransactionResult.ErrorType global::Nethermind.Evm.TransactionProcessing.TransactionResult.Error" or
        "global::Nethermind.Evm.EvmExceptionType global::Nethermind.Evm.TransactionProcessing.TransactionResult.EvmExceptionType" or
        "string global::Nethermind.Evm.TransactionProcessing.TransactionResult.ErrorDescription" => ".structField",
        _ => ".unsupported",
    };

    private static string Operation(StageBOperationKind value) => value switch
    {
        StageBOperationKind.DeclarationExpression => ".declarationExpression", StageBOperationKind.ExpressionStatement => ".expressionStatement",
        StageBOperationKind.Return => ".returnValue", StageBOperationKind.SimpleAssignment => ".assignment",
        StageBOperationKind.Invocation => ".invocation", StageBOperationKind.ObjectCreation => ".objectCreation",
        StageBOperationKind.CollectionExpression => ".collection", StageBOperationKind.FieldReference => ".field",
        StageBOperationKind.PropertyReference => ".property", StageBOperationKind.LocalReference => ".local",
        StageBOperationKind.ParameterReference => ".parameter", StageBOperationKind.InstanceReference => ".instance",
        StageBOperationKind.Literal => ".literal", StageBOperationKind.DefaultValue => ".defaultValue",
        StageBOperationKind.Binary => ".binary", StageBOperationKind.Unary => ".unary", StageBOperationKind.Conversion => ".conversion",
        StageBOperationKind.Parenthesized => ".parenthesized", StageBOperationKind.Argument => ".argument",
        StageBOperationKind.IsPattern => ".isPattern", StageBOperationKind.ConstantPattern => ".constantPattern",
        StageBOperationKind.NegatedPattern => ".negatedPattern", StageBOperationKind.IsNull => ".isNull",
        StageBOperationKind.FlowCapture => ".capture", StageBOperationKind.FlowCaptureReference => ".captureReference",
        StageBOperationKind.Discard => ".discard", _ => throw new ExtractionException("Stage-B Lean operation changed: " + value + "."),
    };
    private static string Ref(StageBRefKind value) => value switch { StageBRefKind.None => ".none", StageBRefKind.Ref => ".ref", StageBRefKind.Out => ".out", StageBRefKind.In => ".inRef", StageBRefKind.RefReadOnly => ".refReadOnly", StageBRefKind.RefReadOnlyParameter => ".refReadOnlyParameter", _ => throw new ExtractionException("Stage-B ref kind changed.") };
    private static string Mode(StageBPrefixOperandMode value) => value switch { StageBPrefixOperandMode.Value => ".value", StageBPrefixOperandMode.Location => ".location", StageBPrefixOperandMode.ReadOnlyLocation => ".readOnlyLocation", _ => throw new ExtractionException("Stage-B operand mode changed.") };
    private static string ArgumentMode(StageBArgumentMode value) => value switch { StageBArgumentMode.Value => ".value", StageBArgumentMode.ReadOnlyLocation => ".readOnlyLocation", StageBArgumentMode.ReadOnlyTemporary => ".readOnlyTemporary", StageBArgumentMode.WritableLocation => ".writableLocation", StageBArgumentMode.OutLocation => ".outLocation", _ => throw new ExtractionException("Stage-B argument mode changed.") };
    private static string TargetKind(StageBPrefixTargetKind value) => value switch
    {
        StageBPrefixTargetKind.Local => ".local", StageBPrefixTargetKind.StaticField => ".staticField",
        StageBPrefixTargetKind.DefaultValue => ".defaultValue", StageBPrefixTargetKind.Initialization => ".initialization",
        StageBPrefixTargetKind.StateCharge => ".stateCharge", StageBPrefixTargetKind.ExternalRequest => ".external",
        StageBPrefixTargetKind.RefundSuspension => ".refund", _ => throw new ExtractionException("Stage-B target kind changed."),
    };
    private static string RequestKind(StageBRequestKind? value) => value switch
    {
        null => "none", StageBRequestKind.Constant => "some .constant", StageBRequestKind.Projection => "some .projection",
        StageBRequestKind.WorldState => "some .worldState", StageBRequestKind.Tracer => "some .tracer",
        StageBRequestKind.CodeLookup => "some .codeLookup", StageBRequestKind.Pool => "some .pool",
        StageBRequestKind.Collection => "some .collection", StageBRequestKind.Arithmetic => "some .arithmetic",
        StageBRequestKind.Representation => "some .representation", StageBRequestKind.Framework => "some .framework",
        _ => throw new ExtractionException("Stage-B request kind changed."),
    };
    private static string Exit(StageBPrefixExit value) => value switch { StageBPrefixExit.Branch => ".branch", StageBPrefixExit.Return => ".returnValue", StageBPrefixExit.Suspend => ".suspend", StageBPrefixExit.OutsideSelectedDomain => ".outside", _ => throw new ExtractionException("Stage-B exit changed.") };
    private static string Condition(string value) => value switch { "None" => ".none", "WhenTrue" => ".whenTrue", "WhenFalse" => ".whenFalse", _ => throw new ExtractionException("Stage-B condition changed: " + value + ".") };
    private static string MemberKind(StageBMemberKind value) => "." + char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..];
    private static string Receiver(StageBReceiverKind value) => "." + char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..];
    private static string TypeKind(StageBTypeKind value) => "." + char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..];
    private static string Bool(bool value) => value ? "true" : "false";
    private static string IntList(IEnumerable<int> values) => "[" + string.Join(", ", values) + "]";
    private static string StringList(IEnumerable<string> values) => List(values, Quote);
    private static string Option<T>(T? value, Func<T, string> emit) where T : class => value is null ? "none" : "some (" + emit(value) + ")";
    private static string List<T>(IEnumerable<T> values, Func<T, string> emit) => "[" + string.Join(", ", values.Select(emit)) + "]";
    private static string Quote(string value)
    {
        StringBuilder builder = new("\"");
        foreach (char character in value)
            switch (character) { case '\\': builder.Append("\\\\"); break; case '"': builder.Append("\\\""); break; case '\n': builder.Append("\\n"); break; case '\r': builder.Append("\\r"); break; case '\t': builder.Append("\\t"); break; default: builder.Append(character); break; }
        return builder.Append('"').ToString();
    }
}
