// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.Lean.TransactionProcessorExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(string IrPath, string ManifestPath, string LeanPath, int SourceCount);

internal static class Extractor
{
    internal const string AncestorBaselineCommit = "b2478235e71e6a7ec2a509aa0155e25d5fdfff80";
    internal const string SourceIdentityAuthority =
        "The source-manifest SHA-256 identities are authoritative for the admitted current source; the ancestor baseline records lineage only.";
    internal const string TransactionProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal const string OptionsPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs";
    internal const string GasPolicyPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    internal const string MainnetDiPath =
        "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";

    private const string ExtractorVersion = "1.0.0";
    private const string IrFileName = "TransactionProcessorLifecycle.ir.json";
    private const string ManifestFileName = "TransactionProcessorLifecycle.source-manifest.json";
    private const string DefaultLeanPath =
        "tools/Evm/Lean/TransactionProcessorExtractor/Generated/TransactionProcessorLifecycle.lean";
    private const string LifecycleOwnerKey =
        TransactionProcessorPath + ":Nethermind.Evm.TransactionProcessing:TransactionProcessorBase/1";

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] ExpectedSemanticBindings =
    [
        "BlockProcessingModule.AddScoped<ITransactionProcessor, EthereumTransactionProcessor>",
        "EthereumTransactionProcessor -> EthereumTransactionProcessorBase -> TransactionProcessorBase<EthereumGasPolicy>",
        "ordinary Execute overload -> validation/gas/nonce gates -> simple-transfer or EVM dispatch",
        "ExecuteSimpleTransfer and ExecuteEvmTransaction -> refund/fee/finalization tail ordering",
        "ExecuteEvmCall -> snapshot/value/VM/rollback-or-deploy/refund partial-order anchors",
        "FinalizeTransaction -> gas observation, state finalization, receipt projection, TransactionResult projection",
    ];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string canonicalRoot = Path.GetFullPath(repoRoot);
        SourceFile[] sources =
        [
            Read(canonicalRoot, TransactionProcessorPath, "ordinary transaction processor"),
            Read(canonicalRoot, OptionsPath, "execution options"),
            Read(canonicalRoot, GasPolicyPath, "standard Ethereum gas policy"),
            Read(canonicalRoot, MainnetDiPath, "standard mainnet processor registration"),
        ];

        foreach (SourceFile source in sources)
        {
            RejectErrors(source);
        }

        OptionsShape options = ValidateOptions(sources[1]);
        ReachabilityShape reachability = ValidateReachability(sources[0], sources[2], sources[3]);
        LifecycleShape lifecycle = ValidateLifecycle(sources[0]);
        IrDocument document = new(
            SchemaVersion: 1,
            ExtractorVersion,
            AncestorBaselineCommit,
            SourceIdentityAuthority,
            Kernel: "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessor ordinary lifecycle control",
            options,
            reachability,
            lifecycle,
            ExternalObligations:
            [
                "sender recovery and intrinsic-gas calculation are total external hooks in this slice",
                "all validation predicates and detailed TransactionResult error payloads remain external",
                "effective-price, gas purchase, nonce, and UInt256 arithmetic correctness remain external",
                "simple-transfer selection, account existence, value transfer, VM execution, and code deposit remain external",
                "snapshot, rollback, commit, transient reset, empty-account reaping, and state-root correctness remain external",
                "refund/fee amounts, block counters, destroy-list effects, logs, and receipt payload bytes remain external",
                "tracer callback implementation and exception behavior remain external",
                "ContainerBuilder AddScoped DSL semantics, Autofac resolution semantics, and CLR/JIT behavior remain external",
            ]);

        ValidateIrShape(document);
        byte[] irBytes = Serialize(document);
        string irHash = Sha256(irBytes);
        IrDocument serialized = DeserializeIr(irBytes);
        ValidateRoundTrippedIr(document, serialized);
        byte[] leanBytes = EmitLean(serialized, irHash);

        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(canonicalRoot, DefaultLeanPath));
        EnsureWithin(leanOutputPath is null ? canonicalRoot : output, leanPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        Write(irPath, irBytes);
        Write(leanPath, leanBytes);

        SourceIdentity[] sourceIdentities = ToSourceIdentities(sources);
        Manifest manifest = new(
            SchemaVersion: 1,
            ExtractorVersion,
            CompilerVersion: typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion: LanguageVersion.CSharp14.ToDisplayString(),
            AncestorBaselineCommit,
            SourceIdentityAuthority,
            Kernel: document.Kernel,
            Sources: sourceIdentities,
            Admissions: BuildAdmissions(sourceIdentities),
            Ir: new ArtifactIdentity(IrFileName, irHash),
            Lean: new ArtifactIdentity(Normalize(DefaultLeanPath), Sha256(leanBytes)),
            CombinedSha256: CombinedHash(sourceIdentities),
            SemanticBindings: ExpectedSemanticBindings);
        byte[] manifestBytes = Serialize(manifest);
        Manifest serializedManifest = DeserializeManifest(manifestBytes);
        ValidateRoundTrippedManifest(manifest, serializedManifest);
        Write(manifestPath, manifestBytes);
        return new ExtractionResult(irPath, manifestPath, leanPath, sources.Length);
    }

    internal static void ValidateSerializedIr(byte[] sourceDerivedIr, byte[] candidateIr)
    {
        IrDocument sourceDerived = DeserializeIr(sourceDerivedIr);
        IrDocument candidate = DeserializeIr(candidateIr);
        ValidateRoundTrippedIr(sourceDerived, candidate);
    }

    internal static void ValidateSerializedManifest(byte[] sourceDerivedManifest, byte[] candidateManifest)
    {
        Manifest sourceDerived = DeserializeManifest(sourceDerivedManifest);
        Manifest candidate = DeserializeManifest(candidateManifest);
        ValidateRoundTrippedManifest(sourceDerived, candidate);
    }

    private static IrDocument DeserializeIr(byte[] bytes)
    {
        if (bytes is null)
        {
            throw new ExtractionException("The serialized lifecycle IR input was null.");
        }

        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized lifecycle IR was empty.");
            ValidateIrShape(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized lifecycle IR is not valid JSON: {exception.Message}");
        }
    }

    private static Manifest DeserializeManifest(byte[] bytes)
    {
        if (bytes is null)
        {
            throw new ExtractionException("The serialized lifecycle manifest input was null.");
        }

        try
        {
            Manifest manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized lifecycle manifest was empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized lifecycle manifest is not valid JSON: {exception.Message}");
        }
    }

    private static void ValidateManifest(Manifest manifest)
    {
        if (manifest is null)
            throw new ExtractionException("The serialized lifecycle manifest was null.");
        RequireText(manifest.ExtractorVersion, "Manifest.ExtractorVersion");
        RequireText(manifest.CompilerVersion, "Manifest.CompilerVersion");
        RequireText(manifest.LanguageVersion, "Manifest.LanguageVersion");
        RequireText(manifest.AncestorBaselineCommit, "Manifest.AncestorBaselineCommit");
        RequireText(manifest.SourceIdentityAuthority, "Manifest.SourceIdentityAuthority");
        RequireText(manifest.Kernel, "Manifest.Kernel");
        if (manifest.SchemaVersion != 1 || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.LanguageVersion != LanguageVersion.CSharp14.ToDisplayString() ||
            manifest.AncestorBaselineCommit != AncestorBaselineCommit ||
            manifest.SourceIdentityAuthority != SourceIdentityAuthority)
            throw new ExtractionException("The serialized lifecycle manifest header or build identity changed.");
        if (manifest.Sources is null || manifest.Admissions is null || manifest.Ir is null || manifest.Lean is null ||
            manifest.SemanticBindings is null || manifest.Sources.Any(static source => source is null) ||
            manifest.Admissions.Any(static admission => admission is null) ||
            manifest.SemanticBindings.Any(static binding => binding is null))
            throw new ExtractionException("The serialized lifecycle manifest contains null collections or entries.");

        HashSet<string> sourcePaths = new(StringComparer.Ordinal);
        foreach (SourceIdentity source in manifest.Sources)
        {
            RequireText(source.Path, "Manifest.Sources.Path");
            RequireSha256(source.Sha256, "Manifest.Sources.Sha256");
            if (!sourcePaths.Add(source.Path))
                throw new ExtractionException($"Duplicate lifecycle source identity {source.Path}.");
        }
        ValidateAdmissions(manifest.Admissions, manifest.Sources);
        ValidateArtifact(manifest.Ir, IrFileName, "Manifest.Ir");
        ValidateArtifact(manifest.Lean, Normalize(DefaultLeanPath), "Manifest.Lean");
        RequireSha256(manifest.CombinedSha256, "Manifest.CombinedSha256");
        if (manifest.CombinedSha256 != CombinedHash(manifest.Sources))
            throw new ExtractionException("The serialized lifecycle manifest combined source fingerprint changed.");
        if (!manifest.SemanticBindings.SequenceEqual(ExpectedSemanticBindings, StringComparer.Ordinal))
            throw new ExtractionException("The serialized lifecycle manifest semantic bindings changed.");
    }

    private static void ValidateRoundTrippedManifest(Manifest sourceDerived, Manifest roundTripped)
    {
        ValidateManifest(sourceDerived);
        ValidateManifest(roundTripped);
        if (!Serialize(sourceDerived).AsSpan().SequenceEqual(Serialize(roundTripped)))
            throw new ExtractionException("The serialized lifecycle manifest does not exactly match source-derived lineage.");
    }

    private static AdmissionIdentity[] BuildAdmissions(IEnumerable<SourceIdentity> sources) => sources
        .Select(static source => new AdmissionIdentity(
            $"{source.Path}:compilation-unit/0:complete-source", source.Sha256))
        .ToArray();

    private static void ValidateAdmissions(IReadOnlyList<AdmissionIdentity> admissions, IReadOnlyList<SourceIdentity> sources)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (AdmissionIdentity admission in admissions)
        {
            RequireText(admission.Key, "Manifest.Admissions.Key");
            RequireSha256(admission.SourceSha256, "Manifest.Admissions.SourceSha256");
            if (!admission.Key.EndsWith(":compilation-unit/0:complete-source", StringComparison.Ordinal))
                throw new ExtractionException($"Lifecycle admission identity is not path/owner-qualified: {admission.Key}.");
            if (!keys.Add(admission.Key))
                throw new ExtractionException($"Duplicate owner-qualified lifecycle admission identity {admission.Key}.");
        }
        if (!admissions.SequenceEqual(BuildAdmissions(sources)))
            throw new ExtractionException("The serialized lifecycle admission identities do not match the source identities.");
    }

    private static void ValidateArtifact(ArtifactIdentity artifact, string expectedPath, string field)
    {
        if (artifact.Path != expectedPath)
            throw new ExtractionException($"{field}.Path changed.");
        RequireSha256(artifact.Sha256, $"{field}.Sha256");
    }

    private static void RequireSha256(string? value, string field)
    {
        if (value is null || value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
            throw new ExtractionException($"{field} is not a SHA-256 identity.");
    }

    private static void ValidateIrShape(IrDocument document)
    {
        if (document is null)
        {
            throw new ExtractionException("The serialized lifecycle IR document was null.");
        }

        RequireText(document.ExtractorVersion, "ExtractorVersion");
        RequireText(document.AncestorBaselineCommit, "AncestorBaselineCommit");
        RequireText(document.SourceIdentityAuthority, "SourceIdentityAuthority");
        RequireText(document.Kernel, "Kernel");
        ValidateIrOptionsShape(document.Options);
        ValidateIrReachabilityShape(document.Reachability);
        ValidateIrLifecycleShape(document.Lifecycle);
        RequireTextArray(document.ExternalObligations, "ExternalObligations");
    }

    private static void ValidateIrOptionsShape(OptionsShape shape)
    {
        if (shape is null)
        {
            throw new ExtractionException("The serialized lifecycle IR Options was null.");
        }
    }

    private static void ValidateIrReachabilityShape(ReachabilityShape shape)
    {
        if (shape is null)
        {
            throw new ExtractionException("The serialized lifecycle IR Reachability was null.");
        }

        RequireText(shape.Service, "Reachability.Service");
        RequireText(shape.ConcreteProcessor, "Reachability.ConcreteProcessor");
        RequireText(shape.EthereumBase, "Reachability.EthereumBase");
        RequireText(shape.ClosedGenericBase, "Reachability.ClosedGenericBase");
        ValidateStageAnchorShape(shape.Registration, "Reachability.Registration");
    }

    private static void ValidateIrLifecycleShape(LifecycleShape shape)
    {
        if (shape is null)
        {
            throw new ExtractionException("The serialized lifecycle IR Lifecycle was null.");
        }

        RequireText(shape.RestoreFormula, "Lifecycle.RestoreFormula");
        RequireText(shape.CommitFormula, "Lifecycle.CommitFormula");
        RequireText(shape.CommitBeforeFormula, "Lifecycle.CommitBeforeFormula");
        RequireStageAnchors(shape.AdmissionStages, "Lifecycle.AdmissionStages");
        RequireTextArray(shape.RejectionGates, "Lifecycle.RejectionGates");
        RequireTextArray(shape.RestoreOnRejectionGates, "Lifecycle.RestoreOnRejectionGates");
        RequireStageAnchors(shape.SimpleTransferTail, "Lifecycle.SimpleTransferTail");
        RequireStageAnchors(shape.EvmTail, "Lifecycle.EvmTail");
        RequirePartialOrderEdges(shape.EvmCallPartialOrder, "Lifecycle.EvmCallPartialOrder");
        RequireMethodFingerprints(shape.MethodFingerprints, "Lifecycle.MethodFingerprints");
        ValidateIrFinalizationShape(shape.Finalization);
    }

    private static void ValidateIrFinalizationShape(FinalizationShape shape)
    {
        if (shape is null)
        {
            throw new ExtractionException("The serialized lifecycle IR Lifecycle.Finalization was null.");
        }

        RequireText(shape.WarmupWriteGate, "Lifecycle.Finalization.WarmupWriteGate");
        RequireTextArray(shape.BranchPriority, "Lifecycle.Finalization.BranchPriority");
        RequireTextArray(shape.RestoreActions, "Lifecycle.Finalization.RestoreActions");
        RequireText(shape.BuildUpReapGate, "Lifecycle.Finalization.BuildUpReapGate");
        RequireText(shape.ReceiptGate, "Lifecycle.Finalization.ReceiptGate");
        RequireText(shape.ReceiptStateRootGate, "Lifecycle.Finalization.ReceiptStateRootGate");
        RequireText(shape.FailureOutputGate, "Lifecycle.Finalization.FailureOutputGate");
        RequireTextArray(shape.FailureErrorPrecedence, "Lifecycle.Finalization.FailureErrorPrecedence");
        RequireText(shape.ResultGate, "Lifecycle.Finalization.ResultGate");
        RequireStageAnchors(shape.Anchors, "Lifecycle.Finalization.Anchors");
    }

    private static void RequireStageAnchors(StageAnchor[]? anchors, string field)
    {
        if (anchors is null)
        {
            throw new ExtractionException($"The serialized lifecycle IR {field} was null.");
        }

        for (int index = 0; index < anchors.Length; index++)
        {
            ValidateStageAnchorShape(anchors[index], $"{field}[{index}]");
        }
    }

    private static void RequirePartialOrderEdges(PartialOrderEdge[]? edges, string field)
    {
        if (edges is null)
        {
            throw new ExtractionException($"The serialized lifecycle IR {field} was null.");
        }

        for (int index = 0; index < edges.Length; index++)
        {
            PartialOrderEdge edge = edges[index] ?? throw new ExtractionException(
                $"The serialized lifecycle IR {field}[{index}] was null.");

            ValidateStageAnchorShape(edge.Before, $"{field}[{index}].Before");
            ValidateStageAnchorShape(edge.After, $"{field}[{index}].After");
        }
    }

    private static void RequireMethodFingerprints(MethodFingerprint[]? fingerprints, string field)
    {
        if (fingerprints is null)
        {
            throw new ExtractionException($"The serialized lifecycle IR {field} was null.");
        }

        HashSet<string> keys = new(StringComparer.Ordinal);
        for (int index = 0; index < fingerprints.Length; index++)
        {
            MethodFingerprint fingerprint = fingerprints[index] ?? throw new ExtractionException(
                $"The serialized lifecycle IR {field}[{index}] was null.");

            RequireText(fingerprint.Key, $"{field}[{index}].Key");
            RequireText(fingerprint.Sha256, $"{field}[{index}].Sha256");
            if (!fingerprint.Key.StartsWith(LifecycleOwnerKey + ":method:", StringComparison.Ordinal))
                throw new ExtractionException($"The serialized lifecycle IR {field}[{index}].Key is not path/namespace/owner-qualified.");
            if (!keys.Add(fingerprint.Key))
                throw new ExtractionException($"The serialized lifecycle IR contains duplicate method identity {fingerprint.Key}.");
        }
    }

    private static void RequireTextArray(string[]? values, string field)
    {
        if (values is null)
        {
            throw new ExtractionException($"The serialized lifecycle IR {field} was null.");
        }

        for (int index = 0; index < values.Length; index++)
        {
            RequireText(values[index], $"{field}[{index}]");
        }
    }

    private static void ValidateStageAnchorShape(StageAnchor anchor, string field)
    {
        if (anchor is null)
        {
            throw new ExtractionException($"The serialized lifecycle IR {field} was null.");
        }

        RequireText(anchor.Stage, $"{field}.Stage");
        RequireText(anchor.Method, $"{field}.Method");
        RequireText(anchor.CanonicalSyntax, $"{field}.CanonicalSyntax");
    }

    private static void ValidateRoundTrippedIr(IrDocument sourceDerived, IrDocument roundTripped)
    {
        ValidateIrShape(sourceDerived);
        ValidateIrShape(roundTripped);
        RequireEqual("SchemaVersion", sourceDerived.SchemaVersion, roundTripped.SchemaVersion);
        RequireEqual("ExtractorVersion", sourceDerived.ExtractorVersion, roundTripped.ExtractorVersion);
        RequireEqual("AncestorBaselineCommit", sourceDerived.AncestorBaselineCommit, roundTripped.AncestorBaselineCommit);
        RequireEqual("SourceIdentityAuthority", sourceDerived.SourceIdentityAuthority, roundTripped.SourceIdentityAuthority);
        RequireEqual("Kernel", sourceDerived.Kernel, roundTripped.Kernel);
        ValidateIrOptionsBinding(sourceDerived.Options, roundTripped.Options);
        ValidateIrReachabilityBinding(sourceDerived.Reachability, roundTripped.Reachability);
        ValidateIrLifecycleBinding(sourceDerived.Lifecycle, roundTripped.Lifecycle);
        ValidateTextSequence(sourceDerived.ExternalObligations, roundTripped.ExternalObligations, "ExternalObligations");
    }

    private static void ValidateIrOptionsBinding(OptionsShape sourceDerived, OptionsShape roundTripped)
    {
        RequireEqual("Options.None", sourceDerived.None, roundTripped.None);
        RequireEqual("Options.Commit", sourceDerived.Commit, roundTripped.Commit);
        RequireEqual("Options.Restore", sourceDerived.Restore, roundTripped.Restore);
        RequireEqual("Options.SkipValidation", sourceDerived.SkipValidation, roundTripped.SkipValidation);
        RequireEqual("Options.Warmup", sourceDerived.Warmup, roundTripped.Warmup);
        RequireEqual("Options.BuildUp", sourceDerived.BuildUp, roundTripped.BuildUp);
    }

    private static void ValidateIrReachabilityBinding(ReachabilityShape sourceDerived, ReachabilityShape roundTripped)
    {
        RequireEqual("Reachability.Service", sourceDerived.Service, roundTripped.Service);
        RequireEqual("Reachability.ConcreteProcessor", sourceDerived.ConcreteProcessor, roundTripped.ConcreteProcessor);
        RequireEqual("Reachability.EthereumBase", sourceDerived.EthereumBase, roundTripped.EthereumBase);
        RequireEqual("Reachability.ClosedGenericBase", sourceDerived.ClosedGenericBase, roundTripped.ClosedGenericBase);
        ValidateStageAnchorBinding(sourceDerived.Registration, roundTripped.Registration, "Reachability.Registration");
    }

    private static void ValidateIrLifecycleBinding(LifecycleShape sourceDerived, LifecycleShape roundTripped)
    {
        RequireEqual("Lifecycle.RestoreFormula", sourceDerived.RestoreFormula, roundTripped.RestoreFormula);
        RequireEqual("Lifecycle.CommitFormula", sourceDerived.CommitFormula, roundTripped.CommitFormula);
        RequireEqual("Lifecycle.CommitBeforeFormula", sourceDerived.CommitBeforeFormula, roundTripped.CommitBeforeFormula);
        ValidateStageAnchorSequence(sourceDerived.AdmissionStages, roundTripped.AdmissionStages, "Lifecycle.AdmissionStages");
        ValidateTextSequence(sourceDerived.RejectionGates, roundTripped.RejectionGates, "Lifecycle.RejectionGates");
        ValidateTextSequence(sourceDerived.RestoreOnRejectionGates, roundTripped.RestoreOnRejectionGates,
            "Lifecycle.RestoreOnRejectionGates");
        ValidateStageAnchorSequence(sourceDerived.SimpleTransferTail, roundTripped.SimpleTransferTail, "Lifecycle.SimpleTransferTail");
        ValidateStageAnchorSequence(sourceDerived.EvmTail, roundTripped.EvmTail, "Lifecycle.EvmTail");
        ValidatePartialOrderEdgeSequence(sourceDerived.EvmCallPartialOrder, roundTripped.EvmCallPartialOrder,
            "Lifecycle.EvmCallPartialOrder");
        ValidateMethodFingerprintSequence(sourceDerived.MethodFingerprints, roundTripped.MethodFingerprints,
            "Lifecycle.MethodFingerprints");
        ValidateIrFinalizationBinding(sourceDerived.Finalization, roundTripped.Finalization);
    }

    private static void ValidateIrFinalizationBinding(FinalizationShape sourceDerived, FinalizationShape roundTripped)
    {
        RequireEqual("Lifecycle.Finalization.WarmupWriteGate", sourceDerived.WarmupWriteGate, roundTripped.WarmupWriteGate);
        ValidateTextSequence(sourceDerived.BranchPriority, roundTripped.BranchPriority, "Lifecycle.Finalization.BranchPriority");
        ValidateTextSequence(sourceDerived.RestoreActions, roundTripped.RestoreActions, "Lifecycle.Finalization.RestoreActions");
        RequireEqual("Lifecycle.Finalization.BuildUpReapGate", sourceDerived.BuildUpReapGate, roundTripped.BuildUpReapGate);
        RequireEqual("Lifecycle.Finalization.ReceiptGate", sourceDerived.ReceiptGate, roundTripped.ReceiptGate);
        RequireEqual("Lifecycle.Finalization.ReceiptStateRootGate", sourceDerived.ReceiptStateRootGate,
            roundTripped.ReceiptStateRootGate);
        RequireEqual("Lifecycle.Finalization.FailureOutputGate", sourceDerived.FailureOutputGate, roundTripped.FailureOutputGate);
        ValidateTextSequence(sourceDerived.FailureErrorPrecedence, roundTripped.FailureErrorPrecedence,
            "Lifecycle.Finalization.FailureErrorPrecedence");
        RequireEqual("Lifecycle.Finalization.ResultGate", sourceDerived.ResultGate, roundTripped.ResultGate);
        ValidateStageAnchorSequence(sourceDerived.Anchors, roundTripped.Anchors, "Lifecycle.Finalization.Anchors");
    }

    private static void ValidateTextSequence(string[] sourceDerived, string[] roundTripped, string field)
    {
        RequireEqual($"{field}.Length", sourceDerived.Length, roundTripped.Length);
        for (int index = 0; index < sourceDerived.Length; index++)
        {
            RequireEqual($"{field}[{index}]", sourceDerived[index], roundTripped[index]);
        }
    }

    private static void ValidateStageAnchorSequence(StageAnchor[] sourceDerived, StageAnchor[] roundTripped, string field)
    {
        RequireEqual($"{field}.Length", sourceDerived.Length, roundTripped.Length);
        for (int index = 0; index < sourceDerived.Length; index++)
        {
            ValidateStageAnchorBinding(sourceDerived[index], roundTripped[index], $"{field}[{index}]");
        }
    }

    private static void ValidatePartialOrderEdgeSequence(PartialOrderEdge[] sourceDerived, PartialOrderEdge[] roundTripped, string field)
    {
        RequireEqual($"{field}.Length", sourceDerived.Length, roundTripped.Length);
        for (int index = 0; index < sourceDerived.Length; index++)
        {
            ValidatePartialOrderEdgeBinding(sourceDerived[index], roundTripped[index], $"{field}[{index}]");
        }
    }

    private static void ValidateMethodFingerprintSequence(MethodFingerprint[] sourceDerived, MethodFingerprint[] roundTripped, string field)
    {
        RequireEqual($"{field}.Length", sourceDerived.Length, roundTripped.Length);
        for (int index = 0; index < sourceDerived.Length; index++)
        {
            ValidateMethodFingerprintBinding(sourceDerived[index], roundTripped[index], $"{field}[{index}]");
        }
    }

    private static void ValidateStageAnchorBinding(StageAnchor sourceDerived, StageAnchor roundTripped, string field)
    {
        RequireEqual($"{field}.Stage", sourceDerived.Stage, roundTripped.Stage);
        RequireEqual($"{field}.Method", sourceDerived.Method, roundTripped.Method);
        RequireEqual($"{field}.CanonicalSyntax", sourceDerived.CanonicalSyntax, roundTripped.CanonicalSyntax);
        RequireEqual($"{field}.StartLine", sourceDerived.StartLine, roundTripped.StartLine);
        RequireEqual($"{field}.StartColumn", sourceDerived.StartColumn, roundTripped.StartColumn);
        RequireEqual($"{field}.EndLine", sourceDerived.EndLine, roundTripped.EndLine);
        RequireEqual($"{field}.EndColumn", sourceDerived.EndColumn, roundTripped.EndColumn);
    }

    private static void ValidatePartialOrderEdgeBinding(PartialOrderEdge sourceDerived, PartialOrderEdge roundTripped, string field)
    {
        ValidateStageAnchorBinding(sourceDerived.Before, roundTripped.Before, $"{field}.Before");
        ValidateStageAnchorBinding(sourceDerived.After, roundTripped.After, $"{field}.After");
    }

    private static void ValidateMethodFingerprintBinding(MethodFingerprint sourceDerived, MethodFingerprint roundTripped, string field)
    {
        RequireEqual($"{field}.Key", sourceDerived.Key, roundTripped.Key);
        RequireEqual($"{field}.Sha256", sourceDerived.Sha256, roundTripped.Sha256);
    }

    private static void RequireText(string? value, string field)
    {
        if (value is null)
        {
            throw new ExtractionException($"The serialized lifecycle IR {field} was null.");
        }
    }

    private static void RequireEqual<T>(string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new ExtractionException($"The round-tripped lifecycle IR {field} changed from its source-derived value.");
        }
    }

    private static OptionsShape ValidateOptions(SourceFile source)
    {
        EnumDeclarationSyntax options = RequireSingle(
            source.Root.DescendantNodes().OfType<EnumDeclarationSyntax>(),
            static item => item.Identifier.ValueText == "ExecutionOptions",
            "ExecutionOptions must be declared exactly once.");
        if (!HasModifier(options.Modifiers, SyntaxKind.PublicKeyword) ||
            !HasAttribute(options.AttributeLists, "Flags") ||
            NamespaceOf(options) != "Nethermind.Evm.TransactionProcessing")
        {
            throw new ExtractionException("ExecutionOptions must remain the public flags enum in the transaction-processing namespace.");
        }

        Dictionary<string, string> expected = new(StringComparer.Ordinal)
        {
            ["None"] = "0",
            ["Commit"] = "1",
            ["Restore"] = "2",
            ["SkipValidation"] = "4",
            ["Warmup"] = "8",
            ["BuildUp"] = "16",
            ["SkipValidationAndCommit"] = "Commit|SkipValidation",
            ["CommitAndRestore"] = "Commit|Restore|SkipValidation",
        };
        if (options.Members.Count != expected.Count)
        {
            throw new ExtractionException("ExecutionOptions changed from the admitted eight-member layout.");
        }

        foreach (EnumMemberDeclarationSyntax member in options.Members)
        {
            if (member.EqualsValue is null ||
                !expected.TryGetValue(member.Identifier.ValueText, out string? value) ||
                Canonical(member.EqualsValue.Value) != value)
            {
                throw new ExtractionException("ExecutionOptions changed from the admitted flag values.");
            }
        }

        return new OptionsShape(0, 1, 2, 4, 8, 16);
    }

    private static ReachabilityShape ValidateReachability(
        SourceFile processor,
        SourceFile gasPolicy,
        SourceFile mainnetDi)
    {
        ClassDeclarationSyntax ethereum = RequireClass(processor.Root, "EthereumTransactionProcessor", 0);
        if (!HasModifier(ethereum.Modifiers, SyntaxKind.PublicKeyword) ||
            !HasModifier(ethereum.Modifiers, SyntaxKind.SealedKeyword) ||
            ethereum.BaseList?.Types is not [BaseTypeSyntax directBase] ||
            Canonical(directBase.Type) != "EthereumTransactionProcessorBase" ||
            ethereum.Members.Count != 0)
        {
            throw new ExtractionException("EthereumTransactionProcessor must remain the sealed standard wrapper over EthereumTransactionProcessorBase.");
        }

        ClassDeclarationSyntax ethereumBase = RequireClass(processor.Root, "EthereumTransactionProcessorBase", 0);
        if (!HasModifier(ethereumBase.Modifiers, SyntaxKind.PublicKeyword) ||
            !HasModifier(ethereumBase.Modifiers, SyntaxKind.AbstractKeyword) ||
            ethereumBase.BaseList?.Types is not [BaseTypeSyntax genericBase] ||
            Canonical(genericBase.Type) != "TransactionProcessorBase<EthereumGasPolicy>" ||
            ethereumBase.Members.Count != 0)
        {
            throw new ExtractionException("EthereumTransactionProcessorBase must remain the direct EthereumGasPolicy specialization.");
        }

        StructDeclarationSyntax policy = RequireSingle(
            gasPolicy.Root.DescendantNodes().OfType<StructDeclarationSyntax>(),
            static item => item.Identifier.ValueText == "EthereumGasPolicy",
            "EthereumGasPolicy must be declared exactly once.");
        if (!HasModifier(policy.Modifiers, SyntaxKind.PublicKeyword) ||
            policy.BaseList?.Types is not [BaseTypeSyntax policyInterface] ||
            Canonical(policyInterface.Type) != "IGasPolicy<EthereumGasPolicy>")
        {
            throw new ExtractionException("EthereumGasPolicy must remain the standard self-typed IGasPolicy implementation.");
        }

        ClassDeclarationSyntax module = RequireClass(mainnetDi.Root, "BlockProcessingModule", 0);
        MethodDeclarationSyntax load = RequireMethod(module, "Load", 1);
        InvocationExpressionSyntax[] registrations = FindInvocations(load, "AddScoped", invocation =>
            Canonical(invocation.Expression).EndsWith(
                "AddScoped<ITransactionProcessor,EthereumTransactionProcessor>",
                StringComparison.Ordinal));
        if (registrations.Length != 1)
        {
            throw new ExtractionException("BlockProcessingModule must contain exactly one scoped standard ITransactionProcessor registration.");
        }
        if (registrations[0].Ancestors().Any(static ancestor =>
                ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or IfStatementSyntax or
                    ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax))
        {
            throw new ExtractionException("The standard transaction-processor registration must remain an unconditional direct Load-body chain.");
        }
        ExpressionStatementSyntax? registrationStatement = registrations[0]
            .Ancestors()
            .OfType<ExpressionStatementSyntax>()
            .FirstOrDefault();
        if (load.Body is null ||
            registrationStatement?.Parent != load.Body ||
            load.Body.Statements.FirstOrDefault() != registrationStatement)
        {
            throw new ExtractionException("The standard transaction-processor registration must remain in the first unconditional Load-body chain.");
        }

        return new ReachabilityShape(
            "ITransactionProcessor",
            "EthereumTransactionProcessor",
            "EthereumTransactionProcessorBase",
            "TransactionProcessorBase<EthereumGasPolicy>",
            AnchorOf(mainnetDi, "mainnetRegistration", "BlockProcessingModule.Load", registrations[0]));
    }

    private static LifecycleShape ValidateLifecycle(SourceFile source)
    {
        ClassDeclarationSyntax processor = RequireClass(source.Root, "TransactionProcessorBase", 1);
        MethodDeclarationSyntax outerExecute = RequireMethod(processor, "Execute", 3);
        MethodDeclarationSyntax execute = RequireMethod(processor, "Execute", 6);
        MethodDeclarationSyntax simple = RequireMethod(processor, "ExecuteSimpleTransfer", 15);
        MethodDeclarationSyntax evm = RequireMethod(processor, "ExecuteEvmTransaction", 16);
        MethodDeclarationSyntax evmCall = RequireMethod(processor, "ExecuteEvmCall", 14);
        MethodDeclarationSyntax finalize = RequireMethod(processor, "FinalizeTransaction", 12);

        RejectNestedFunctions(outerExecute);
        RejectNestedFunctions(execute);
        RejectNestedFunctions(simple);
        RejectNestedFunctions(finalize);

        InvocationExpressionSyntax recoverBefore = RequireInvocation(outerExecute, "RecoverSenderBeforeIntrinsicGas");
        InvocationExpressionSyntax intrinsic = RequireInvocation(outerExecute, "CalculateIntrinsicGas");
        InvocationExpressionSyntax executeForward = RequireInvocation(outerExecute, "Execute");
        RequireOrder("outer ordinary Execute", recoverBefore, intrinsic, executeForward);
        if (Canonical(executeForward.ArgumentList) != "(tx,tracer,opts,header,spec,inintrinsicGas)")
        {
            throw new ExtractionException("The ordinary outer Execute overload must forward the recovered transaction, spec, and intrinsic gas directly.");
        }

        InvocationExpressionSyntax validateStatic = RequireInvocation(execute, "ValidateStatic");
        InvocationExpressionSyntax effectivePrice = RequireInvocation(execute, "CalculateEffectiveGasPrice");
        InvocationExpressionSyntax metrics = RequireInvocation(execute, "UpdateMetrics");
        InvocationExpressionSyntax recoverSender = RequireInvocation(execute, "RecoverSenderIfNeeded");
        InvocationExpressionSyntax validateSender = RequireInvocation(execute, "ValidateSender");
        InvocationExpressionSyntax buyGas = RequireInvocation(execute, "BuyGas");
        InvocationExpressionSyntax incrementNonce = RequireInvocation(execute, "IncrementNonce");
        InvocationExpressionSyntax prepare = RequireInvocation(execute, "PrepareSimpleTransferFastPath");
        InvocationExpressionSyntax commitBefore = RequireInvocation(execute, "Commit", static invocation =>
            Canonical(invocation.Expression) == "WorldState.Commit");
        InvocationExpressionSyntax availableGas = RequireInvocation(execute, "CalculateAvailableGas");
        InvocationExpressionSyntax simpleDispatch = RequireInvocation(execute, "ExecuteSimpleTransfer");
        InvocationExpressionSyntax evmDispatch = RequireInvocation(execute, "ExecuteEvmTransaction");
        RequireOrder(
            "ordinary Execute admission and route",
            validateStatic,
            effectivePrice,
            metrics,
            recoverSender,
            validateSender,
            buyGas,
            incrementNonce,
            prepare,
            commitBefore,
            availableGas,
            simpleDispatch,
            evmDispatch);

        string executeBody = Canonical(execute.Body ?? throw new ExtractionException("The ordinary Execute overload must have a body."));
        RequireContains(executeBody,
            "boolrestore=opts.HasFlag(ExecutionOptions.Restore);",
            "The restore option projection changed.");
        RequireContains(executeBody,
            "boolcommit=opts.HasFlag(ExecutionOptions.Commit)||(!opts.HasFlag(ExecutionOptions.SkipValidation)&&!spec.IsEip658Enabled);",
            "The effective commit projection changed.");
        RequireContains(executeBody,
            "if(!(result=ValidateStatic(tx,header,spec,opts,inintrinsicGas)))returnresult;",
            "Static validation must remain the first rejecting admission gate.");
        RequireContains(executeBody,
            "if(!(result=ValidateSender(tx,header,spec,tracer,opts))||!(result=BuyGas(tx,spec,tracer,opts,effectiveGasPrice,outUInt256premiumPerGas,outUInt256senderReservedGasPayment,outUInt256blobBaseFee))||!(result=IncrementNonce(tx,header,spec,tracer,opts)))",
            "Sender validation, gas purchase, and nonce update must remain one left-to-right short-circuit gate.");
        RequireContains(executeBody,
            "if(restore){WorldState.Reset(resetBlockChanges:false);}returnresult;",
            "Restore-mode cleanup must remain attached to the sender/gas/nonce rejection gate.");
        RequireContains(executeBody,
            "boolcommitBeforeExecution=commit&&(simpleTransferRecipientisnull||restore||tracer.IsTracingState);if(commitBeforeExecution)WorldState.Commit(spec,tracer.IsTracingState?tracer:NullTxTracer.Instance,commitRoots:false);",
            "The pre-execution commit condition or action changed.");
        RequireContains(executeBody,
            "if(!(result=CalculateAvailableGas(tx,spec,inintrinsicGas,outTGasPolicygasAvailable)))returnresult;",
            "Available-gas failure must remain an immediate post-commit gate.");
        RequireContains(executeBody,
            "if(simpleTransferRecipientisnotnull){returnExecuteSimpleTransfer(",
            "The simple-transfer route must remain the first terminal dispatch.");
        RequireContains(executeBody,
            "returnExecuteEvmTransaction(",
            "The EVM route must remain the ordinary fallback dispatch.");

        StageAnchor[] admissionStages =
        [
            AnchorOf(source, "recoverSenderBeforeIntrinsicGas", "Execute(Transaction, tracer, options)", recoverBefore),
            AnchorOf(source, "calculateIntrinsicGas", "Execute(Transaction, tracer, options)", intrinsic),
            AnchorOf(source, "validateStatic", "Execute(..., intrinsicGas)", validateStatic),
            AnchorOf(source, "calculateEffectiveGasPrice", "Execute(..., intrinsicGas)", effectivePrice),
            AnchorOf(source, "updateMetrics", "Execute(..., intrinsicGas)", metrics),
            AnchorOf(source, "recoverSenderIfNeeded", "Execute(..., intrinsicGas)", recoverSender),
            AnchorOf(source, "validateSender", "Execute(..., intrinsicGas)", validateSender),
            AnchorOf(source, "buyGas", "Execute(..., intrinsicGas)", buyGas),
            AnchorOf(source, "incrementNonce", "Execute(..., intrinsicGas)", incrementNonce),
            AnchorOf(source, "prepareSimpleTransferFastPath", "Execute(..., intrinsicGas)", prepare),
            AnchorOf(source, "commitBeforeExecution", "Execute(..., intrinsicGas)", commitBefore),
            AnchorOf(source, "calculateAvailableGas", "Execute(..., intrinsicGas)", availableGas),
            AnchorOf(source, "dispatchSimpleTransfer", "Execute(..., intrinsicGas)", simpleDispatch),
            AnchorOf(source, "dispatchEvm", "Execute(..., intrinsicGas)", evmDispatch),
        ];

        StageAnchor[] simpleTail = ExtractSimpleTail(source, simple);
        StageAnchor[] evmTail = ExtractEvmTail(source, evm);
        PartialOrderEdge[] evmCallEdges = ExtractEvmCallEdges(source, evmCall);
        FinalizationShape finalization = ValidateFinalization(source, finalize);
        MethodFingerprint[] methodFingerprints = ValidateMethodFingerprints(
            outerExecute,
            execute,
            simple,
            evm,
            evmCall,
            finalize);

        return new LifecycleShape(
            RestoreFormula: "opts.HasFlag(ExecutionOptions.Restore)",
            CommitFormula: "opts.HasFlag(ExecutionOptions.Commit) || (!opts.HasFlag(ExecutionOptions.SkipValidation) && !spec.IsEip658Enabled)",
            CommitBeforeFormula: "commit && (simpleTransferRecipient is null || restore || tracer.IsTracingState)",
            AdmissionStages: admissionStages,
            RejectionGates: ["validateStatic", "validateSender", "buyGas", "incrementNonce", "calculateAvailableGas"],
            RestoreOnRejectionGates: ["validateSender", "buyGas", "incrementNonce"],
            SimpleTransferTail: simpleTail,
            EvmTail: evmTail,
            EvmCallPartialOrder: evmCallEdges,
            MethodFingerprints: methodFingerprints,
            Finalization: finalization);
    }

    private static StageAnchor[] ExtractSimpleTail(SourceFile source, MethodDeclarationSyntax method)
    {
        InvocationExpressionSyntax stateCharge = RequireInvocation(method, "TryConsumeStateGas");
        InvocationExpressionSyntax payValue = RequireInvocation(method, "PayValue");
        InvocationExpressionSyntax refund = RequireInvocation(method, "Refund");
        InvocationExpressionSyntax fees = RequireInvocation(method, "UpdateHeaderGasUsedAndPayFees");
        InvocationExpressionSyntax finalize = RequireInvocation(method, "FinalizeTransaction");
        RequireOrder("simple-transfer tail", stateCharge, payValue, refund, fees, finalize);
        return
        [
            AnchorOf(source, "recipientStateCharge", "ExecuteSimpleTransfer", stateCharge),
            AnchorOf(source, "payValue", "ExecuteSimpleTransfer", payValue),
            AnchorOf(source, "refund", "ExecuteSimpleTransfer", refund),
            AnchorOf(source, "headerGasAndPayFees", "ExecuteSimpleTransfer", fees),
            AnchorOf(source, "finalizeTransaction", "ExecuteSimpleTransfer", finalize),
        ];
    }

    private static StageAnchor[] ExtractEvmTail(SourceFile source, MethodDeclarationSyntax method)
    {
        InvocationExpressionSyntax delegations = RequireInvocation(method, "ProcessDelegations");
        InvocationExpressionSyntax environment = RequireInvocation(method, "BuildExecutionEnvironment");
        InvocationExpressionSyntax stateCharge = RequireInvocation(method, "TryConsumeStateGas");
        InvocationExpressionSyntax executeCall = RequireFirstInvocation(method, "ExecuteEvmCall");
        InvocationExpressionSyntax fees = RequireInvocation(method, "UpdateHeaderGasUsedAndPayFees");
        InvocationExpressionSyntax destroy = RequireFirstInvocationAfter(method, "FinalizeDestroyedAccount", fees.SpanStart);
        InvocationExpressionSyntax finalize = RequireInvocation(method, "FinalizeTransaction");
        RequireOrder("EVM outer tail", delegations, environment, stateCharge, executeCall, fees, destroy, finalize);
        return
        [
            AnchorOf(source, "processDelegations", "ExecuteEvmTransaction", delegations),
            AnchorOf(source, "buildExecutionEnvironment", "ExecuteEvmTransaction", environment),
            AnchorOf(source, "recipientStateCharge", "ExecuteEvmTransaction", stateCharge),
            AnchorOf(source, "executeEvmCall", "ExecuteEvmTransaction", executeCall),
            AnchorOf(source, "headerGasAndPayFees", "ExecuteEvmTransaction", fees),
            AnchorOf(source, "deferredDestroyList", "ExecuteEvmTransaction", destroy),
            AnchorOf(source, "finalizeTransaction", "ExecuteEvmTransaction", finalize),
        ];
    }

    private static PartialOrderEdge[] ExtractEvmCallEdges(SourceFile source, MethodDeclarationSyntax method)
    {
        InvocationExpressionSyntax snapshot = RequireFirstInvocation(method, "TakeSnapshot");
        InvocationExpressionSyntax payValue = RequireInvocation(method, "PayValue");
        InvocationExpressionSyntax vm = RequireFirstInvocation(method, "ExecuteTransaction");
        IfStatementSyntax rollbackBranch = RequireSingle(
            ExecutableDescendants(method).OfType<IfStatementSyntax>(),
            static branch => Canonical(branch.Condition) == "substate.ShouldRevert||substate.IsError",
            "ExecuteEvmCall must retain exactly one post-VM revert-or-error rollback branch.");
        InvocationExpressionSyntax rollback = RequireSingle(
            ExecutableDescendants(rollbackBranch.Statement).OfType<InvocationExpressionSyntax>(),
            static invocation =>
                Canonical(invocation.Expression) == "WorldState.Restore" &&
                Canonical(invocation.ArgumentList) == "(snapshot)",
            "The post-VM revert-or-error branch must restore the admitted top-level snapshot exactly once.");
        InvocationExpressionSyntax deploy = RequireInvocation(method, "DeployContract");
        InvocationExpressionSyntax refund = RequireInvocation(method, "Refund");
        RequireBefore("EVM call snapshot before value transfer", snapshot, payValue);
        RequireBefore("EVM call value transfer before VM", payValue, vm);
        RequireBefore("EVM call VM before rollback", vm, rollback);
        RequireBefore("EVM call VM before deployment", vm, deploy);
        RequireBefore("EVM call rollback before normal refund", rollback, refund);
        RequireBefore("EVM call deployment before normal refund", deploy, refund);
        return
        [
            Edge(source, "topExecutionSnapshot", snapshot, "payValue", payValue),
            Edge(source, "payValue", payValue, "vmExecution", vm),
            Edge(source, "vmExecution", vm, "executionRollback", rollback),
            Edge(source, "vmExecution", vm, "deployment", deploy),
            Edge(source, "executionRollback", rollback, "refund", refund),
            Edge(source, "deployment", deploy, "refund", refund),
        ];
    }

    private static FinalizationShape ValidateFinalization(SourceFile source, MethodDeclarationSyntax method)
    {
        BlockSyntax body = method.Body ?? throw new ExtractionException("FinalizeTransaction must have a body.");
        if (body.Statements.Count != 5 ||
            body.Statements[0] is not IfStatementSyntax blockGasWrite ||
            body.Statements[1] is not IfStatementSyntax spentGasWrite ||
            body.Statements[2] is not IfStatementSyntax stateFinalization ||
            body.Statements[3] is not IfStatementSyntax receipt ||
            body.Statements[4] is not ReturnStatementSyntax resultReturn)
        {
            throw new ExtractionException("FinalizeTransaction changed from its admitted five-stage control shape.");
        }

        string warmupCondition = "!opts.HasFlag(ExecutionOptions.Warmup)";
        if (Canonical(blockGasWrite.Condition) != warmupCondition ||
            Canonical(spentGasWrite.Condition) != warmupCondition ||
            Canonical(blockGasWrite.Statement) != "{tx.BlockGasUsed=spentGas.EffectiveBlockGas;}" ||
            Canonical(spentGasWrite.Statement) != "tx.SpentGas=spentGas.SpentGas;")
        {
            throw new ExtractionException("FinalizeTransaction warmup gas-observation gates changed.");
        }

        if (Canonical(stateFinalization.Condition) != "restore" ||
            stateFinalization.Else?.Statement is not IfStatementSyntax commitBranch ||
            Canonical(commitBranch.Condition) != "commit" ||
            commitBranch.Else is null)
        {
            throw new ExtractionException("FinalizeTransaction restore/commit/reset branch priority changed.");
        }

        string restoreBody = Canonical(stateFinalization.Statement);
        RequireContains(restoreBody, "WorldState.Reset(resetBlockChanges:false);", "Restore finalization must reset transaction changes first.");
        RequireContains(restoreBody, "if(deleteCallerAccount){WorldState.DeleteAccount(tx.SenderAddress!);}", "Restore finalization must retain the synthetic-caller deletion branch.");
        RequireContains(restoreBody, "if(!senderReservedGasPayment.IsZero){WorldState.AddToBalance(tx.SenderAddress!,senderReservedGasPayment,spec);}", "Restore finalization must return the reserved payment when nonzero.");
        RequireContains(restoreBody, "DecrementNonce(tx);", "Restore finalization must decrement the sender nonce.");
        RequireContains(restoreBody, "WorldState.Commit(spec,commitRoots:false);", "Restore finalization must commit the restored sender bookkeeping without roots.");

        string commitBody = Canonical(commitBranch.Statement);
        RequireContains(commitBody,
            "WorldState.Commit(spec,tracer.IsTracingState?tracer:NullStateTracer.Instance,commitRoots:!spec.IsEip658Enabled);",
            "Commit finalization changed its tracer or pre-EIP-658 root gate.");
        string resetBody = Canonical(commitBranch.Else.Statement);
        RequireContains(resetBody, "WorldState.ResetTransient();", "Non-commit finalization must reset transient state.");
        RequireContains(resetBody,
            "if(opts==ExecutionOptions.BuildUp&&spec.IsEip8037Enabled){WorldState.ReapEmptyAccounts();}",
            "Build-up EIP-8037 finalization must reap empty accounts after transient reset.");

        if (Canonical(receipt.Condition) != "tracer.IsTracingReceipt")
        {
            throw new ExtractionException("Receipt finalization must remain gated by tracer.IsTracingReceipt.");
        }
        string receiptBody = Canonical(receipt.Statement);
        RequireContains(receiptBody,
            "if(!spec.IsEip658Enabled){WorldState.RecalculateStateRoot();stateRoot=WorldState.StateRoot;}",
            "Receipt state-root projection changed from the pre-EIP-658 gate.");
        RequireContains(receiptBody,
            "if(statusCode==StatusCode.Failure)",
            "Receipt status projection must retain its explicit failure branch.");
        RequireContains(receiptBody,
            "byte[]output=substate.ShouldRevert?substate.Output.AsReadOnlyArray():[];",
            "Failure receipts must expose returndata only for REVERT.");
        RequireContains(receiptBody,
            "if(errorisnull&&substate.EvmExceptionTypeisnotEvmExceptionType.None){error=substate.EvmExceptionType.FastToString();}",
            "Failure receipt errors must fall back from substate error to EVM exception.");
        RequireContains(receiptBody,
            "tracer.MarkAsFailed(executingAccount,spentGas,output,error,stateRoot);",
            "Failure receipt projection changed.");
        RequireContains(receiptBody,
            "LogEntry[]logs=substate.Logs.Count!=0?substate.LogsToArray():[];tracer.MarkAsSuccess(executingAccount,spentGas,substate.Output.AsReadOnlyArray(),logs,stateRoot);",
            "Success receipt output/log projection changed.");
        if (resultReturn.Expression is null ||
            Canonical(resultReturn.Expression) !=
            "substate.EvmExceptionType!=EvmExceptionType.None?TransactionResult.EvmException(substate.EvmExceptionType,substate.SubstateError):TransactionResult.Ok")
        {
            throw new ExtractionException("FinalizeTransaction result must remain projected from EvmExceptionType independently of receipt status.");
        }

        return new FinalizationShape(
            WarmupWriteGate: "!opts.HasFlag(ExecutionOptions.Warmup)",
            BranchPriority: ["restore", "commit", "resetTransient"],
            RestoreActions: ["reset", "deleteCaller | returnReservedGas; decrementNonce; commitWithoutRoots"],
            BuildUpReapGate: "opts == ExecutionOptions.BuildUp && spec.IsEip8037Enabled",
            ReceiptGate: "tracer.IsTracingReceipt",
            ReceiptStateRootGate: "!spec.IsEip658Enabled",
            FailureOutputGate: "substate.ShouldRevert",
            FailureErrorPrecedence: ["substate.Error", "substate.EvmExceptionType", "none"],
            ResultGate: "substate.EvmExceptionType != EvmExceptionType.None",
            Anchors:
            [
                AnchorOf(source, "writeBlockGas", "FinalizeTransaction", blockGasWrite),
                AnchorOf(source, "writeSpentGas", "FinalizeTransaction", spentGasWrite),
                AnchorOf(source, "finalizeState", "FinalizeTransaction", stateFinalization),
                AnchorOf(source, "projectReceipt", "FinalizeTransaction", receipt),
                AnchorOf(source, "projectResult", "FinalizeTransaction", resultReturn),
            ]);
    }

    private static MethodFingerprint[] ValidateMethodFingerprints(
        MethodDeclarationSyntax outerExecute,
        MethodDeclarationSyntax execute,
        MethodDeclarationSyntax simple,
        MethodDeclarationSyntax evm,
        MethodDeclarationSyntax evmCall,
        MethodDeclarationSyntax finalize)
    {
        MethodFingerprint[] actual =
        [
            Fingerprint("Execute/3", outerExecute),
            Fingerprint("Execute/6", execute),
            Fingerprint("ExecuteSimpleTransfer/15", simple),
            Fingerprint("ExecuteEvmTransaction/16", evm),
            Fingerprint("ExecuteEvmCall/14", evmCall),
            Fingerprint("FinalizeTransaction/12", finalize),
        ];
        string[] expected =
        [
            "456eb3167c3fbabe0d5a81d62a48e3818a3c59a0376399df749d1a0405043499",
            "bd2f30b2ef5ffb7dc2daa9d6ac1b8e5f7c1bb779fbcc4079bea54f142ff91be1",
            "b3a4b0544ecafaf58fab5392b1eb3cf5a74f5ad13832075295281e6e9b13dc84",
            "fb0349e6fb14296142e90160a7a1eef090f0833177fe143be9183c6f9cb3ad89",
            "39c8cdb3e99f5028a05421420a8ea351297500e83aa018a7ebae8e9d91426bf8",
            "b2fa7d276948dcb0b71acc4d4a47063bab3ac6a6505d3b73403d6c5326df459b",
        ];
        List<string> mismatches = [];
        for (int i = 0; i < actual.Length; i++)
        {
            if (!string.Equals(actual[i].Sha256, expected[i], StringComparison.Ordinal))
            {
                mismatches.Add($"{actual[i].Key}={actual[i].Sha256}");
            }
        }
        if (mismatches.Count != 0)
        {
            throw new ExtractionException(
                $"The admitted transaction lifecycle method-token fingerprints changed: {string.Join(", ", mismatches)}");
        }
        return actual;
    }

    private static MethodFingerprint Fingerprint(string name, MethodDeclarationSyntax method)
    {
        StringBuilder tokens = new();
        foreach (SyntaxToken token in method.DescendantTokens(descendIntoTrivia: false))
        {
            tokens.Append(token.RawKind).Append(':').Append(token.Text.Length).Append(':')
                .Append(token.Text).Append(';');
        }
        return new MethodFingerprint($"{LifecycleOwnerKey}:method:{name}", Sha256(Encoding.UTF8.GetBytes(tokens.ToString())));
    }

    private static byte[] EmitLean(IrDocument document, string irHash)
    {
        StringBuilder builder = new();
        builder.AppendLine($"-- Generated by TransactionProcessorExtractor {document.ExtractorVersion}.");
        builder.AppendLine($"-- Ancestor baseline Nethermind commit: {document.AncestorBaselineCommit}");
        builder.AppendLine($"-- {document.SourceIdentityAuthority}");
        builder.AppendLine($"-- IR SHA-256: {irHash}");
        builder.AppendLine("-- Definitions only: all proofs live in the handwritten Refinement module.");
        builder.AppendLine("-- External hook implementations are intentionally not represented by these control definitions.");
        builder.AppendLine();
        builder.AppendLine("namespace Eip803x.Generated.TransactionProcessorLifecycle");
        builder.AppendLine();
        builder.AppendLine("inductive Stage where");
        builder.AppendLine("  | recoverSenderBeforeIntrinsicGas | calculateIntrinsicGas | validateStatic");
        builder.AppendLine("  | calculateEffectiveGasPrice | updateMetrics | recoverSenderIfNeeded");
        builder.AppendLine("  | validateSender | buyGas | incrementNonce | prepareSimpleTransferFastPath");
        builder.AppendLine("  | commitBeforeExecution | calculateAvailableGas | dispatchSimpleTransfer | dispatchEvm");
        builder.AppendLine("  | recipientStateCharge | payValue | refund | headerGasAndPayFees");
        builder.AppendLine("  | processDelegations | buildExecutionEnvironment | executeEvmCall");
        builder.AppendLine("  | deferredDestroyList | finalizeTransaction");
        builder.AppendLine("  | topExecutionSnapshot | vmExecution | executionRollback | deployment");
        builder.AppendLine("  | restore | commit | resetTransient | receiptStart | receiptObserve");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("inductive RejectStage where");
        builder.AppendLine("  | validateStatic | validateSender | buyGas | incrementNonce | calculateAvailableGas");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("inductive Route where | simpleTransfer | evm deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("structure Options where");
        builder.AppendLine("  commit : Bool");
        builder.AppendLine("  restore : Bool");
        builder.AppendLine("  skipValidation : Bool");
        builder.AppendLine("  warmup : Bool");
        builder.AppendLine("  buildUp : Bool");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine($"def noneValue : Nat := {document.Options.None}");
        builder.AppendLine($"def commitValue : Nat := {document.Options.Commit}");
        builder.AppendLine($"def restoreValue : Nat := {document.Options.Restore}");
        builder.AppendLine($"def skipValidationValue : Nat := {document.Options.SkipValidation}");
        builder.AppendLine($"def warmupValue : Nat := {document.Options.Warmup}");
        builder.AppendLine($"def buildUpValue : Nat := {document.Options.BuildUp}");
        builder.AppendLine();
        builder.AppendLine("structure PrefixInput where");
        builder.AppendLine("  options : Options");
        builder.AppendLine("  eip658 : Bool");
        builder.AppendLine("  simpleTransfer : Bool");
        builder.AppendLine("  tracingState : Bool");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("structure AdmissionHooks where");
        builder.AppendLine("  validateStatic : Bool");
        builder.AppendLine("  validateSender : Bool");
        builder.AppendLine("  buyGas : Bool");
        builder.AppendLine("  incrementNonce : Bool");
        builder.AppendLine("  calculateAvailableGas : Bool");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("inductive PrefixOutcome where");
        builder.AppendLine("  | rejected (stage : RejectStage) | routed (route : Route)");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("structure PrefixResult where");
        builder.AppendLine("  outcome : PrefixOutcome");
        builder.AppendLine("  restoreOnReject : Bool");
        builder.AppendLine("  committedBeforeExecution : Bool");
        builder.AppendLine("  trace : List Stage");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("def effectiveCommit (input : PrefixInput) : Bool :=");
        builder.AppendLine("  input.options.commit || (!input.options.skipValidation && !input.eip658)");
        builder.AppendLine();
        builder.AppendLine("def shouldCommitBeforeExecution (input : PrefixInput) : Bool :=");
        builder.AppendLine("  effectiveCommit input && (!input.simpleTransfer || input.options.restore || input.tracingState)");
        builder.AppendLine();
        builder.AppendLine("def reject (input : PrefixInput) (stage : RejectStage)");
        builder.AppendLine("    (restoreEligible : Bool) (trace : List Stage) : PrefixResult :=");
        builder.AppendLine("  { outcome := .rejected stage");
        builder.AppendLine("    restoreOnReject := restoreEligible && input.options.restore");
        builder.AppendLine("    committedBeforeExecution := false");
        builder.AppendLine("    trace }");
        builder.AppendLine();
        builder.AppendLine("def runPrefix (input : PrefixInput) (hooks : AdmissionHooks) : PrefixResult :=");
        builder.AppendLine("  let staticTrace := [.recoverSenderBeforeIntrinsicGas, .calculateIntrinsicGas, .validateStatic]");
        builder.AppendLine("  if !hooks.validateStatic then reject input .validateStatic false staticTrace else");
        builder.AppendLine("  let senderTrace := staticTrace ++ [.calculateEffectiveGasPrice, .updateMetrics,");
        builder.AppendLine("    .recoverSenderIfNeeded, .validateSender]");
        builder.AppendLine("  if !hooks.validateSender then reject input .validateSender true senderTrace else");
        builder.AppendLine("  let boughtTrace := senderTrace ++ [.buyGas]");
        builder.AppendLine("  if !hooks.buyGas then reject input .buyGas true boughtTrace else");
        builder.AppendLine("  let nonceTrace := boughtTrace ++ [.incrementNonce]");
        builder.AppendLine("  if !hooks.incrementNonce then reject input .incrementNonce true nonceTrace else");
        builder.AppendLine("  let preparedTrace := nonceTrace ++ [.prepareSimpleTransferFastPath]");
        builder.AppendLine("  let commitBefore := shouldCommitBeforeExecution input");
        builder.AppendLine("  let committedTrace := if commitBefore then preparedTrace ++ [.commitBeforeExecution] else preparedTrace");
        builder.AppendLine("  let availableTrace := committedTrace ++ [.calculateAvailableGas]");
        builder.AppendLine("  if !hooks.calculateAvailableGas then");
        builder.AppendLine("    { outcome := .rejected .calculateAvailableGas, restoreOnReject := false,");
        builder.AppendLine("      committedBeforeExecution := commitBefore, trace := availableTrace }");
        builder.AppendLine("  else if input.simpleTransfer then");
        builder.AppendLine("    { outcome := .routed .simpleTransfer, restoreOnReject := false,");
        builder.AppendLine("      committedBeforeExecution := commitBefore, trace := availableTrace ++ [.dispatchSimpleTransfer] }");
        builder.AppendLine("  else");
        builder.AppendLine("    { outcome := .routed .evm, restoreOnReject := false,");
        builder.AppendLine("      committedBeforeExecution := commitBefore, trace := availableTrace ++ [.dispatchEvm] }");
        builder.AppendLine();
        EmitStageList(builder, "simpleTransferTailOrder", document.Lifecycle.SimpleTransferTail);
        EmitStageList(builder, "evmOuterTailOrder", document.Lifecycle.EvmTail);
        EmitEdgeList(builder, "evmCallRequiredOrder", document.Lifecycle.EvmCallPartialOrder);
        builder.AppendLine("inductive Status where | success | failure deriving DecidableEq, Repr");
        builder.AppendLine("inductive StateAction where");
        builder.AppendLine("  | reset | deleteCaller | returnReservedGas | decrementNonce");
        builder.AppendLine("  | commitWithoutRoots | commitWithRoots (roots : Bool)");
        builder.AppendLine("  | resetTransient | reapEmptyAccounts");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine("inductive ErrorSource where | substate | evmException | none deriving DecidableEq, Repr");
        builder.AppendLine("inductive ResultProjection where | ok | evmException deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("structure SpecFlags where");
        builder.AppendLine("  eip658 : Bool");
        builder.AppendLine("  eip8037 : Bool");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine("structure SubstateProjection where");
        builder.AppendLine("  shouldRevert : Bool");
        builder.AppendLine("  hasError : Bool");
        builder.AppendLine("  hasEvmException : Bool");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine("structure FinalizeInput where");
        builder.AppendLine("  options : Options");
        builder.AppendLine("  spec : SpecFlags");
        builder.AppendLine("  tracingReceipt : Bool");
        builder.AppendLine("  deleteCallerAccount : Bool");
        builder.AppendLine("  hasReservedGasPayment : Bool");
        builder.AppendLine("  status : Status");
        builder.AppendLine("  substate : SubstateProjection");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine("structure ReceiptProjection where");
        builder.AppendLine("  status : Status");
        builder.AppendLine("  includesStateRoot : Bool");
        builder.AppendLine("  includesOutput : Bool");
        builder.AppendLine("  includesLogs : Bool");
        builder.AppendLine("  errorSource : ErrorSource");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine("structure FinalizeResult where");
        builder.AppendLine("  writesBlockGas : Bool");
        builder.AppendLine("  writesSpentGas : Bool");
        builder.AppendLine("  stateActions : List StateAction");
        builder.AppendLine("  receipt : Option ReceiptProjection");
        builder.AppendLine("  result : ResultProjection");
        builder.AppendLine("  trace : List Stage");
        builder.AppendLine("  deriving DecidableEq, Repr");
        builder.AppendLine();
        builder.AppendLine("def isExactBuildUp (options : Options) : Bool :=");
        builder.AppendLine("  options.buildUp && !options.commit && !options.restore && !options.skipValidation && !options.warmup");
        builder.AppendLine();
        builder.AppendLine("def finalizationActions (input : FinalizeInput) : List StateAction :=");
        builder.AppendLine("  if input.options.restore then");
        builder.AppendLine("    [.reset] ++ if input.deleteCallerAccount then [.deleteCaller] else");
        builder.AppendLine("      (if input.hasReservedGasPayment then [.returnReservedGas] else []) ++ [.decrementNonce, .commitWithoutRoots]");
        builder.AppendLine("  else if input.options.commit || (!input.options.skipValidation && !input.spec.eip658) then");
        builder.AppendLine("    [.commitWithRoots (!input.spec.eip658)]");
        builder.AppendLine("  else [.resetTransient] ++");
        builder.AppendLine("    if isExactBuildUp input.options && input.spec.eip8037 then [.reapEmptyAccounts] else []");
        builder.AppendLine();
        builder.AppendLine("def receiptProjection (input : FinalizeInput) : Option ReceiptProjection :=");
        builder.AppendLine("  if !input.tracingReceipt then none else");
        builder.AppendLine("  let failed := input.status = .failure");
        builder.AppendLine("  let errorSource := if !failed then .none else if input.substate.hasError then .substate");
        builder.AppendLine("    else if input.substate.hasEvmException then .evmException else .none");
        builder.AppendLine("  some");
        builder.AppendLine("    { status := input.status");
        builder.AppendLine("      includesStateRoot := !input.spec.eip658");
        builder.AppendLine("      includesOutput := !failed || input.substate.shouldRevert");
        builder.AppendLine("      includesLogs := !failed");
        builder.AppendLine("      errorSource }");
        builder.AppendLine();
        builder.AppendLine("def finalizationTrace (input : FinalizeInput) : List Stage :=");
        builder.AppendLine("  let stateStage := if input.options.restore then .restore else");
        builder.AppendLine("    if input.options.commit || (!input.options.skipValidation && !input.spec.eip658) then .commit else .resetTransient");
        builder.AppendLine("  [stateStage] ++ if input.tracingReceipt then [.receiptStart, .receiptObserve] else []");
        builder.AppendLine();
        builder.AppendLine("def finalize (input : FinalizeInput) : FinalizeResult :=");
        builder.AppendLine("  { writesBlockGas := !input.options.warmup");
        builder.AppendLine("    writesSpentGas := !input.options.warmup");
        builder.AppendLine("    stateActions := finalizationActions input");
        builder.AppendLine("    receipt := receiptProjection input");
        builder.AppendLine("    result := if input.substate.hasEvmException then .evmException else .ok");
        builder.AppendLine("    trace := finalizationTrace input }");
        builder.AppendLine();
        builder.AppendLine("end Eip803x.Generated.TransactionProcessorLifecycle");
        return new UTF8Encoding(false).GetBytes(builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void EmitStageList(StringBuilder builder, string name, StageAnchor[] anchors)
    {
        builder.AppendLine($"def {name} : List Stage :=");
        builder.Append("  [");
        for (int i = 0; i < anchors.Length; i++)
        {
            if (i != 0) builder.Append(", ");
            builder.Append('.').Append(anchors[i].Stage);
        }
        builder.AppendLine("]");
        builder.AppendLine();
    }

    private static void EmitEdgeList(StringBuilder builder, string name, PartialOrderEdge[] edges)
    {
        builder.AppendLine($"def {name} : List (Stage × Stage) :=");
        builder.Append("  [");
        for (int i = 0; i < edges.Length; i++)
        {
            if (i != 0) builder.Append(", ");
            builder.Append("(.").Append(edges[i].Before.Stage).Append(", .")
                .Append(edges[i].After.Stage).Append(')');
        }
        builder.AppendLine("]");
        builder.AppendLine();
    }

    private static PartialOrderEdge Edge(
        SourceFile source,
        string beforeName,
        SyntaxNode before,
        string afterName,
        SyntaxNode after) =>
        new(AnchorOf(source, beforeName, "ExecuteEvmCall", before), AnchorOf(source, afterName, "ExecuteEvmCall", after));

    private static StageAnchor AnchorOf(SourceFile source, string stage, string method, SyntaxNode node)
    {
        LinePositionSpan span = source.Text.Lines.GetLinePositionSpan(node.Span);
        return new StageAnchor(
            stage,
            method,
            Canonical(node),
            span.Start.Line + 1,
            span.Start.Character + 1,
            span.End.Line + 1,
            span.End.Character + 1);
    }

    private static void RequireOrder(string scope, params SyntaxNode[] nodes)
    {
        for (int i = 1; i < nodes.Length; i++)
        {
            RequireBefore(scope, nodes[i - 1], nodes[i]);
        }
    }

    private static void RequireBefore(string scope, SyntaxNode before, SyntaxNode after)
    {
        if (before.SpanStart >= after.SpanStart)
        {
            throw new ExtractionException($"The admitted {scope} order changed.");
        }
    }

    private static InvocationExpressionSyntax RequireInvocation(
        MethodDeclarationSyntax method,
        string name,
        Func<InvocationExpressionSyntax, bool>? extra = null)
    {
        InvocationExpressionSyntax[] matches = FindInvocations(method, name, extra);
        if (matches.Length != 1)
        {
            throw new ExtractionException($"{method.Identifier.ValueText} must contain exactly one admitted {name} call, found {matches.Length}.");
        }
        return matches[0];
    }

    private static InvocationExpressionSyntax RequireFirstInvocation(MethodDeclarationSyntax method, string name)
    {
        InvocationExpressionSyntax[] matches = FindInvocations(method, name, null);
        if (matches.Length == 0)
        {
            throw new ExtractionException($"{method.Identifier.ValueText} must contain an admitted {name} call.");
        }
        return matches[0];
    }

    private static InvocationExpressionSyntax RequireFirstInvocationAfter(
        MethodDeclarationSyntax method,
        string name,
        int position,
        Func<InvocationExpressionSyntax, bool>? extra = null)
    {
        InvocationExpressionSyntax[] matches = FindInvocations(method, name, extra);
        foreach (InvocationExpressionSyntax match in matches)
        {
            if (match.SpanStart > position) return match;
        }
        throw new ExtractionException($"{method.Identifier.ValueText} must contain an admitted {name} call after the preceding stage.");
    }

    private static InvocationExpressionSyntax[] FindInvocations(
        MethodDeclarationSyntax method,
        string name,
        Func<InvocationExpressionSyntax, bool>? extra)
    {
        List<InvocationExpressionSyntax> matches = [];
        foreach (InvocationExpressionSyntax invocation in ExecutableDescendants(method).OfType<InvocationExpressionSyntax>())
        {
            if (InvocationName(invocation) == name && (extra is null || extra(invocation)))
            {
                matches.Add(invocation);
            }
        }
        matches.Sort(static (left, right) => left.SpanStart.CompareTo(right.SpanStart));
        return matches.ToArray();
    }

    private static IEnumerable<SyntaxNode> ExecutableDescendants(SyntaxNode node) =>
        node.DescendantNodes(static child =>
            child is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax);

    private static string InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static MethodDeclarationSyntax RequireMethod(ClassDeclarationSyntax type, string name, int parameterCount)
    {
        List<MethodDeclarationSyntax> matches = [];
        foreach (MemberDeclarationSyntax member in type.Members)
        {
            if (member is MethodDeclarationSyntax method &&
                method.Identifier.ValueText == name &&
                method.ParameterList.Parameters.Count == parameterCount)
            {
                matches.Add(method);
            }
        }
        if (matches.Count != 1)
        {
            throw new ExtractionException($"{type.Identifier.ValueText}.{name}/{parameterCount} must be declared exactly once.");
        }
        return matches[0];
    }

    private static ClassDeclarationSyntax RequireClass(CompilationUnitSyntax root, string name, int typeParameterCount) =>
        RequireSingle(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            item => item.Identifier.ValueText == name &&
                (item.TypeParameterList?.Parameters.Count ?? 0) == typeParameterCount,
            $"{name} with {typeParameterCount} type parameters must be declared exactly once.");

    private static T RequireSingle<T>(IEnumerable<T> source, Func<T, bool> predicate, string message)
    {
        T? match = default;
        int count = 0;
        foreach (T item in source)
        {
            if (!predicate(item)) continue;
            match = item;
            count++;
        }
        if (count != 1 || match is null)
        {
            throw new ExtractionException(message);
        }
        return match;
    }

    private static void RejectNestedFunctions(MethodDeclarationSyntax method)
    {
        if (method.DescendantNodes().Any(static node =>
                node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
        {
            throw new ExtractionException($"{method.Identifier.ValueText} may not hide admitted lifecycle effects in nested functions.");
        }
    }

    private static void RejectErrors(SourceFile source)
    {
        foreach (Diagnostic diagnostic in source.Tree.GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                throw new ExtractionException($"{source.RelativePath} does not parse as C# 14: {diagnostic.GetMessage()}");
            }
        }
    }

    private static bool HasModifier(SyntaxTokenList modifiers, SyntaxKind kind)
    {
        foreach (SyntaxToken modifier in modifiers)
        {
            if (modifier.IsKind(kind)) return true;
        }
        return false;
    }

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name)
    {
        foreach (AttributeListSyntax list in lists)
        foreach (AttributeSyntax attribute in list.Attributes)
        {
            if (Canonical(attribute.Name) == name) return true;
        }
        return false;
    }

    private static string NamespaceOf(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current is not null)
        {
            if (current is BaseNamespaceDeclarationSyntax declaration)
            {
                return declaration.Name.ToString();
            }
            current = current.Parent;
        }
        return string.Empty;
    }

    private static void RequireContains(string actual, string expected, string message)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new ExtractionException(message);
        }
    }

    private static string Canonical(SyntaxNode node)
    {
        string text = node.WithoutTrivia().ToFullString();
        StringBuilder builder = new(text.Length);
        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character)) builder.Append(character);
        }
        return builder.ToString();
    }

    private static SourceFile Read(string root, string relativePath, string label)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureWithin(root, path);
        if (!File.Exists(path))
        {
            throw new ExtractionException($"Missing {label} source: {relativePath}");
        }
        byte[] bytes = File.ReadAllBytes(path);
        SourceText text = SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, relativePath);
        return new SourceFile(relativePath, Sha256(bytes), text, tree, tree.GetCompilationUnitRoot());
    }

    private static SourceIdentity[] ToSourceIdentities(SourceFile[] sources)
    {
        SourceIdentity[] identities = new SourceIdentity[sources.Length];
        for (int i = 0; i < sources.Length; i++)
        {
            identities[i] = new SourceIdentity(sources[i].RelativePath, sources[i].Hash);
        }
        return identities;
    }

    private static byte[] Serialize<T>(T value)
    {
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        string normalized = Encoding.UTF8.GetString(serialized)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n') + "\n";
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(normalized);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string CombinedHash(IEnumerable<SourceIdentity> sources)
    {
        StringBuilder builder = new();
        foreach (SourceIdentity source in sources)
            builder.Append(source.Path).Append('\n').Append(source.Sha256).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void EnsureWithin(string root, string path)
    {
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string canonicalPath = Path.GetFullPath(path);
        if (!canonicalPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException($"Output or source path escapes the admitted root: {canonicalPath}");
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed record SourceFile(
        string RelativePath,
        string Hash,
        SourceText Text,
        SyntaxTree Tree,
        CompilationUnitSyntax Root);

    private sealed record IrDocument(
        int SchemaVersion,
        string ExtractorVersion,
        string AncestorBaselineCommit,
        string SourceIdentityAuthority,
        string Kernel,
        OptionsShape Options,
        ReachabilityShape Reachability,
        LifecycleShape Lifecycle,
        string[] ExternalObligations);

    private sealed record OptionsShape(int None, int Commit, int Restore, int SkipValidation, int Warmup, int BuildUp);

    private sealed record ReachabilityShape(
        string Service,
        string ConcreteProcessor,
        string EthereumBase,
        string ClosedGenericBase,
        StageAnchor Registration);

    private sealed record LifecycleShape(
        string RestoreFormula,
        string CommitFormula,
        string CommitBeforeFormula,
        StageAnchor[] AdmissionStages,
        string[] RejectionGates,
        string[] RestoreOnRejectionGates,
        StageAnchor[] SimpleTransferTail,
        StageAnchor[] EvmTail,
        PartialOrderEdge[] EvmCallPartialOrder,
        MethodFingerprint[] MethodFingerprints,
        FinalizationShape Finalization);

    private sealed record FinalizationShape(
        string WarmupWriteGate,
        string[] BranchPriority,
        string[] RestoreActions,
        string BuildUpReapGate,
        string ReceiptGate,
        string ReceiptStateRootGate,
        string FailureOutputGate,
        string[] FailureErrorPrecedence,
        string ResultGate,
        StageAnchor[] Anchors);

    private sealed record StageAnchor(
        string Stage,
        string Method,
        string CanonicalSyntax,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn);

    private sealed record PartialOrderEdge(StageAnchor Before, StageAnchor After);

    private sealed record MethodFingerprint(string Key, string Sha256);

    private sealed record SourceIdentity(string Path, string Sha256);

    private sealed record ArtifactIdentity(string Path, string Sha256);

    private sealed record AdmissionIdentity(string Key, string SourceSha256);

    private sealed record Manifest(
        int SchemaVersion,
        string ExtractorVersion,
        string CompilerVersion,
        string LanguageVersion,
        string AncestorBaselineCommit,
        string SourceIdentityAuthority,
        string Kernel,
        SourceIdentity[] Sources,
        AdmissionIdentity[] Admissions,
        ArtifactIdentity Ir,
        ArtifactIdentity Lean,
        string CombinedSha256,
        string[] SemanticBindings);
}
