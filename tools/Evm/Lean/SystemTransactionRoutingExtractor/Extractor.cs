// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.Lean.SystemTransactionRoutingExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(string IrPath, string ManifestPath, string LeanPath, int SourceCount);

internal static class Extractor
{
    internal const string AncestorBaselineCommit = "b2478235e71e6a7ec2a509aa0155e25d5fdfff80";
    internal const string SourceIdentityAuthority =
        "The source-manifest SHA-256 identities are authoritative for the admitted current source; the ancestor baseline records lineage only.";
    internal const string KernelPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs";
    internal const string OptionsPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs";
    internal const string TransactionProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal const string SystemProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs";
    internal const string TransactionExtensionsPath =
        "src/Nethermind/Nethermind.Core/TransactionExtensions.cs";
    internal const string MainnetDiPath =
        "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string ContainerRegistrationPath =
        "src/Nethermind/Nethermind.Core/ContainerBuilderExtensions.cs";

    private const string KernelMetadataName =
        "Nethermind.Evm.TransactionProcessing.SystemTransactionRoutingKernel";
    private const string DefaultLeanPath =
        "tools/Evm/Lean/Eip803x/Generated/SystemTransactionRoutingKernel.lean";
    private const string IrFileName = "SystemTransactionRoutingKernel.ir.json";
    private const string ManifestFileName = "SystemTransactionRoutingKernel.source-manifest.json";
    private const string ExtractorVersion = "1.8.0";

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
        "TransactionExtensions.IsSystem -> SystemTransactionRoutingKernel.UseSystemProcessor -> SystemTransactionProcessor.Execute",
        "SystemTransactionProcessor.Execute -> ShouldPayOriginalValue/GetSystemExecutionOptions -> TransactionProcessorBase.Execute",
        "SystemTransactionProcessor.PayValue -> assigned _payOriginalValue field -> TransactionProcessorBase.PayValue",
        "semantically bound route/classifier/PayValue calls -> no CFG-before-target ordinary by-value tx/spec reference escape",
        "UpdateHeaderGasUsedAndPayFees gate decision only -> ParticipatesInNormalBlockCounters; virtual PayFees effects are outside the claim",
        "BlockProcessingModule.AddScoped<ITransactionProcessor, EthereumTransactionProcessor>",
        "Nethermind.Core.ContainerBuilderExtensions.AddScoped<T, TImpl> on Autofac.ContainerBuilder",
        "Nethermind.Core.ContainerBuilderExtensions.AddScoped<T, TImpl> -> BindScoped<T, TImpl> -> Autofac scoped externally-owned service binding",
        "EthereumTransactionProcessor -> inherited TransactionProcessorBase<EthereumGasPolicy>.CreateSystemTransactionProcessor",
    ];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string canonicalRoot = Path.GetFullPath(repoRoot);
        SourceFile[] sources =
        [
            Read(canonicalRoot, KernelPath, "routing kernel"),
            Read(canonicalRoot, OptionsPath, "execution options"),
            Read(canonicalRoot, TransactionProcessorPath, "transaction processor route"),
            Read(canonicalRoot, SystemProcessorPath, "system transaction option adapter"),
            Read(canonicalRoot, TransactionExtensionsPath, "system transaction classifier"),
            Read(canonicalRoot, MainnetDiPath, "standard mainnet transaction-processor registration"),
            Read(canonicalRoot, ContainerRegistrationPath, "production scoped-registration DSL"),
        ];

        foreach (SourceFile source in sources)
        {
            RejectVariantSyntax(source);
        }

        SourceFile kernel = sources[0];
        SourceFile options = sources[1];
        SourceFile transactionProcessor = sources[2];
        SourceFile systemProcessor = sources[3];
        SourceFile transactionExtensions = sources[4];
        SourceFile mainnetDi = sources[5];
        SourceFile containerRegistration = sources[6];

        KernelShape kernelShape = ValidateKernel(kernel.Root, options.Root);
        AdapterShape adapterShape = ValidateAdapters(
            kernel,
            options,
            transactionProcessor,
            systemProcessor,
            transactionExtensions,
            mainnetDi,
            containerRegistration);

        IrDocument document = new(
            SchemaVersion: 1,
            ExtractorVersion,
            AncestorBaselineCommit,
            SourceIdentityAuthority,
            KernelMetadataName,
            kernelShape,
        adapterShape);
        ValidateIrShape(document);
        byte[] irBytes = Serialize(document);
        string irHash = Sha256(irBytes);
        IrDocument serializedDocument = DeserializeIr(irBytes);
        ValidateRoundTrippedIr(document, serializedDocument);
        byte[] leanBytes = EmitLean(serializedDocument, irHash);

        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(canonicalRoot, DefaultLeanPath));
        EnsureWithin(leanOutputPath is null ? canonicalRoot : output, leanPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        Write(irPath, irBytes);
        Write(leanPath, leanBytes);

        SourceIdentity[] sourceIdentities = sources
            .Select(static source => new SourceIdentity(source.RelativePath, source.Hash))
            .ToArray();
        Manifest manifest = new(
            SchemaVersion: 1,
            ExtractorVersion,
            CompilerVersion: typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion: LanguageVersion.CSharp14.ToDisplayString(),
            AncestorBaselineCommit,
            SourceIdentityAuthority,
            KernelMetadataName,
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
            throw new ExtractionException("The serialized routing IR input was null.");
        }

        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized routing IR was empty.");
            ValidateIrShape(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized routing IR is not valid JSON: {exception.Message}");
        }
    }

    private static Manifest DeserializeManifest(byte[] bytes)
    {
        if (bytes is null)
        {
            throw new ExtractionException("The serialized routing manifest input was null.");
        }

        try
        {
            Manifest manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized routing manifest was empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized routing manifest is not valid JSON: {exception.Message}");
        }
    }

    private static void ValidateManifest(Manifest manifest)
    {
        if (manifest is null)
        {
            throw new ExtractionException("The serialized routing manifest was null.");
        }
        RequireText(manifest.ExtractorVersion, "Manifest.ExtractorVersion");
        RequireText(manifest.CompilerVersion, "Manifest.CompilerVersion");
        RequireText(manifest.LanguageVersion, "Manifest.LanguageVersion");
        RequireText(manifest.AncestorBaselineCommit, "Manifest.AncestorBaselineCommit");
        RequireText(manifest.SourceIdentityAuthority, "Manifest.SourceIdentityAuthority");
        RequireText(manifest.Kernel, "Manifest.Kernel");
        if (manifest.SchemaVersion != 1 || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.LanguageVersion != LanguageVersion.CSharp14.ToDisplayString() ||
            manifest.AncestorBaselineCommit != AncestorBaselineCommit ||
            manifest.SourceIdentityAuthority != SourceIdentityAuthority || manifest.Kernel != KernelMetadataName)
            throw new ExtractionException("The serialized routing manifest header or build identity changed.");
        if (manifest.Sources is null || manifest.Admissions is null || manifest.Ir is null || manifest.Lean is null ||
            manifest.SemanticBindings is null || manifest.Sources.Any(static source => source is null) ||
            manifest.Admissions.Any(static admission => admission is null) ||
            manifest.SemanticBindings.Any(static binding => binding is null))
            throw new ExtractionException("The serialized routing manifest contains null collections or entries.");

        HashSet<string> sourcePaths = new(StringComparer.Ordinal);
        foreach (SourceIdentity source in manifest.Sources)
        {
            RequireText(source.Path, "Manifest.Sources.Path");
            RequireSha256(source.Sha256, "Manifest.Sources.Sha256");
            if (!sourcePaths.Add(source.Path))
                throw new ExtractionException($"Duplicate routing source identity {source.Path}.");
        }
        ValidateAdmissions(manifest.Admissions, manifest.Sources);
        ValidateArtifact(manifest.Ir, IrFileName, "Manifest.Ir");
        ValidateArtifact(manifest.Lean, Normalize(DefaultLeanPath), "Manifest.Lean");
        RequireSha256(manifest.CombinedSha256, "Manifest.CombinedSha256");
        if (manifest.CombinedSha256 != CombinedHash(manifest.Sources))
            throw new ExtractionException("The serialized routing manifest combined source fingerprint changed.");
        if (!manifest.SemanticBindings.SequenceEqual(ExpectedSemanticBindings, StringComparer.Ordinal))
            throw new ExtractionException("The serialized routing manifest semantic bindings changed.");
    }

    private static void ValidateRoundTrippedManifest(Manifest sourceDerived, Manifest roundTripped)
    {
        ValidateManifest(sourceDerived);
        ValidateManifest(roundTripped);
        if (!Serialize(sourceDerived).AsSpan().SequenceEqual(Serialize(roundTripped)))
            throw new ExtractionException("The serialized routing manifest does not exactly match source-derived lineage.");
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
                throw new ExtractionException($"Routing admission identity is not path/owner-qualified: {admission.Key}.");
            if (!keys.Add(admission.Key))
                throw new ExtractionException($"Duplicate owner-qualified routing admission identity {admission.Key}.");
        }
        AdmissionIdentity[] expected = BuildAdmissions(sources);
        if (!admissions.SequenceEqual(expected))
            throw new ExtractionException("The serialized routing admission identities do not match the source identities.");
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
            throw new ExtractionException("The serialized routing IR document was null.");
        }

        RequireText(document.ExtractorVersion, "ExtractorVersion");
        RequireText(document.AncestorBaselineCommit, "AncestorBaselineCommit");
        RequireText(document.SourceIdentityAuthority, "SourceIdentityAuthority");
        RequireText(document.Kernel, "Kernel");
        ValidateProgramShape(document.Program);
        ValidateAdapterShape(document.Adapter);
    }

    private static void ValidateProgramShape(KernelShape shape)
    {
        if (shape is null)
        {
            throw new ExtractionException("The serialized routing IR Program was null.");
        }

        RequireText(shape.UseSystemProcessor, "Program.UseSystemProcessor");
        RequireText(shape.ShouldPayOriginalValue, "Program.ShouldPayOriginalValue");
        RequireText(shape.GetSystemExecutionOptions, "Program.GetSystemExecutionOptions");
        RequireText(shape.ParticipatesInNormalBlockCounters, "Program.ParticipatesInNormalBlockCounters");
    }

    private static void ValidateAdapterShape(AdapterShape shape)
    {
        if (shape is null)
        {
            throw new ExtractionException("The serialized routing IR Adapter was null.");
        }

        RequireText(shape.Classifier, "Adapter.Classifier");
        RequireText(shape.Route, "Adapter.Route");
        RequireText(shape.SystemFactory, "Adapter.SystemFactory");
        RequireText(shape.SystemOverride, "Adapter.SystemOverride");
        RequireText(shape.CounterGate, "Adapter.CounterGate");
        RequireText(shape.MainnetProcessor, "Adapter.MainnetProcessor");
        RequireText(shape.MainnetRegistration, "Adapter.MainnetRegistration");
        RequireText(shape.CounterClaim, "Adapter.CounterClaim");
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
        ValidateProgramBinding(sourceDerived.Program, roundTripped.Program);
        ValidateAdapterBinding(sourceDerived.Adapter, roundTripped.Adapter);
    }

    private static void ValidateProgramBinding(KernelShape sourceDerived, KernelShape roundTripped)
    {
        RequireEqual("Program.None", sourceDerived.None, roundTripped.None);
        RequireEqual("Program.Commit", sourceDerived.Commit, roundTripped.Commit);
        RequireEqual("Program.Restore", sourceDerived.Restore, roundTripped.Restore);
        RequireEqual("Program.SkipValidation", sourceDerived.SkipValidation, roundTripped.SkipValidation);
        RequireEqual("Program.Warmup", sourceDerived.Warmup, roundTripped.Warmup);
        RequireEqual("Program.BuildUp", sourceDerived.BuildUp, roundTripped.BuildUp);
        RequireEqual("Program.UseSystemProcessor", sourceDerived.UseSystemProcessor, roundTripped.UseSystemProcessor);
        RequireEqual("Program.ShouldPayOriginalValue", sourceDerived.ShouldPayOriginalValue, roundTripped.ShouldPayOriginalValue);
        RequireEqual("Program.GetSystemExecutionOptions", sourceDerived.GetSystemExecutionOptions, roundTripped.GetSystemExecutionOptions);
        RequireEqual("Program.ParticipatesInNormalBlockCounters", sourceDerived.ParticipatesInNormalBlockCounters,
            roundTripped.ParticipatesInNormalBlockCounters);
    }

    private static void ValidateAdapterBinding(AdapterShape sourceDerived, AdapterShape roundTripped)
    {
        RequireEqual("Adapter.Classifier", sourceDerived.Classifier, roundTripped.Classifier);
        RequireEqual("Adapter.Route", sourceDerived.Route, roundTripped.Route);
        RequireEqual("Adapter.SystemFactory", sourceDerived.SystemFactory, roundTripped.SystemFactory);
        RequireEqual("Adapter.SystemOverride", sourceDerived.SystemOverride, roundTripped.SystemOverride);
        RequireEqual("Adapter.CounterGate", sourceDerived.CounterGate, roundTripped.CounterGate);
        RequireEqual("Adapter.MainnetProcessor", sourceDerived.MainnetProcessor, roundTripped.MainnetProcessor);
        RequireEqual("Adapter.MainnetRegistration", sourceDerived.MainnetRegistration, roundTripped.MainnetRegistration);
        RequireEqual("Adapter.IsSystemClassifierBound", sourceDerived.IsSystemClassifierBound, roundTripped.IsSystemClassifierBound);
        RequireEqual("Adapter.KernelCallsBound", sourceDerived.KernelCallsBound, roundTripped.KernelCallsBound);
        RequireEqual("Adapter.SystemExecuteOverridesBase", sourceDerived.SystemExecuteOverridesBase, roundTripped.SystemExecuteOverridesBase);
        RequireEqual("Adapter.StandardProcessorInheritsDefaultFactory", sourceDerived.StandardProcessorInheritsDefaultFactory,
            roundTripped.StandardProcessorInheritsDefaultFactory);
        RequireEqual("Adapter.MainnetRegistrationBound", sourceDerived.MainnetRegistrationBound, roundTripped.MainnetRegistrationBound);
        RequireEqual("Adapter.OptionsForwardedToCounterGate", sourceDerived.OptionsForwardedToCounterGate,
            roundTripped.OptionsForwardedToCounterGate);
        RequireEqual("Adapter.PayValueUsesAssignedField", sourceDerived.PayValueUsesAssignedField, roundTripped.PayValueUsesAssignedField);
        RequireEqual("Adapter.PayValueOverridesBase", sourceDerived.PayValueOverridesBase, roundTripped.PayValueOverridesBase);
        RequireEqual("Adapter.VirtualPayValueCallsBound", sourceDerived.VirtualPayValueCallsBound,
            roundTripped.VirtualPayValueCallsBound);
        RequireEqual("Adapter.ScopedBindingImplementationBound", sourceDerived.ScopedBindingImplementationBound,
            roundTripped.ScopedBindingImplementationBound);
        RequireEqual("Adapter.RoutedSystemForwardingMustReach", sourceDerived.RoutedSystemForwardingMustReach,
            roundTripped.RoutedSystemForwardingMustReach);
        RequireEqual("Adapter.AdmittedArgumentBindingsRemainDirect", sourceDerived.AdmittedArgumentBindingsRemainDirect,
            roundTripped.AdmittedArgumentBindingsRemainDirect);
        RequireEqual("Adapter.BoundReferenceArgumentsFailClosed", sourceDerived.BoundReferenceArgumentsFailClosed,
            roundTripped.BoundReferenceArgumentsFailClosed);
        RequireEqual("Adapter.MainnetRegistrationDominatesLoadExit", sourceDerived.MainnetRegistrationDominatesLoadExit,
            roundTripped.MainnetRegistrationDominatesLoadExit);
        RequireEqual("Adapter.CounterClaim", sourceDerived.CounterClaim, roundTripped.CounterClaim);
    }

    private static void RequireText(string? value, string field)
    {
        if (value is null)
        {
            throw new ExtractionException($"The serialized routing IR {field} was null.");
        }
    }

    private static void RequireEqual<T>(string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new ExtractionException($"The round-tripped routing IR {field} changed from its source-derived value.");
        }
    }

    private static KernelShape ValidateKernel(CompilationUnitSyntax root, CompilationUnitSyntax optionsRoot)
    {
        EnumDeclarationSyntax options = RequireSingle(
            optionsRoot.DescendantNodes().OfType<EnumDeclarationSyntax>()
                .Where(static item => item.Identifier.ValueText == "ExecutionOptions"),
            "ExecutionOptions must be declared exactly once.");
        if (NamespaceOf(options) != "Nethermind.Evm.TransactionProcessing" ||
            !options.AttributeLists.SelectMany(static list => list.Attributes)
                .Any(static attribute => Canonical(attribute.Name) == "Flags"))
        {
            throw new ExtractionException("ExecutionOptions must remain the pinned flags enum.");
        }

        Dictionary<string, string> expectedMembers = new(StringComparer.Ordinal)
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
        if (options.Members.Count != expectedMembers.Count || options.Members.Any(member =>
                member.EqualsValue is null ||
                !expectedMembers.TryGetValue(member.Identifier.ValueText, out string? expected) ||
                Canonical(member.EqualsValue.Value) != expected))
        {
            throw new ExtractionException("ExecutionOptions values changed from the pinned flag layout.");
        }

        ClassDeclarationSyntax type = RequireSingle(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static item => item.Identifier.ValueText == "SystemTransactionRoutingKernel"),
            "SystemTransactionRoutingKernel must be declared exactly once.");
        if (NamespaceOf(type) != "Nethermind.Evm.TransactionProcessing" ||
            !type.Modifiers.Any(SyntaxKind.InternalKeyword) ||
            !type.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            type.Members.Count != 4)
        {
            throw new ExtractionException("SystemTransactionRoutingKernel changed its closed internal static shape.");
        }

        MethodDeclarationSyntax route = RequireMethod(type, "UseSystemProcessor");
        MethodDeclarationSyntax payValue = RequireMethod(type, "ShouldPayOriginalValue");
        MethodDeclarationSyntax effective = RequireMethod(type, "GetSystemExecutionOptions");
        MethodDeclarationSyntax counters = RequireMethod(type, "ParticipatesInNormalBlockCounters");
        RequireMethodShape(route, "bool", ["boolisSystemTransaction", "ExecutionOptionsoptions"]);
        RequireMethodShape(payValue, "bool", ["ExecutionOptionsoptions"]);
        RequireMethodShape(effective, "ExecutionOptions", ["ExecutionOptionsoptions", "boolpayOriginalValue"]);
        RequireMethodShape(counters, "bool", ["ExecutionOptionsoptions", "boolparallel"]);

        if (route.ExpressionBody is not { Expression: ExpressionSyntax routeExpression } ||
            Canonical(routeExpression) != "isSystemTransaction||options==ExecutionOptions.SkipValidation" ||
            payValue.Body is not
            {
                Statements:
                [
                    LocalDeclarationStatementSyntax coreOptions,
                    ReturnStatementSyntax { Expression: ExpressionSyntax payExpression },
                ],
            } ||
            Canonical(coreOptions) != "ExecutionOptionscoreOptions=options&~ExecutionOptions.Warmup;" ||
            Canonical(payExpression) !=
            "(coreOptions&ExecutionOptions.SkipValidation)!=ExecutionOptions.SkipValidation&&(coreOptions&ExecutionOptions.SkipValidationAndCommit)!=ExecutionOptions.SkipValidationAndCommit" ||
            effective.ExpressionBody is not { Expression: ExpressionSyntax effectiveExpression } ||
            Canonical(effectiveExpression) !=
            "payOriginalValue?options|ExecutionOptions.SkipValidationAndCommit:options" ||
            counters.ExpressionBody is not { Expression: ExpressionSyntax counterExpression } ||
            Canonical(counterExpression) !=
            "(options&ExecutionOptions.SkipValidation)!=ExecutionOptions.SkipValidation&&!parallel")
        {
            throw new ExtractionException("System transaction routing kernel semantics changed from the pinned form.");
        }

        return new KernelShape(
            None: 0,
            Commit: 1,
            Restore: 2,
            SkipValidation: 4,
            Warmup: 8,
            BuildUp: 16,
            UseSystemProcessor: Canonical(routeExpression),
            ShouldPayOriginalValue: Canonical(payExpression),
            GetSystemExecutionOptions: Canonical(effectiveExpression),
            ParticipatesInNormalBlockCounters: Canonical(counterExpression));
    }

    private static AdapterShape ValidateAdapters(
        SourceFile kernel,
        SourceFile options,
        SourceFile transactionProcessor,
        SourceFile systemProcessor,
        SourceFile transactionExtensions,
        SourceFile mainnetDi,
        SourceFile containerRegistration)
    {
        ClassDeclarationSyntax processorBase = RequireSingle(
            transactionProcessor.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static item => item.Identifier.ValueText == "TransactionProcessorBase" &&
                    item.TypeParameterList?.Parameters.Count == 1),
            "Generic TransactionProcessorBase must be declared exactly once.");
        RejectIdentifierShadows(
            transactionProcessor.Root,
            ["SystemTransactionRoutingKernel", "ExecutionOptions", "SystemTransactionProcessor"]);
        RejectIdentifierShadows(
            systemProcessor.Root,
            ["SystemTransactionRoutingKernel", "ExecutionOptions"]);
        RejectTypeShadows(mainnetDi.Root, ["ITransactionProcessor", "EthereumTransactionProcessor"]);
        RejectTypeShadows(transactionExtensions.Root, ["Address", "SystemTransaction"]);
        MethodDeclarationSyntax executeCore = RequireMethod(processorBase, "ExecuteCore");
        if (executeCore.Body is not
            {
                Statements:
                [
                    IfStatementSyntax traceStart,
                    IfStatementSyntax,
                    LocalDeclarationStatementSyntax resultDeclaration,
                    IfStatementSyntax traceEnd,
                    ReturnStatementSyntax normalReturn,
                ],
            } ||
            Canonical(traceStart) != "if(Logger.IsTrace)Logger.Trace($\"Executing tx {tx.Hash}\");" ||
            Canonical(traceEnd) != "if(Logger.IsTrace)Logger.Trace($\"Tx {tx.Hash} was executed, {result}\");" ||
            Canonical(resultDeclaration) != "TransactionResultresult=Execute(tx,tracer,opts);" ||
            Canonical(normalReturn) != "returnresult;")
        {
            throw new ExtractionException("ExecuteCore must retain its complete reachable dispatch body.");
        }
        IfStatementSyntax routeIf = RequireSingle(
            executeCore.Body.Statements.OfType<IfStatementSyntax>()
                .Where(static item => Canonical(item.Condition).Contains("SystemTransactionRoutingKernel", StringComparison.Ordinal)),
            "ExecuteCore must have exactly one kernel-controlled system route.");
        if (Canonical(routeIf.Condition) !=
            "SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(),opts)" ||
            !ReferenceEquals(routeIf.Parent, executeCore.Body) ||
            routeIf.Else is not null ||
            routeIf.Statement is not BlockSyntax
            {
                Statements:
                [ReturnStatementSyntax { Expression: ExpressionSyntax systemReturn }],
            } ||
            Canonical(systemReturn) != "GetOrCreateSystemTransactionProcessor().Execute(tx,tracer,opts)")
        {
            throw new ExtractionException(
                "ExecuteCore system route must be a direct, else-free branch with one forwarding return.");
        }

        MethodDeclarationSyntax createSystem = RequireMethod(processorBase, "CreateSystemTransactionProcessor");
        if (!createSystem.Modifiers.Any(SyntaxKind.ProtectedKeyword) ||
            !createSystem.Modifiers.Any(SyntaxKind.VirtualKeyword) ||
            Canonical(createSystem.ReturnType) != "SystemTransactionProcessor<TGasPolicy>" ||
            createSystem.ExpressionBody is not { Expression: ImplicitObjectCreationExpressionSyntax creation } ||
            Canonical(creation.ArgumentList) !=
            "(_blobBaseFeeCalculator,SpecProvider,WorldState,VirtualMachine,_codeInfoRepository,_logManager)")
        {
            throw new ExtractionException("The standard system-processor factory or its override surface changed.");
        }

        MethodDeclarationSyntax getSystem = RequireMethod(processorBase, "GetOrCreateSystemTransactionProcessor");
        if (!getSystem.Modifiers.Any(SyntaxKind.PrivateKeyword) ||
            Canonical(getSystem.ReturnType) != "SystemTransactionProcessor<TGasPolicy>" ||
            getSystem.Body is not
            {
                Statements:
                [
                    IfStatementSyntax
                {
                    Condition: ExpressionSyntax missingSystem,
                    Statement: BlockSyntax
                    {
                        Statements: [ExpressionStatementSyntax compareExchange],
                    },
                },
                    ReturnStatementSyntax { Expression: ExpressionSyntax cachedSystem },
                ],
            } ||
            Canonical(missingSystem) != "_systemTransactionProcessorisnull" ||
            Canonical(compareExchange) !=
            "Interlocked.CompareExchange(ref_systemTransactionProcessor,CreateSystemTransactionProcessor(),null);" ||
            Canonical(cachedSystem) != "_systemTransactionProcessor")
        {
            throw new ExtractionException("The cached system-processor factory route changed.");
        }

        ClassDeclarationSyntax ethereumProcessor = RequireSingle(
            transactionProcessor.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static item => item.Identifier.ValueText == "EthereumTransactionProcessor"),
            "EthereumTransactionProcessor must be declared exactly once.");
        if (!ethereumProcessor.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !ethereumProcessor.Modifiers.Any(SyntaxKind.SealedKeyword) ||
            ethereumProcessor.BaseList?.Types is not [BaseTypeSyntax ethereumBase] ||
            Canonical(ethereumBase.Type) != "EthereumTransactionProcessorBase" ||
            ethereumProcessor.Members.Count != 0)
        {
            throw new ExtractionException("The standard EthereumTransactionProcessor inheritance route changed.");
        }
        ClassDeclarationSyntax ethereumProcessorBase = RequireSingle(
            transactionProcessor.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static item => item.Identifier.ValueText == "EthereumTransactionProcessorBase"),
            "EthereumTransactionProcessorBase must be declared exactly once.");
        if (!ethereumProcessorBase.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !ethereumProcessorBase.Modifiers.Any(SyntaxKind.AbstractKeyword) ||
            ethereumProcessorBase.BaseList?.Types is not [BaseTypeSyntax genericProcessorBase] ||
            Canonical(genericProcessorBase.Type) != "TransactionProcessorBase<EthereumGasPolicy>" ||
            ethereumProcessorBase.Members.Count != 0)
        {
            throw new ExtractionException("The standard Ethereum gas-policy processor inheritance route changed.");
        }

        ClassDeclarationSyntax systemType = RequireSingle(
            systemProcessor.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static item => item.Identifier.ValueText == "SystemTransactionProcessor"),
            "SystemTransactionProcessor must be declared exactly once.");
        MethodDeclarationSyntax systemExecute = RequireMethod(systemType, "Execute");
        if (!systemExecute.Modifiers.Any(SyntaxKind.ProtectedKeyword) ||
            !systemExecute.Modifiers.Any(SyntaxKind.OverrideKeyword) ||
            systemExecute.Body is not
            {
                Statements:
                [
                    LocalDeclarationStatementSyntax systemAccountReadSuppression,
                    ExpressionStatementSyntax beforeSystemTransaction,
                    ExpressionStatementSyntax payAssignment,
                    ReturnStatementSyntax systemExecuteReturn,
                ],
            } ||
            Canonical(systemAccountReadSuppression) !=
            "usingIDisposable?systemAccountReadSuppression=ShouldSuppressSystemAccountReads(tx)?WorldState.BeginSystemAccountReadSuppression():null;" ||
            Canonical(beforeSystemTransaction) != "OnBeforeSystemTransaction();")
        {
            throw new ExtractionException("SystemTransactionProcessor.Execute must retain its complete reachable override body.");
        }

        if (Canonical(payAssignment) !=
            "_payOriginalValue=SystemTransactionRoutingKernel.ShouldPayOriginalValue(opts);" ||
            systemExecuteReturn.Expression is null ||
            Canonical(systemExecuteReturn.Expression) !=
            "base.Execute(tx,tracer,SystemTransactionRoutingKernel.GetSystemExecutionOptions(opts,_payOriginalValue))" ||
            systemExecute.Body.Statements.Count != 4 ||
            systemExecute.Body.Statements.IndexOf(payAssignment) != 2 ||
            systemExecute.Body.Statements.IndexOf(systemExecuteReturn) != 3)
        {
            throw new ExtractionException("SystemTransactionProcessor.Execute option projection changed.");
        }
        MethodDeclarationSyntax payValue = RequireMethod(systemType, "PayValue");
        VariableDeclaratorSyntax payOriginalValueField = RequireSingle(
            systemType.Members.OfType<FieldDeclarationSyntax>()
                .SelectMany(static declaration => declaration.Declaration.Variables)
                .Where(static variable => variable.Identifier.ValueText == "_payOriginalValue"),
            "SystemTransactionProcessor._payOriginalValue must be declared exactly once as a field.");
        FieldDeclarationSyntax payOriginalValueDeclaration =
            payOriginalValueField.FirstAncestorOrSelf<FieldDeclarationSyntax>()!;
        if (Canonical(payOriginalValueDeclaration) != "privatebool_payOriginalValue;" ||
            !payValue.Modifiers.Any(SyntaxKind.ProtectedKeyword) ||
            !payValue.Modifiers.Any(SyntaxKind.OverrideKeyword) ||
            Canonical(payValue.ReturnType) != "void" ||
            !payValue.ParameterList.Parameters.Select(Canonical).SequenceEqual(
                ["Transactiontx", "IReleaseSpecspec", "ExecutionOptionsopts"],
                StringComparer.Ordinal) ||
            payValue.Body is not
            {
                Statements:
                [
                    IfStatementSyntax
                {
                    Condition: ExpressionSyntax payCondition,
                    Statement: BlockSyntax
                    {
                        Statements: [ExpressionStatementSyntax basePayValue],
                    },
                },
                ],
            } ||
            Canonical(payCondition) != "_payOriginalValue" ||
            Canonical(basePayValue) != "base.PayValue(tx,spec,opts);")
        {
            throw new ExtractionException("SystemTransactionProcessor.PayValue no longer consumes the pinned decision.");
        }

        MethodDeclarationSyntax counterMethod = RequireMethod(processorBase, "UpdateHeaderGasUsedAndPayFees");
        IfStatementSyntax counterIf = RequireSingle(
            counterMethod.Body?.Statements.OfType<IfStatementSyntax>() ?? [],
            "UpdateHeaderGasUsedAndPayFees must have exactly one outer counter gate.");
        if (counterMethod.Body is not
            {
                Statements: [IfStatementSyntax outerCounterIf, ExpressionStatementSyntax],
            } ||
            !ReferenceEquals(counterIf, outerCounterIf))
        {
            throw new ExtractionException(
                "UpdateHeaderGasUsedAndPayFees must retain one direct Boolean counter gate and its post-gate fee call.");
        }
        if (Canonical(counterIf.Condition) !=
            "SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(opts,_parallel)")
        {
            throw new ExtractionException("Normal block counter participation is no longer controlled by the pinned kernel.");
        }
        ValidateOptionsReachCounterGate(processorBase, counterMethod);

        ClassDeclarationSyntax extensionsType = RequireSingle(
            transactionExtensions.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static item => item.Identifier.ValueText == "TransactionExtensions"),
            "TransactionExtensions must be declared exactly once.");
        MethodDeclarationSyntax isSystem = RequireSingle(
            extensionsType.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(static method => method.Identifier.ValueText == "IsSystem"),
            "TransactionExtensions.IsSystem must be declared exactly once.");
        if (isSystem.ExpressionBody is not { Expression: ExpressionSyntax isSystemExpression } ||
            Canonical(isSystemExpression) !=
            "txisSystemTransaction||tx.SenderAddress==Address.SystemUser||tx.IsOPSystemTransaction")
        {
            throw new ExtractionException("TransactionExtensions.IsSystem classifier changed from the pinned shape.");
        }

        ClassDeclarationSyntax module = RequireSingle(
            mainnetDi.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static item => item.Identifier.ValueText == "BlockProcessingModule"),
            "BlockProcessingModule must be declared exactly once.");
        MethodDeclarationSyntax load = RequireMethod(module, "Load");
        if (mainnetDi.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Any(static method => method.Identifier.ValueText == "AddScoped") ||
            mainnetDi.Root.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
                .Any(static method => method.Identifier.ValueText == "AddScoped"))
        {
            throw new ExtractionException("Standard mainnet DI source must not declare a competing AddScoped route.");
        }
        InvocationExpressionSyntax[] registrations = load.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(IsTransactionProcessorRegistration)
            .OrderBy(static invocation => invocation.SpanStart)
            .ToArray();
        InvocationExpressionSyntax registration = RequireSingle(
            registrations,
            "BlockProcessingModule.Load must contain exactly one ITransactionProcessor registration with no later override.");
        RequireCompleteLoadRegistrationGraph(load, registration, registrations);
        if (registration.Expression is not MemberAccessExpressionSyntax
            {
                Expression: ExpressionSyntax registrationReceiver,
                Name: GenericNameSyntax
                {
                    Identifier.ValueText: "AddScoped",
                    TypeArgumentList.Arguments:
                    [TypeSyntax serviceType, TypeSyntax implementationType],
                },
            } ||
            Canonical(serviceType) != "ITransactionProcessor" ||
            Canonical(implementationType) != "EthereumTransactionProcessor" ||
            registration.ArgumentList.Arguments.Count != 0)
        {
            throw new ExtractionException("Standard mainnet ITransactionProcessor DI route changed.");
        }
        if (!IsBuilderChain(registrationReceiver))
        {
            throw new ExtractionException("Standard mainnet DI receiver chain contains a non-admitted operation.");
        }
        if (!IsDirectBodyExpression(registration, load))
        {
            throw new ExtractionException("Standard mainnet ITransactionProcessor registration must be a live direct body statement.");
        }
        ScopedRegistrationShape scopedRegistration = ValidateScopedRegistration(containerRegistration.Root);

        ValidateSemanticBindings(
            kernel,
            options,
            transactionProcessor,
            systemProcessor,
            transactionExtensions,
            mainnetDi,
            routeIf,
            systemReturn,
            payAssignment,
            systemExecuteReturn,
            payValue,
            counterIf,
            isSystem,
            registration,
            scopedRegistration.AddScoped,
            scopedRegistration.BindScoped,
            containerRegistration,
            createSystem,
            ethereumBase.Type,
            payOriginalValueField);

        return new AdapterShape(
            Classifier: "TransactionExtensions.IsSystem",
            Route: "TransactionProcessorBase<TGasPolicy>.ExecuteCore",
            SystemFactory: "TransactionProcessorBase<TGasPolicy>.CreateSystemTransactionProcessor (virtual)",
            SystemOverride: "SystemTransactionProcessor<TGasPolicy>.Execute",
            CounterGate: "TransactionProcessorBase<TGasPolicy>.UpdateHeaderGasUsedAndPayFees normal counter-gate decision only",
            MainnetProcessor: "EthereumTransactionProcessor : EthereumTransactionProcessorBase",
            MainnetRegistration: ".AddScoped<ITransactionProcessor, EthereumTransactionProcessor>()",
            IsSystemClassifierBound: true,
            KernelCallsBound: true,
            SystemExecuteOverridesBase: true,
            StandardProcessorInheritsDefaultFactory: true,
            MainnetRegistrationBound: true,
            OptionsForwardedToCounterGate: true,
            PayValueUsesAssignedField: true,
            PayValueOverridesBase: true,
            VirtualPayValueCallsBound: true,
            ScopedBindingImplementationBound: true,
            RoutedSystemForwardingMustReach: true,
            AdmittedArgumentBindingsRemainDirect: true,
            BoundReferenceArgumentsFailClosed: true,
            MainnetRegistrationDominatesLoadExit: true,
            CounterClaim: "normal counter-gate decision only; virtual PayFees effects are outside the claim");
    }

    private static ScopedRegistrationShape ValidateScopedRegistration(CompilationUnitSyntax root)
    {
        ClassDeclarationSyntax extensions = RequireSingle(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(static type =>
                type.Identifier.ValueText == "ContainerBuilderExtensions" &&
                NamespaceOf(type) == "Nethermind.Core"),
            "Production ContainerBuilderExtensions must be declared exactly once.");
        MethodDeclarationSyntax addScoped = RequireSingle(
            extensions.Members.OfType<MethodDeclarationSyntax>().Where(static candidate =>
                candidate.Identifier.ValueText == "AddScoped" &&
                candidate.TypeParameterList?.Parameters.Count == 2 &&
                candidate.ParameterList.Parameters.Count == 1),
            "Production AddScoped<T, TImpl>(ContainerBuilder) must be declared exactly once.");
        if (!addScoped.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !addScoped.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            Canonical(addScoped.ReturnType) != "ContainerBuilder" ||
            Canonical(addScoped.TypeParameterList!) != "<T,TImpl>" ||
            Canonical(addScoped.ParameterList) != "(thisContainerBuilderbuilder)" ||
            addScoped.ConstraintClauses.Select(Canonical).ToArray() is not ["whereTImpl:T", "whereT:notnull"] ||
            addScoped.Body is null ||
            Canonical(addScoped.Body) !=
            "{builder.RegisterType<TImpl>().As<TImpl>().CommonNethermindConfig().InstancePerLifetimeScope();builder.BindScoped<T,TImpl>();returnbuilder;}")
        {
            throw new ExtractionException("Production AddScoped<T, TImpl> registration semantics changed.");
        }

        MethodDeclarationSyntax bindScoped = RequireSingle(
            extensions.Members.OfType<MethodDeclarationSyntax>().Where(static candidate =>
                candidate.Identifier.ValueText == "BindScoped" &&
                candidate.TypeParameterList?.Parameters.Count == 2 &&
                candidate.ParameterList.Parameters.Count == 1),
            "Production BindScoped<TTo, TFrom>(ContainerBuilder) must be declared exactly once.");
        if (!bindScoped.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !bindScoped.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            Canonical(bindScoped.ReturnType) != "ContainerBuilder" ||
            Canonical(bindScoped.TypeParameterList!) != "<TTo,TFrom>" ||
            Canonical(bindScoped.ParameterList) != "(thisContainerBuilderbuilder)" ||
            bindScoped.ConstraintClauses.Select(Canonical).ToArray() is not ["whereTFrom:TTo", "whereTTo:notnull"] ||
            bindScoped.Body is null ||
            Canonical(bindScoped.Body) !=
            "{builder.Register(static(it)=>it.Resolve<TFrom>()).As<TTo>().InstancePerLifetimeScope().ExternallyOwned();returnbuilder;}")
        {
            throw new ExtractionException("Production BindScoped<TTo, TFrom> registration semantics changed.");
        }

        return new ScopedRegistrationShape(addScoped, bindScoped);
    }

    private static void ValidateOptionsReachCounterGate(
        ClassDeclarationSyntax processorBase,
        MethodDeclarationSyntax counterMethod)
    {
        MethodDeclarationSyntax entryExecute = RequireSingle(
            processorBase.Members.OfType<MethodDeclarationSyntax>().Where(static method =>
                method.Identifier.ValueText == "Execute" &&
                method.Modifiers.Any(SyntaxKind.ProtectedKeyword) &&
                method.ParameterList.Parameters.Count == 3),
            "The three-argument virtual Execute entry must be declared exactly once.");
        MethodDeclarationSyntax execution = RequireSingle(
            processorBase.Members.OfType<MethodDeclarationSyntax>().Where(static method =>
                method.Identifier.ValueText == "Execute" &&
                method.Modifiers.Any(SyntaxKind.PrivateKeyword) &&
                method.ParameterList.Parameters.Count == 6),
            "The six-argument Execute implementation must be declared exactly once.");
        MethodDeclarationSyntax evm = RequireMethod(processorBase, "ExecuteEvmTransaction");
        MethodDeclarationSyntax simple = RequireMethod(processorBase, "ExecuteSimpleTransfer");

        RequireUniqueForwardedOption(entryExecute, "Execute", targetParameterCount: 6);
        RequireUniqueForwardedOption(execution, "ExecuteSimpleTransfer", targetParameterCount: 15);
        RequireUniqueForwardedOption(execution, "ExecuteEvmTransaction", targetParameterCount: 16);
        RequireUniqueForwardedOption(evm, "UpdateHeaderGasUsedAndPayFees", targetParameterCount: 11);
        RequireUniqueForwardedOption(simple, "UpdateHeaderGasUsedAndPayFees", targetParameterCount: 11);
        if (counterMethod.ParameterList.Parameters.Count != 11 ||
            counterMethod.ParameterList.Parameters[4].Identifier.ValueText != "opts")
        {
            throw new ExtractionException("The normal counter gate option parameter changed.");
        }
    }

    private static InvocationExpressionSyntax RequireUniqueForwardedOption(
        MethodDeclarationSyntax source,
        string targetName,
        int targetParameterCount)
    {
        if (source.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
            .Any(local => local.Identifier.ValueText == targetName))
        {
            throw new ExtractionException($"Option forwarding to {targetName} must not be shadowed by a local function.");
        }
        InvocationExpressionSyntax invocation = RequireSingle(
            source.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(invocation =>
                InvocationName(invocation) == targetName &&
                invocation.ArgumentList.Arguments.Count == targetParameterCount),
            $"The pinned option path must invoke {targetName} exactly once.");
        return invocation;
    }

    private static InvocationExpressionSyntax RequireForwardedOption(
        SemanticModel model,
        MethodDeclarationSyntax source,
        IMethodSymbol sourceSymbol,
        string targetName,
        int targetParameterCount,
        int argumentIndex)
    {
        InvocationExpressionSyntax invocation = RequireUniqueForwardedOption(source, targetName, targetParameterCount);
        RequireReachableSyntax(model, source, invocation, $"{sourceSymbol.Name} -> {targetName} option forwarding");
        IParameterSymbol optionsParameter = sourceSymbol.Parameters.SingleOrDefault(static parameter => parameter.Name == "opts")
            ?? throw new ExtractionException($"{sourceSymbol.Name} must declare an 'opts' parameter.");
        RequireParameterArgument(
            model,
            invocation,
            argumentIndex,
            optionsParameter,
            $"{sourceSymbol.Name} -> {targetName} option forwarding");
        RequireParameterArgument(
            model,
            invocation,
            0,
            RequireMethodParameter(sourceSymbol, "tx", $"{sourceSymbol.Name} -> {targetName} forwarding"),
            $"{sourceSymbol.Name} -> {targetName} transaction forwarding");
        if (sourceSymbol.Parameters.Any(static parameter => parameter.Name == "spec"))
        {
            int specArgumentIndex = targetName == "Execute" ? 4 : 2;
            RequireParameterArgument(
                model,
                invocation,
                specArgumentIndex,
                RequireMethodParameter(sourceSymbol, "spec", $"{sourceSymbol.Name} -> {targetName} forwarding"),
                $"{sourceSymbol.Name} -> {targetName} specification forwarding");
        }
        RequireUnchangedParametersBeforeInvocation(
            model,
            source,
            invocation,
            AdmittedParameters(sourceSymbol),
            $"{sourceSymbol.Name} -> {targetName} option forwarding",
            ReferenceEscapePolicy.DirectArgumentBindingsOnly);
        return invocation;
    }

    private static string? InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax { Name: SimpleNameSyntax name } => name.Identifier.ValueText,
        _ => null,
    };

    private static void RejectIdentifierShadows(CompilationUnitSyntax root, IReadOnlyList<string> names)
    {
        HashSet<string> selected = new(names, StringComparer.Ordinal);
        if (root.DescendantNodes().Any(node => node switch
            {
                VariableDeclaratorSyntax variable => selected.Contains(variable.Identifier.ValueText),
                ParameterSyntax parameter => selected.Contains(parameter.Identifier.ValueText),
                PropertyDeclarationSyntax property => selected.Contains(property.Identifier.ValueText),
                BaseTypeDeclarationSyntax type => selected.Contains(type.Identifier.ValueText),
                _ => false,
            }))
        {
            throw new ExtractionException("A pinned routing symbol is shadowed by an adapter declaration.");
        }
    }

    private static void RejectTypeShadows(CompilationUnitSyntax root, IReadOnlyList<string> names)
    {
        HashSet<string> selected = new(names, StringComparer.Ordinal);
        if (root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Any(type => selected.Contains(type.Identifier.ValueText)))
        {
            throw new ExtractionException("A pinned routing type is shadowed by an adapter declaration.");
        }
    }

    private static void ValidateSemanticBindings(
        SourceFile kernel,
        SourceFile options,
        SourceFile transactionProcessor,
        SourceFile systemProcessor,
        SourceFile transactionExtensions,
        SourceFile mainnetDi,
        IfStatementSyntax routeIf,
        ExpressionSyntax systemReturn,
        ExpressionStatementSyntax payAssignment,
        ReturnStatementSyntax systemExecuteReturn,
        MethodDeclarationSyntax systemPayValue,
        IfStatementSyntax counterIf,
        MethodDeclarationSyntax isSystem,
        InvocationExpressionSyntax registration,
        MethodDeclarationSyntax scopedRegistration,
        MethodDeclarationSyntax bindScoped,
        SourceFile containerRegistration,
        MethodDeclarationSyntax createSystem,
        TypeSyntax ethereumBase,
        VariableDeclaratorSyntax payOriginalValueField)
    {
        SourceFile[] admittedSources =
        [
            kernel,
            options,
            transactionProcessor,
            systemProcessor,
            transactionExtensions,
            mainnetDi,
            containerRegistration,
        ];
        CSharpCompilation compilation = CompileAdmittedSources(admittedSources);
        SemanticModel kernelModel = compilation.GetSemanticModel(kernel.Tree);
        SemanticModel transactionModel = compilation.GetSemanticModel(transactionProcessor.Tree);
        SemanticModel systemModel = compilation.GetSemanticModel(systemProcessor.Tree);
        SemanticModel classifierModel = compilation.GetSemanticModel(transactionExtensions.Tree);
        SemanticModel diModel = compilation.GetSemanticModel(mainnetDi.Tree);
        SemanticModel registrationModel = compilation.GetSemanticModel(containerRegistration.Tree);

        INamedTypeSymbol kernelSymbol = RequireSourceType(
            compilation,
            KernelMetadataName,
            kernel,
            "routing kernel");
        Dictionary<string, IMethodSymbol> kernelMethods = kernelSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .ToDictionary(static method => method.Name, StringComparer.Ordinal);
        if (kernelMethods.Count != 4)
        {
            throw new ExtractionException("The routing kernel semantic method set changed.");
        }
        foreach (IMethodSymbol method in kernelMethods.Values)
        {
            RequireSourceDeclaration(method, kernel, $"routing kernel method {method.Name}");
        }

        IMethodSymbol classifierSymbol = RequireDeclaredMethod(
            classifierModel,
            isSystem,
            transactionExtensions,
            "TransactionExtensions.IsSystem");
        IInvocationOperation routeKernelOperation = RequireBoundInvocation(
            transactionModel,
            routeIf.Condition,
            kernelMethods["UseSystemProcessor"],
            "ExecuteCore routing-kernel call");
        IInvocationOperation classifierOperation = RequireBoundInvocation(
            transactionModel,
            routeIf.Condition,
            classifierSymbol,
            "ExecuteCore system classifier call");
        RequireReachableSyntax(
            transactionModel,
            routeIf.FirstAncestorOrSelf<MethodDeclarationSyntax>()!,
            routeIf.Condition,
            "ExecuteCore system route");

        ClassDeclarationSyntax processorBase = (ClassDeclarationSyntax)routeIf.FirstAncestorOrSelf<ClassDeclarationSyntax>()!;
        MethodDeclarationSyntax executeCore = RequireMethod(processorBase, "ExecuteCore");
        IMethodSymbol executeCoreSymbol = RequireDeclaredMethod(
            transactionModel,
            executeCore,
            transactionProcessor,
            "TransactionProcessorBase.ExecuteCore");
        if (routeKernelOperation.Syntax is not InvocationExpressionSyntax routeKernelInvocation)
        {
            throw new ExtractionException("The ExecuteCore routing-kernel call did not retain invocation syntax.");
        }
        RequireParameterArgument(
            transactionModel,
            routeKernelInvocation,
            1,
            RequireMethodParameter(executeCoreSymbol, "opts", "ExecuteCore routing-kernel call"),
            "ExecuteCore routing-kernel call");
        RequireUnchangedParametersBeforeInvocation(
            transactionModel,
            executeCore,
            routeKernelInvocation,
            AdmittedParameters(executeCoreSymbol),
            "ExecuteCore routing-kernel call",
            ReferenceEscapePolicy.RejectByValueReferenceEscapes);
        if (classifierOperation.Syntax is not InvocationExpressionSyntax classifierInvocation)
        {
            throw new ExtractionException("ExecuteCore system classifier call did not retain invocation syntax.");
        }
        RequireReceiverParameter(
            classifierOperation,
            RequireMethodParameter(executeCoreSymbol, "tx", "ExecuteCore system classifier call"),
            "ExecuteCore system classifier call");
        RequireUnchangedParametersBeforeInvocation(
            transactionModel,
            executeCore,
            classifierInvocation,
            AdmittedParameters(executeCoreSymbol),
            "ExecuteCore system classifier call",
            ReferenceEscapePolicy.RejectByValueReferenceEscapes);
        MethodDeclarationSyntax entryExecute = RequireSingle(
            processorBase.Members.OfType<MethodDeclarationSyntax>().Where(static method =>
                method.Identifier.ValueText == "Execute" &&
                method.Modifiers.Any(SyntaxKind.ProtectedKeyword) &&
                method.ParameterList.Parameters.Count == 3),
            "The three-argument virtual Execute entry must be declared exactly once.");
        MethodDeclarationSyntax execution = RequireSingle(
            processorBase.Members.OfType<MethodDeclarationSyntax>().Where(static method =>
                method.Identifier.ValueText == "Execute" &&
                method.Modifiers.Any(SyntaxKind.PrivateKeyword) &&
                method.ParameterList.Parameters.Count == 6),
            "The six-argument Execute implementation must be declared exactly once.");
        MethodDeclarationSyntax evm = RequireMethod(processorBase, "ExecuteEvmTransaction");
        MethodDeclarationSyntax simple = RequireMethod(processorBase, "ExecuteSimpleTransfer");
        MethodDeclarationSyntax evmCall = RequireMethod(processorBase, "ExecuteEvmCall");
        MethodDeclarationSyntax counterMethod = RequireMethod(processorBase, "UpdateHeaderGasUsedAndPayFees");
        IMethodSymbol entryExecuteSymbol = RequireDeclaredMethod(
            transactionModel, entryExecute, transactionProcessor, "TransactionProcessorBase.Execute entry");
        InvocationExpressionSyntax normalExecute = RequireSingle(
            executeCore.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(static invocation =>
                invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "Execute" } &&
                invocation.ArgumentList.Arguments.Count == 3),
            "ExecuteCore must invoke the normal three-argument Execute entry exactly once.");
        RequireExactInvocation(
            transactionModel,
            normalExecute,
            entryExecuteSymbol,
            "ExecuteCore normal Execute dispatch");
        RequireParameterArgument(
            transactionModel,
            normalExecute,
            0,
            RequireMethodParameter(executeCoreSymbol, "tx", "ExecuteCore normal Execute dispatch"),
            "ExecuteCore normal Execute dispatch");
        RequireParameterArgument(
            transactionModel,
            normalExecute,
            1,
            RequireMethodParameter(executeCoreSymbol, "tracer", "ExecuteCore normal Execute dispatch"),
            "ExecuteCore normal Execute dispatch");
        RequireParameterArgument(
            transactionModel,
            normalExecute,
            2,
            RequireMethodParameter(executeCoreSymbol, "opts", "ExecuteCore normal Execute dispatch"),
            "ExecuteCore normal Execute dispatch");
        RequireUnchangedParametersBeforeInvocation(
            transactionModel,
            executeCore,
            normalExecute,
            AdmittedParameters(executeCoreSymbol),
            "ExecuteCore normal Execute dispatch",
            ReferenceEscapePolicy.RejectByValueReferenceEscapes,
            permittedReceiverCalls: [classifierSymbol]);
        RequireReachableSyntax(
            transactionModel,
            executeCore,
            normalExecute,
            "ExecuteCore normal Execute dispatch");
        IMethodSymbol executionSymbol = RequireDeclaredMethod(
            transactionModel, execution, transactionProcessor, "TransactionProcessorBase.Execute implementation");
        IMethodSymbol evmSymbol = RequireDeclaredMethod(
            transactionModel, evm, transactionProcessor, "TransactionProcessorBase.ExecuteEvmTransaction");
        IMethodSymbol simpleSymbol = RequireDeclaredMethod(
            transactionModel, simple, transactionProcessor, "TransactionProcessorBase.ExecuteSimpleTransfer");
        IMethodSymbol evmCallSymbol = RequireDeclaredMethod(
            transactionModel, evmCall, transactionProcessor, "TransactionProcessorBase.ExecuteEvmCall");
        IMethodSymbol counterSymbol = RequireDeclaredMethod(
            transactionModel, counterMethod, transactionProcessor, "TransactionProcessorBase.UpdateHeaderGasUsedAndPayFees");

        InvocationExpressionSyntax entryForward = RequireForwardedOption(
            transactionModel,
            entryExecute,
            entryExecuteSymbol,
            "Execute",
            targetParameterCount: 6,
            argumentIndex: 2);
        InvocationExpressionSyntax simpleDispatch = RequireForwardedOption(
            transactionModel,
            execution,
            executionSymbol,
            "ExecuteSimpleTransfer",
            targetParameterCount: 15,
            argumentIndex: 4);
        InvocationExpressionSyntax evmDispatch = RequireForwardedOption(
            transactionModel,
            execution,
            executionSymbol,
            "ExecuteEvmTransaction",
            targetParameterCount: 16,
            argumentIndex: 4);
        InvocationExpressionSyntax evmCounter = RequireForwardedOption(
            transactionModel,
            evm,
            evmSymbol,
            "UpdateHeaderGasUsedAndPayFees",
            targetParameterCount: 11,
            argumentIndex: 4);
        InvocationExpressionSyntax simpleCounter = RequireForwardedOption(
            transactionModel,
            simple,
            simpleSymbol,
            "UpdateHeaderGasUsedAndPayFees",
            targetParameterCount: 11,
            argumentIndex: 4);
        RequireExactInvocation(transactionModel, entryForward, executionSymbol, "Execute entry option forwarding");
        RequireExactInvocation(transactionModel, simpleDispatch, simpleSymbol, "simple-transfer dispatch");
        RequireExactInvocation(transactionModel, evmDispatch, evmSymbol, "EVM dispatch");
        RequireExactInvocation(transactionModel, evmCounter, counterSymbol, "EVM counter forwarding");
        RequireExactInvocation(transactionModel, simpleCounter, counterSymbol, "simple-transfer counter forwarding");
        RequireDirectReturnInvocation(entryExecute, entryForward, "Execute entry option forwarding");
        RequireReachableSyntax(transactionModel, entryExecute, entryForward, "Execute entry option forwarding");
        RequireDispatchShape(execution, simpleDispatch, evmDispatch);
        RequireReachableSyntax(transactionModel, execution, simpleDispatch, "simple-transfer dispatch");
        RequireReachableSyntax(transactionModel, execution, evmDispatch, "EVM dispatch");
        RequireDirectBodyInvocation(evm, evmCounter, "EVM counter forwarding");
        RequireDirectBodyInvocation(simple, simpleCounter, "simple-transfer counter forwarding");
        RequireReachableSyntax(transactionModel, evm, evmCounter, "EVM counter forwarding");
        RequireReachableSyntax(transactionModel, simple, simpleCounter, "simple-transfer counter forwarding");
        RequireMustReachExit(transactionModel, simple, simpleCounter, "simple-transfer counter forwarding");
        InvocationExpressionSyntax[] evmCallDispatches = evm.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(static invocation => InvocationName(invocation) == "ExecuteEvmCall")
            .ToArray();
        if (evmCallDispatches.Length != 2)
        {
            throw new ExtractionException("ExecuteEvmTransaction must dispatch exactly both tracing branches to ExecuteEvmCall.");
        }
        foreach (InvocationExpressionSyntax evmCallDispatch in evmCallDispatches)
        {
            RequireExactInvocation(
                transactionModel,
                evmCallDispatch,
                evmCallSymbol,
                "EVM ExecuteEvmCall dispatch");
            RequireParameterArgument(
                transactionModel,
                evmCallDispatch,
                0,
                RequireMethodParameter(evmSymbol, "tx", "EVM ExecuteEvmCall dispatch"),
                "EVM ExecuteEvmCall dispatch");
            RequireParameterArgument(
                transactionModel,
                evmCallDispatch,
                2,
                RequireMethodParameter(evmSymbol, "spec", "EVM ExecuteEvmCall dispatch"),
                "EVM ExecuteEvmCall dispatch");
            RequireParameterArgument(
                transactionModel,
                evmCallDispatch,
                4,
                RequireMethodParameter(evmSymbol, "opts", "EVM ExecuteEvmCall dispatch"),
                "EVM ExecuteEvmCall dispatch");
            RequireUnchangedParametersBeforeInvocation(
                transactionModel,
                evm,
                evmCallDispatch,
                AdmittedParameters(evmSymbol),
                "EVM ExecuteEvmCall dispatch",
                ReferenceEscapePolicy.DirectArgumentBindingsOnly);
            RequirePostDominates(
                transactionModel,
                evm,
                evmCallDispatch,
                evmCounter,
                "EVM counter forwarding after ExecuteEvmCall");
        }
        IInvocationOperation counterGateOperation = RequireBoundInvocation(
            transactionModel,
            counterIf.Condition,
            kernelMethods["ParticipatesInNormalBlockCounters"],
            "normal-counter kernel call");
        if (counterGateOperation.Syntax is not InvocationExpressionSyntax counterGateInvocation)
        {
            throw new ExtractionException("The normal-counter kernel call did not retain invocation syntax.");
        }
        RequireParameterArgument(
            transactionModel,
            counterGateInvocation,
            0,
            RequireMethodParameter(counterSymbol, "opts", "normal-counter kernel call"),
            "normal-counter kernel call");
        RequireUnchangedParametersBeforeInvocation(
            transactionModel,
            counterMethod,
            counterGateInvocation,
            AdmittedParameters(counterSymbol),
            "normal-counter kernel call",
            ReferenceEscapePolicy.DirectArgumentBindingsOnly);
        InvocationExpressionSyntax routedSystemExecute = RequireSingle(
            routeIf.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(static invocation => Canonical(invocation.Expression) ==
                    "GetOrCreateSystemTransactionProcessor().Execute"),
            "ExecuteCore must invoke the system processor exactly once.");
        IInvocationOperation routedSystemOperation = RequireExactInvocation(
            transactionModel,
            routedSystemExecute,
            entryExecuteSymbol,
            "ExecuteCore system-processor dispatch");
        RequireParameterArgument(
            transactionModel,
            routedSystemExecute,
            0,
            RequireMethodParameter(executeCoreSymbol, "tx", "ExecuteCore system-processor dispatch"),
            "ExecuteCore system-processor dispatch");
        RequireParameterArgument(
            transactionModel,
            routedSystemExecute,
            1,
            RequireMethodParameter(executeCoreSymbol, "tracer", "ExecuteCore system-processor dispatch"),
            "ExecuteCore system-processor dispatch");
        RequireParameterArgument(
            transactionModel,
            routedSystemExecute,
            2,
            RequireMethodParameter(executeCoreSymbol, "opts", "ExecuteCore system-processor dispatch"),
            "ExecuteCore system-processor dispatch");
        RequireUnchangedParametersBeforeInvocation(
            transactionModel,
            executeCore,
            routedSystemExecute,
            AdmittedParameters(executeCoreSymbol),
            "ExecuteCore system-processor dispatch",
            ReferenceEscapePolicy.RejectByValueReferenceEscapes,
            permittedReceiverCalls: [classifierSymbol]);
        RequireConditionalPathMustReach(
            transactionModel,
            executeCore,
            routeIf,
            routedSystemExecute,
            "ExecuteCore system-processor dispatch");
        if (routedSystemOperation.Instance?.Type is not INamedTypeSymbol routedSystemType ||
            routedSystemType.OriginalDefinition.ToDisplayString() !=
            "Nethermind.Evm.TransactionProcessing.SystemTransactionProcessor<TGasPolicy>" ||
            !HasSourceDeclaration(routedSystemType.OriginalDefinition, systemProcessor))
        {
            throw new ExtractionException("ExecuteCore does not dispatch base Execute through the admitted SystemTransactionProcessor source type.");
        }

        IMethodSymbol systemExecuteSymbol = RequireDeclaredMethod(
            systemModel,
            (MethodDeclarationSyntax)systemExecuteReturn.FirstAncestorOrSelf<MethodDeclarationSyntax>()!,
            systemProcessor,
            "SystemTransactionProcessor.Execute");
        MethodDeclarationSyntax systemExecuteMethod =
            systemExecuteReturn.FirstAncestorOrSelf<MethodDeclarationSyntax>()!;
        if (!SymbolEqualityComparer.Default.Equals(
                systemExecuteSymbol.OverriddenMethod?.OriginalDefinition,
                entryExecuteSymbol.OriginalDefinition))
        {
            throw new ExtractionException("SystemTransactionProcessor.Execute no longer overrides the admitted base Execute method.");
        }
        IInvocationOperation shouldPayOriginalValueOperation = RequireBoundInvocation(
            systemModel,
            payAssignment,
            kernelMethods["ShouldPayOriginalValue"],
            "system pay-original-value kernel call");
        IInvocationOperation systemExecutionOptionsOperation = RequireBoundInvocation(
            systemModel,
            systemExecuteReturn,
            kernelMethods["GetSystemExecutionOptions"],
            "system execution-options kernel call");
        if (shouldPayOriginalValueOperation.Syntax is not InvocationExpressionSyntax shouldPayOriginalValueInvocation ||
            systemExecutionOptionsOperation.Syntax is not InvocationExpressionSyntax systemExecutionOptionsInvocation)
        {
            throw new ExtractionException("The system kernel calls did not retain invocation syntax.");
        }
        RequireParameterArgument(
            systemModel,
            shouldPayOriginalValueInvocation,
            0,
            RequireMethodParameter(systemExecuteSymbol, "opts", "system pay-original-value kernel call"),
            "system pay-original-value kernel call");
        RequireUnchangedParametersBeforeInvocation(
            systemModel,
            systemExecuteMethod,
            shouldPayOriginalValueInvocation,
            AdmittedParameters(systemExecuteSymbol),
            "system pay-original-value kernel call",
            ReferenceEscapePolicy.DirectArgumentBindingsOnly);
        RequireParameterArgument(
            systemModel,
            systemExecutionOptionsInvocation,
            0,
            RequireMethodParameter(systemExecuteSymbol, "opts", "system execution-options kernel call"),
            "system execution-options kernel call");
        RequireUnchangedParametersBeforeInvocation(
            systemModel,
            systemExecuteMethod,
            systemExecutionOptionsInvocation,
            AdmittedParameters(systemExecuteSymbol),
            "system execution-options kernel call",
            ReferenceEscapePolicy.DirectArgumentBindingsOnly);
        IFieldSymbol payOriginalValueSymbol = systemModel.GetDeclaredSymbol(payOriginalValueField) as IFieldSymbol
            ?? throw new ExtractionException("SystemTransactionProcessor._payOriginalValue did not bind as a source field.");
        if (payOriginalValueSymbol.DeclaredAccessibility != Accessibility.Private ||
            payOriginalValueSymbol.IsStatic ||
            payOriginalValueSymbol.Type.SpecialType != SpecialType.System_Boolean)
        {
            throw new ExtractionException("SystemTransactionProcessor._payOriginalValue must remain a private instance Boolean field.");
        }
        RequireSourceDeclaration(
            payOriginalValueSymbol,
            systemProcessor,
            "SystemTransactionProcessor._payOriginalValue",
            payOriginalValueField);
        IExpressionStatementOperation payAssignmentOperation = systemModel.GetOperation(payAssignment) as IExpressionStatementOperation
            ?? throw new ExtractionException("The pay-original-value assignment did not bind as an expression statement.");
        if (payAssignmentOperation.Operation is not ISimpleAssignmentOperation
            {
                Target: IFieldReferenceOperation assignmentTarget,
            } ||
            !IsThisFieldReference(assignmentTarget, payOriginalValueSymbol))
        {
            throw new ExtractionException("SystemTransactionProcessor.Execute no longer assigns the admitted _payOriginalValue field.");
        }
        IInvocationOperation executionOptionsOperation = RequireBoundInvocation(
            systemModel,
            systemExecuteReturn,
            kernelMethods["GetSystemExecutionOptions"],
            "system execution-options field projection");
        if (executionOptionsOperation.Syntax is not InvocationExpressionSyntax executionOptionsInvocation)
        {
            throw new ExtractionException("The system execution-options field projection did not retain invocation syntax.");
        }
        RequireParameterArgument(
            systemModel,
            executionOptionsInvocation,
            0,
            RequireMethodParameter(systemExecuteSymbol, "opts", "system execution-options field projection"),
            "system execution-options field projection");
        if (executionOptionsOperation.Arguments is not [_, IArgumentOperation executionOptionsPayArgument] ||
            executionOptionsPayArgument.Value is not IFieldReferenceOperation executionOptionsPayField ||
            !IsThisFieldReference(executionOptionsPayField, payOriginalValueSymbol))
        {
            throw new ExtractionException("SystemTransactionProcessor.Execute no longer projects the assigned _payOriginalValue field.");
        }
        InvocationExpressionSyntax baseExecute = RequireSingle(
            systemExecuteReturn.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Where(static invocation => Canonical(invocation.Expression) == "base.Execute"),
            "SystemTransactionProcessor.Execute must invoke base.Execute exactly once.");
        RequireExactInvocation(
            systemModel,
            baseExecute,
            entryExecuteSymbol,
            "system base Execute dispatch");
        RequireParameterArgument(
            systemModel,
            baseExecute,
            0,
            RequireMethodParameter(systemExecuteSymbol, "tx", "system base Execute dispatch"),
            "system base Execute dispatch");
        RequireParameterArgument(
            systemModel,
            baseExecute,
            1,
            RequireMethodParameter(systemExecuteSymbol, "tracer", "system base Execute dispatch"),
            "system base Execute dispatch");
        RequireUnchangedParametersBeforeInvocation(
            systemModel,
            systemExecuteMethod,
            baseExecute,
            AdmittedParameters(systemExecuteSymbol),
            "system base Execute dispatch",
            ReferenceEscapePolicy.DirectArgumentBindingsOnly);
        RequireDirectReturnInvocation(systemExecuteMethod, baseExecute, "system effective-options projection");
        RequireMustReachExit(
            systemModel,
            systemExecuteMethod,
            baseExecute,
            "SystemTransactionProcessor.Execute inherited forwarding");
        RequirePostDominates(
            systemModel,
            systemExecuteMethod,
            payAssignment,
            baseExecute,
            "SystemTransactionProcessor.Execute inherited forwarding after decision assignment");

        MethodDeclarationSyntax basePayValue = RequireMethod(processorBase, "PayValue");
        IMethodSymbol basePayValueSymbol = RequireDeclaredMethod(
            transactionModel,
            basePayValue,
            transactionProcessor,
            "TransactionProcessorBase.PayValue");
        IMethodSymbol systemPayValueSymbol = RequireDeclaredMethod(
            systemModel,
            systemPayValue,
            systemProcessor,
            "SystemTransactionProcessor.PayValue");
        if (!basePayValueSymbol.IsVirtual ||
            !SymbolEqualityComparer.Default.Equals(
                systemPayValueSymbol.OverriddenMethod?.OriginalDefinition,
                basePayValueSymbol.OriginalDefinition))
        {
            throw new ExtractionException("SystemTransactionProcessor.PayValue no longer overrides the admitted base PayValue method.");
        }
        IfStatementSyntax payValueIf = RequireSingle(
            systemPayValue.Body?.Statements.OfType<IfStatementSyntax>() ?? [],
            "SystemTransactionProcessor.PayValue must retain exactly one decision gate.");
        if (systemModel.GetOperation(payValueIf.Condition) is not IFieldReferenceOperation payConditionField ||
            !IsThisFieldReference(payConditionField, payOriginalValueSymbol))
        {
            throw new ExtractionException("SystemTransactionProcessor.PayValue no longer reads the assigned _payOriginalValue field.");
        }
        InvocationExpressionSyntax basePayValueCall = RequireSingle(
            payValueIf.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            "SystemTransactionProcessor.PayValue must invoke base.PayValue exactly once.");
        RequireExactInvocation(
            systemModel,
            basePayValueCall,
            basePayValueSymbol,
            "system base PayValue dispatch");
        RequirePayValueArguments(systemModel, systemPayValueSymbol, basePayValueCall, "system base PayValue dispatch");
        InvocationExpressionSyntax evmPayValue = RequireNamedInvocation(
            evmCall,
            "PayValue",
            3,
            "EVM PayValue dispatch");
        InvocationExpressionSyntax simplePayValue = RequireNamedInvocation(
            simple,
            "PayValue",
            3,
            "simple-transfer PayValue dispatch");
        RequireVirtualThisInvocation(
            transactionModel,
            evmPayValue,
            basePayValueSymbol,
            "EVM PayValue dispatch");
        RequireVirtualThisInvocation(
            transactionModel,
            simplePayValue,
            basePayValueSymbol,
            "simple-transfer PayValue dispatch");
        RequirePayValueArguments(transactionModel, evmCallSymbol, evmPayValue, "EVM PayValue dispatch");
        RequirePayValueArguments(transactionModel, simpleSymbol, simplePayValue, "simple-transfer PayValue dispatch");
        RequireDirectBodyInvocation(evmCall, evmPayValue, "EVM PayValue dispatch");
        RequireSimplePayValuePlacement(simple, simplePayValue);
        RequireReachableSyntax(transactionModel, evmCall, evmPayValue, "EVM PayValue dispatch");
        RequireReachableSyntax(transactionModel, simple, simplePayValue, "simple-transfer PayValue dispatch");

        IMethodSymbol createSystemSymbol = RequireDeclaredMethod(
            transactionModel,
            createSystem,
            transactionProcessor,
            "TransactionProcessorBase.CreateSystemTransactionProcessor");
        ImplicitObjectCreationExpressionSyntax factoryCreation = (ImplicitObjectCreationExpressionSyntax)
            createSystem.ExpressionBody!.Expression;
        IMethodSymbol constructor = (transactionModel.GetOperation(factoryCreation) as IObjectCreationOperation)?.Constructor
            ?? throw new ExtractionException("The admitted standard system-processor factory creation did not bind.");
        if (constructor.ContainingType.OriginalDefinition.ToDisplayString() !=
                "Nethermind.Evm.TransactionProcessing.SystemTransactionProcessor<TGasPolicy>" ||
            !HasSourceDeclaration(constructor.ContainingType.OriginalDefinition, systemProcessor) ||
            !HasSourceDeclaration(createSystemSymbol, transactionProcessor))
        {
            throw new ExtractionException("The admitted standard factory does not construct the admitted SystemTransactionProcessor source type.");
        }
        INamedTypeSymbol concreteProcessor = RequireSourceType(
            compilation,
            "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessor",
            transactionProcessor,
            "standard Ethereum transaction processor");
        INamedTypeSymbol concreteBase = concreteProcessor.BaseType
            ?? throw new ExtractionException("The standard Ethereum transaction processor has no admitted base type.");
        if (concreteBase.ToDisplayString() !=
                "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessorBase" ||
            !HasSourceDeclaration(concreteBase, transactionProcessor) ||
            concreteBase.BaseType?.OriginalDefinition.ToDisplayString() !=
                "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>" ||
            !HasSourceDeclaration(concreteBase.BaseType.OriginalDefinition, transactionProcessor) ||
            concreteProcessor.GetMembers("CreateSystemTransactionProcessor").Length != 0)
        {
            throw new ExtractionException("The standard Ethereum processor no longer inherits the admitted default system factory route.");
        }

        IMethodSymbol scopedRegistrationSymbol = RequireDeclaredMethod(
            registrationModel,
            scopedRegistration,
            containerRegistration,
            "production ContainerBuilderExtensions.AddScoped");
        IMethodSymbol bindScopedSymbol = RequireDeclaredMethod(
            registrationModel,
            bindScoped,
            containerRegistration,
            "production ContainerBuilderExtensions.BindScoped");
        InvocationExpressionSyntax bindScopedCall = RequireNamedInvocation(
            scopedRegistration,
            "BindScoped",
            0,
            "AddScoped to BindScoped delegation");
        RequireExactInvocation(
            registrationModel,
            bindScopedCall,
            bindScopedSymbol,
            "AddScoped to BindScoped delegation",
            requireReducedExtension: true);
        IMethodSymbol boundBindScoped = (IMethodSymbol)registrationModel.GetSymbolInfo(bindScopedCall).Symbol!;
        ExpressionSyntax bindScopedReceiver = ((MemberAccessExpressionSyntax)bindScopedCall.Expression).Expression;
        if (registrationModel.GetTypeInfo(bindScopedReceiver).Type?.ToDisplayString() != "Autofac.ContainerBuilder" ||
            boundBindScoped.TypeArguments.Length != 2 ||
            !IsMethodTypeParameter(boundBindScoped.TypeArguments[0], scopedRegistrationSymbol, 0) ||
            !IsMethodTypeParameter(boundBindScoped.TypeArguments[1], scopedRegistrationSymbol, 1))
        {
            throw new ExtractionException(
                "Production AddScoped no longer delegates its service and implementation types to BindScoped. " +
                $"Bound arguments: {string.Join(", ", boundBindScoped.TypeArguments.Select(DescribeTypeArgument))}; " +
                $"source parameters: {string.Join(", ", scopedRegistrationSymbol.TypeParameters.Select(DescribeTypeArgument))}.");
        }
        RequireAutofacBindingChain(registrationModel, bindScoped, bindScopedSymbol);
        ClassDeclarationSyntax module = (ClassDeclarationSyntax)registration.FirstAncestorOrSelf<ClassDeclarationSyntax>()!;
        MethodDeclarationSyntax load = RequireMethod(module, "Load");
        IMethodSymbol loadSymbol = RequireDeclaredMethod(
            diModel,
            load,
            mainnetDi,
            "BlockProcessingModule.Load");
        IMethodSymbol? overriddenLoad = loadSymbol.OverriddenMethod;
        if (overriddenLoad?.ContainingType.ToDisplayString() != "Autofac.Module" ||
            overriddenLoad.Name != "Load" ||
            overriddenLoad.Parameters is not [IParameterSymbol loadParameter] ||
            loadParameter.Type.ToDisplayString() != "Autofac.ContainerBuilder" ||
            loadSymbol.Parameters is not [IParameterSymbol sourceLoadParameter] ||
            sourceLoadParameter.Type.ToDisplayString() != "Autofac.ContainerBuilder" ||
            !loadSymbol.ReturnsVoid)
        {
            throw new ExtractionException("BlockProcessingModule.Load does not override Autofac.Module.Load(ContainerBuilder).");
        }

        RequireExactInvocation(
            diModel,
            registration,
            scopedRegistrationSymbol,
            "standard mainnet transaction-processor registration",
            requireReducedExtension: true);
        RequireReachableSyntax(
            diModel,
            load,
            registration,
            "standard mainnet transaction-processor registration");
        IMethodSymbol registrationMethod = (IMethodSymbol)diModel.GetSymbolInfo(registration).Symbol!;
        IMethodSymbol registrationTarget = registrationMethod.ReducedFrom!;
        ExpressionSyntax registrationReceiver = ((MemberAccessExpressionSyntax)registration.Expression).Expression;
        if (diModel.GetTypeInfo(registrationReceiver).Type?.ToDisplayString() != "Autofac.ContainerBuilder" ||
            registrationTarget.Parameters is not [IParameterSymbol receiver] ||
            receiver.Type.ToDisplayString() != "Autofac.ContainerBuilder" ||
            registrationMethod.TypeArguments.Select(static type => type.ToDisplayString()).ToArray() is not
            ["Nethermind.Evm.TransactionProcessing.ITransactionProcessor",
             "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessor"] ||
            registrationMethod.TypeArguments[1] is not INamedTypeSymbol implementation ||
            !HasSourceDeclaration(implementation, transactionProcessor))
        {
            throw new ExtractionException("Standard mainnet DI registration is not the admitted scoped Ethereum transaction-processor route.");
        }
        RequireSemanticLoadRegistrationGraph(
            diModel,
            load,
            registration,
            registrationMethod.TypeArguments[0],
            containerRegistration);
        RequireAdmittedBuilderChain(diModel, registrationReceiver, containerRegistration);

        RequireNoErrorsAt(
            compilation,
            [
                routeIf.Condition,
                routedSystemExecute,
                normalExecute,
                payAssignment,
                systemExecuteReturn,
                systemPayValue,
                counterIf.Condition,
                entryForward,
                simpleDispatch,
                evmDispatch,
                evmCounter,
                simpleCounter,
                evmCallDispatches[0],
                evmCallDispatches[1],
                createSystem,
                isSystem,
                load.ParameterList,
                registration,
                scopedRegistration,
                bindScoped,
            ]);
    }

    private static bool IsThisFieldReference(IFieldReferenceOperation reference, IFieldSymbol expected) =>
        SymbolEqualityComparer.Default.Equals(reference.Field, expected) &&
        reference.Instance is IInstanceReferenceOperation
        {
            ReferenceKind: InstanceReferenceKind.ContainingTypeInstance,
        };

    private static InvocationExpressionSyntax RequireNamedInvocation(
        SyntaxNode scope,
        string targetName,
        int argumentCount,
        string description) => RequireSingle(
            scope.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(invocation =>
                InvocationName(invocation) == targetName &&
                invocation.ArgumentList.Arguments.Count == argumentCount),
            $"{description} must occur exactly once.");

    private static void RequireDirectBodyInvocation(
        MethodDeclarationSyntax method,
        InvocationExpressionSyntax invocation,
        string description)
    {
        if (invocation.Parent is not ExpressionStatementSyntax statement ||
            !ReferenceEquals(statement.Parent, method.Body))
        {
            throw new ExtractionException($"{description} must be a reachable direct body statement.");
        }
    }

    private static void RequireDirectReturnInvocation(
        MethodDeclarationSyntax method,
        InvocationExpressionSyntax invocation,
        string description)
    {
        if (invocation.Parent is not ReturnStatementSyntax returnStatement ||
            !ReferenceEquals(returnStatement.Parent, method.Body))
        {
            throw new ExtractionException($"{description} must be a reachable direct return.");
        }
    }

    private static void RequireDispatchShape(
        MethodDeclarationSyntax execution,
        InvocationExpressionSyntax simpleDispatch,
        InvocationExpressionSyntax evmDispatch)
    {
        if (execution.Body is not
            {
                Statements: [.., IfStatementSyntax simpleBranch, ReturnStatementSyntax evmReturn],
            } ||
            Canonical(simpleBranch.Condition) != "simpleTransferRecipientisnotnull" ||
            simpleBranch.Statement is not BlockSyntax
            {
                Statements: [ReturnStatementSyntax { Expression: InvocationExpressionSyntax simpleReturn }],
            } ||
            !ReferenceEquals(simpleReturn, simpleDispatch) ||
            evmReturn.Expression is not InvocationExpressionSyntax evmReturnExpression ||
            !ReferenceEquals(evmReturnExpression, evmDispatch))
        {
            throw new ExtractionException(
                "The six-argument Execute implementation must retain its reachable simple/EVM dispatch branches.");
        }
    }

    private static void RequireSimplePayValuePlacement(
        MethodDeclarationSyntax simple,
        InvocationExpressionSyntax payValue)
    {
        if (payValue.Parent is not ExpressionStatementSyntax payValueStatement ||
            payValueStatement.Parent is not IfStatementSyntax valueIf ||
            Canonical(valueIf.Condition) != "hasValueTransfer" ||
            valueIf.Else is not null ||
            valueIf.Parent is not BlockSyntax transferBlock ||
            transferBlock.Parent is not IfStatementSyntax transferIf ||
            Canonical(transferIf.Condition) != "!senderIsRecipient&&!newAccountOutOfGas" ||
            transferIf.Else is not null ||
            !ReferenceEquals(transferIf.Parent, simple.Body))
        {
            throw new ExtractionException(
                "Simple-transfer PayValue must remain on the admitted value-transfer path.");
        }
    }

    private static void RequirePayValueArguments(
        SemanticModel model,
        IMethodSymbol caller,
        InvocationExpressionSyntax invocation,
        string description)
    {
        IParameterSymbol[] expectedParameters = [
            RequireMethodParameter(caller, "tx", description),
            RequireMethodParameter(caller, "spec", description),
            RequireMethodParameter(caller, "opts", description),
        ];
        if (invocation.ArgumentList.Arguments.Count != expectedParameters.Length)
        {
            throw new ExtractionException($"{description} must pass exactly tx, spec, and opts.");
        }

        for (int index = 0; index < expectedParameters.Length; index++)
        {
            RequireParameterArgument(model, invocation, index, expectedParameters[index], description);
        }
        MethodDeclarationSyntax callerSyntax = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>()
            ?? throw new ExtractionException($"{description} caller syntax was not found.");
        RequireUnchangedParametersBeforeInvocation(
            model,
            callerSyntax,
            invocation,
            expectedParameters,
            description,
            ReferenceEscapePolicy.RejectByValueReferenceEscapes);
    }

    private static IParameterSymbol RequireMethodParameter(IMethodSymbol method, string name, string description) =>
        method.Parameters.SingleOrDefault(parameter => parameter.Name == name)
        ?? throw new ExtractionException($"{description} caller is missing its '{name}' parameter.");

    private static void RequireParameterArgument(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        int argumentIndex,
        IParameterSymbol expectedParameter,
        string description)
    {
        IInvocationOperation operation = model.GetOperation(invocation) as IInvocationOperation
            ?? throw new ExtractionException($"{description} did not bind as an invocation.");
        if (operation.Arguments.Length <= argumentIndex)
        {
            throw new ExtractionException($"{description} is missing argument {argumentIndex}.");
        }

        IOperation value = operation.Arguments[argumentIndex].Value;
        while (value is IConversionOperation conversion && conversion.IsImplicit)
        {
            value = conversion.Operand;
        }

        if (value is not IParameterReferenceOperation parameterReference ||
            !SymbolEqualityComparer.Default.Equals(parameterReference.Parameter, expectedParameter))
        {
            throw new ExtractionException(
                $"{description} argument {argumentIndex} must be the unchanged '{expectedParameter.Name}' parameter.");
        }
    }

    private static IParameterSymbol[] AdmittedParameters(IMethodSymbol method) => method.Parameters
        .Where(static parameter => parameter.Name is "tx" or "spec" or "opts")
        .ToArray();

    private enum ReferenceEscapePolicy
    {
        DirectArgumentBindingsOnly,
        RejectByValueReferenceEscapes,
    }

    private static void RequireUnchangedParametersBeforeInvocation(
        SemanticModel model,
        MethodDeclarationSyntax method,
        InvocationExpressionSyntax invocation,
        IReadOnlyList<IParameterSymbol> expectedParameters,
        string description,
        ReferenceEscapePolicy referenceEscapePolicy,
        IReadOnlyList<IMethodSymbol>? permittedReceiverCalls = null)
    {
        if (expectedParameters.Count == 0)
        {
            return;
        }

        ControlFlowGraph graph = GetControlFlowGraph(model, method, description);
        BasicBlock target = RequireBlock(
            graph,
            method,
            invocation,
            description,
            requireReachable: true);
        SyntaxNode[] candidates = method.DescendantNodes()
            .Where(static node => node is VariableDeclaratorSyntax or AssignmentExpressionSyntax or
                PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax or ArgumentSyntax or
                InvocationExpressionSyntax or MemberAccessExpressionSyntax)
            .Where(node => CanExecuteBeforeInvocation(graph, node, invocation, target))
            .OrderBy(static node => node.SpanStart)
            .ToArray();
        Dictionary<ISymbol, IParameterSymbol> aliases = new(SymbolEqualityComparer.Default);
        HashSet<ISymbol> referenceAliases = [];
        bool addedAlias;
        do
        {
            addedAlias = false;
            foreach (SyntaxNode node in candidates)
            {
                switch (node)
                {
                    case VariableDeclaratorSyntax variable:
                        if (variable.Initializer is not { Value: ExpressionSyntax initializer })
                        {
                            continue;
                        }

                        if (model.GetDeclaredSymbol(variable) is ILocalSymbol local &&
                            ResolveDirectAdmittedParameter(model, initializer, expectedParameters, aliases) is
                            IParameterSymbol aliasedParameter)
                        {
                            if (aliases.TryAdd(local, aliasedParameter))
                            {
                                addedAlias = true;
                            }
                            if (variable.FirstAncestorOrSelf<VariableDeclarationSyntax>()?.Type is RefTypeSyntax)
                            {
                                referenceAliases.Add(local);
                            }
                        }
                        break;
                    case AssignmentExpressionSyntax assignment:
                        if (ResolveDirectSymbol(model, assignment.Left) is ILocalSymbol assignedLocal &&
                            !referenceAliases.Contains(assignedLocal) &&
                            ResolveDirectAdmittedParameter(
                                model,
                                assignment.Right,
                                expectedParameters,
                                aliases) is IParameterSymbol assignedParameter &&
                            aliases.TryAdd(assignedLocal, assignedParameter))
                        {
                            addedAlias = true;
                        }
                        break;
                }
            }
        }
        while (addedAlias);

        RequireNoCapturingFunctions(model, method, expectedParameters, aliases, description);

        foreach (SyntaxNode node in candidates)
        {
            if (node is AssignmentExpressionSyntax assignmentNode &&
                ResolveAdmittedParameter(model, assignmentNode.Left, expectedParameters, aliases) is null &&
                FindAdmittedReference(model, assignmentNode.Left, expectedParameters, aliases) is not null)
            {
                throw new ExtractionException(
                    $"{description} contains an unanalyzed write through an admitted parameter or alias.");
            }

            switch (node)
            {
                case AssignmentExpressionSyntax assignment
                    when ResolveAdmittedParameter(model, assignment.Left, expectedParameters, aliases) is
                        IParameterSymbol parameter:
                    throw ParameterWriteBeforeInvocation(description, parameter, assignment);
                case AssignmentExpressionSyntax assignmentWrite:
                    if (ResolveDirectAdmittedParameter(
                            model,
                            assignmentWrite.Right,
                            expectedParameters,
                            aliases) is IParameterSymbol escapedParameter &&
                        ResolveDirectSymbol(model, assignmentWrite.Left) is not ILocalSymbol)
                    {
                        throw new ExtractionException(
                            $"{description} lets the admitted '{escapedParameter.Name}' value escape through a non-local alias.");
                    }
                    break;
                case PrefixUnaryExpressionSyntax prefix
                    when prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                        prefix.IsKind(SyntaxKind.PreDecrementExpression):
                    if (ResolveAdmittedParameter(model, prefix.Operand, expectedParameters, aliases) is
                        IParameterSymbol prefixParameter)
                    {
                        throw ParameterWriteBeforeInvocation(description, prefixParameter, prefix);
                    }
                    break;
                case PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                        postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    if (ResolveAdmittedParameter(model, postfix.Operand, expectedParameters, aliases) is
                        IParameterSymbol postfixParameter)
                    {
                        throw ParameterWriteBeforeInvocation(description, postfixParameter, postfix);
                    }
                    break;
                case ArgumentSyntax argument
                    when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) ||
                        argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword):
                    if (ResolveAdmittedParameter(model, argument.Expression, expectedParameters, aliases) is
                        IParameterSymbol argumentParameter)
                    {
                        throw ParameterWriteBeforeInvocation(description, argumentParameter, argument);
                    }
                    if (FindAdmittedReference(model, argument.Expression, expectedParameters, aliases) is not null)
                    {
                        throw new ExtractionException(
                            $"{description} contains an unanalyzed ref/out write through an admitted parameter or alias.");
                    }
                    break;
                case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword):
                    if (FindAdmittedReference(model, argument.Expression, expectedParameters, aliases) is
                        IParameterSymbol inParameter)
                    {
                        throw new ExtractionException(
                            $"{description} contains an unanalyzed in-reference escape through the admitted '{inParameter.Name}' parameter or alias.");
                    }
                    break;
                case ArgumentSyntax argument when referenceEscapePolicy == ReferenceEscapePolicy.RejectByValueReferenceEscapes &&
                    FindByValueAdmittedReference(model, argument, expectedParameters, aliases) is
                        IParameterSymbol byValueParameter:
                    RejectByValueReferenceEscape(
                        model,
                        argument,
                        byValueParameter,
                        description);
                    break;
                case InvocationExpressionSyntax call when
                    referenceEscapePolicy == ReferenceEscapePolicy.RejectByValueReferenceEscapes:
                    RejectReceiverReferenceEscape(
                        model,
                        call,
                        expectedParameters,
                        aliases,
                        permittedReceiverCalls,
                        description);
                    break;
                case MemberAccessExpressionSyntax methodGroup when
                    referenceEscapePolicy == ReferenceEscapePolicy.RejectByValueReferenceEscapes:
                    RejectMethodGroupReferenceEscape(model, methodGroup, expectedParameters, aliases, description);
                    break;
            }
        }
    }

    private static bool CanExecuteBeforeInvocation(
        ControlFlowGraph graph,
        SyntaxNode candidate,
        InvocationExpressionSyntax invocation,
        BasicBlock target)
    {
        if (ReferenceEquals(candidate, invocation) ||
            ReferenceEquals(candidate.SyntaxTree, invocation.SyntaxTree) &&
            invocation.ArgumentList.FullSpan.Contains(candidate.FullSpan))
        {
            return false;
        }

        BasicBlock? source = FindBlock(graph, candidate);
        if (source is null || !source.IsReachable)
        {
            return false;
        }

        if (!ReferenceEquals(source, target))
        {
            return CanReachBlock(source, target);
        }

        return candidate.SpanStart < invocation.SpanStart ||
            CanReachBlockAfterLeaving(source, target);
    }

    private static void RejectReceiverReferenceEscape(
        SemanticModel model,
        InvocationExpressionSyntax call,
        IReadOnlyList<IParameterSymbol> expectedParameters,
        IReadOnlyDictionary<ISymbol, IParameterSymbol> aliases,
        IReadOnlyList<IMethodSymbol>? permittedReceiverCalls,
        string description)
    {
        ExpressionSyntax? receiver = call.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Expression,
            _ => call.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression,
        };
        IParameterSymbol? receiverParameter = receiver is null
            ? null
            : FindByValueAdmittedReferenceSyntax(model, receiver, expectedParameters, aliases);
        IInvocationOperation? operation = model.GetOperation(call) as IInvocationOperation;
        receiverParameter ??= FindByValueAdmittedReference(
            operation?.Instance,
            expectedParameters,
            aliases);
        if (receiverParameter is null)
        {
            return;
        }

        if (operation is null)
        {
            throw new ExtractionException($"{description} contains an unbound receiver invocation.");
        }
        if (permittedReceiverCalls?.Any(permitted => SameOriginalMethod(operation.TargetMethod, permitted)) == true)
        {
            return;
        }
        throw new ExtractionException(
            $"{description} passes the admitted '{receiverParameter.Name}' reference as the receiver to the unmodeled call " +
            $"'{operation.TargetMethod.ToDisplayString()}' before the semantically bound call.");
    }

    private static void RejectMethodGroupReferenceEscape(
        SemanticModel model,
        MemberAccessExpressionSyntax methodGroup,
        IReadOnlyList<IParameterSymbol> expectedParameters,
        IReadOnlyDictionary<ISymbol, IParameterSymbol> aliases,
        string description)
    {
        if (methodGroup.Parent is InvocationExpressionSyntax invocation &&
            ReferenceEquals(invocation.Expression, methodGroup))
        {
            return;
        }

        if (model.GetSymbolInfo(methodGroup).Symbol is not IMethodSymbol method ||
            FindByValueAdmittedReferenceSyntax(
                model,
                methodGroup.Expression,
                expectedParameters,
                aliases) is not IParameterSymbol receiverParameter)
        {
            return;
        }

        throw new ExtractionException(
            $"{description} captures the admitted '{receiverParameter.Name}' reference as the method group " +
            $"'{method.ToDisplayString()}' before the semantically bound call.");
    }

    private static void RejectByValueReferenceEscape(
        SemanticModel model,
        ArgumentSyntax argument,
        IParameterSymbol parameter,
        string description)
    {
        if (argument.Parent is not ArgumentListSyntax { Parent: InvocationExpressionSyntax call })
        {
            throw new ExtractionException(
                $"{description} passes the admitted '{parameter.Name}' reference through an unsupported by-value argument form.");
        }

        IInvocationOperation operation = model.GetOperation(call) as IInvocationOperation
            ?? throw new ExtractionException(
                $"{description} passes the admitted '{parameter.Name}' reference through an unbound by-value invocation.");
        throw new ExtractionException(
            $"{description} passes the admitted '{parameter.Name}' reference by value to the unmodeled call " +
            $"'{operation.TargetMethod.ToDisplayString()}' before the semantically bound call.");
    }

    private static void RequireNoCapturingFunctions(
        SemanticModel model,
        MethodDeclarationSyntax method,
        IReadOnlyList<IParameterSymbol> expectedParameters,
        IReadOnlyDictionary<ISymbol, IParameterSymbol> aliases,
        string description)
    {
        foreach (SyntaxNode function in method.DescendantNodes().Where(static node =>
                     node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
        {
            foreach (IdentifierNameSyntax reference in function.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                ISymbol? symbol = model.GetSymbolInfo(reference).Symbol;
                if (symbol is not null &&
                    (expectedParameters.Any(expected =>
                         SymbolEqualityComparer.Default.Equals(expected, symbol)) ||
                     aliases.ContainsKey(symbol)))
                {
                    throw new ExtractionException(
                        $"{description} hides a write-capable local function or closure over the admitted '{reference.Identifier.ValueText}' value.");
                }
            }
        }
    }

    private static IParameterSymbol? ResolveAdmittedParameter(
        SemanticModel model,
        ExpressionSyntax expression,
        IReadOnlyList<IParameterSymbol> expectedParameters,
        IReadOnlyDictionary<ISymbol, IParameterSymbol> aliases)
    {
        ISymbol? symbol = ResolveRootSymbol(model, expression);
        if (symbol is null)
        {
            return null;
        }

        if (symbol is IParameterSymbol parameter)
        {
            return expectedParameters.Any(expected =>
                SymbolEqualityComparer.Default.Equals(expected, parameter))
                ? parameter
                : null;
        }

        return aliases.TryGetValue(symbol, out IParameterSymbol? aliasedParameter)
            ? aliasedParameter
            : null;
    }

    private static IParameterSymbol? ResolveDirectAdmittedParameter(
        SemanticModel model,
        ExpressionSyntax expression,
        IReadOnlyList<IParameterSymbol> expectedParameters,
        IReadOnlyDictionary<ISymbol, IParameterSymbol> aliases)
    {
        switch (expression)
        {
            case BinaryExpressionSyntax binary when
                binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.CoalesceExpression):
                return ResolveDirectAdmittedParameter(model, binary.Left, expectedParameters, aliases) ??
                    ResolveDirectAdmittedParameter(model, binary.Right, expectedParameters, aliases);
            case ConditionalExpressionSyntax conditional:
                return ResolveDirectAdmittedParameter(model, conditional.WhenTrue, expectedParameters, aliases) ??
                    ResolveDirectAdmittedParameter(model, conditional.WhenFalse, expectedParameters, aliases);
        }

        ISymbol? symbol = ResolveDirectSymbol(model, expression);
        if (symbol is null)
        {
            return null;
        }

        if (symbol is IParameterSymbol parameter)
        {
            return expectedParameters.Any(expected =>
                SymbolEqualityComparer.Default.Equals(expected, parameter))
                ? parameter
                : null;
        }

        return aliases.TryGetValue(symbol, out IParameterSymbol? aliasedParameter)
            ? aliasedParameter
            : null;
    }

    private static IParameterSymbol? FindAdmittedReference(
        SemanticModel model,
        SyntaxNode syntax,
        IReadOnlyList<IParameterSymbol> expectedParameters,
        IReadOnlyDictionary<ISymbol, IParameterSymbol> aliases)
    {
        foreach (IdentifierNameSyntax reference in syntax.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            ISymbol? symbol = model.GetSymbolInfo(reference).Symbol;
            if (symbol is IParameterSymbol parameter && expectedParameters.Any(expected =>
                    SymbolEqualityComparer.Default.Equals(expected, parameter)))
            {
                return parameter;
            }

            if (symbol is not null && aliases.TryGetValue(symbol, out IParameterSymbol? aliasedParameter))
            {
                return aliasedParameter;
            }
        }

        return null;
    }

    private static IParameterSymbol? FindByValueAdmittedReference(
        SemanticModel model,
        ArgumentSyntax argument,
        IReadOnlyList<IParameterSymbol> expectedParameters,
        IReadOnlyDictionary<ISymbol, IParameterSymbol> aliases)
    {
        if (model.GetOperation(argument) is IArgumentOperation argumentOperation)
        {
            return FindByValueAdmittedReference(argumentOperation.Value, expectedParameters, aliases);
        }

        return FindByValueAdmittedReferenceSyntax(model, argument.Expression, expectedParameters, aliases);
    }

    private static IParameterSymbol? FindByValueAdmittedReferenceSyntax(
        SemanticModel model,
        ExpressionSyntax expression,
        IReadOnlyList<IParameterSymbol> expectedParameters,
        IReadOnlyDictionary<ISymbol, IParameterSymbol> aliases)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return FindByValueAdmittedReferenceSyntax(model, parenthesized.Expression, expectedParameters, aliases);
            case CastExpressionSyntax cast:
                return FindByValueAdmittedReferenceSyntax(model, cast.Expression, expectedParameters, aliases);
            case CheckedExpressionSyntax checkedExpression:
                return FindByValueAdmittedReferenceSyntax(model, checkedExpression.Expression, expectedParameters, aliases);
            case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                return FindByValueAdmittedReferenceSyntax(model, postfix.Operand, expectedParameters, aliases);
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.CoalesceExpression):
                return FindByValueAdmittedReferenceSyntax(model, binary.Left, expectedParameters, aliases) ??
                    FindByValueAdmittedReferenceSyntax(model, binary.Right, expectedParameters, aliases);
            case ConditionalExpressionSyntax conditional:
                return FindByValueAdmittedReferenceSyntax(model, conditional.WhenTrue, expectedParameters, aliases) ??
                    FindByValueAdmittedReferenceSyntax(model, conditional.WhenFalse, expectedParameters, aliases);
            default:
                return ResolveDirectAdmittedParameter(model, expression, expectedParameters, aliases) is
                    IParameterSymbol parameter && parameter.Type.IsReferenceType
                    ? parameter
                    : null;
        }
    }

    private static IParameterSymbol? FindByValueAdmittedReference(
        IOperation? operation,
        IReadOnlyList<IParameterSymbol> expectedParameters,
        IReadOnlyDictionary<ISymbol, IParameterSymbol> aliases)
    {
        if (operation is null)
        {
            return null;
        }

        switch (operation)
        {
            case IParameterReferenceOperation parameterReference when parameterReference.Parameter.Type.IsReferenceType:
                return expectedParameters.Any(expected =>
                    SymbolEqualityComparer.Default.Equals(expected, parameterReference.Parameter))
                    ? parameterReference.Parameter
                    : null;
            case ILocalReferenceOperation localReference when
                aliases.TryGetValue(localReference.Local, out IParameterSymbol? aliasedParameter) &&
                aliasedParameter.Type.IsReferenceType:
                return aliasedParameter;
            case IConversionOperation conversion:
                return FindByValueAdmittedReference(conversion.Operand, expectedParameters, aliases);
            case IParenthesizedOperation parenthesized:
                return FindByValueAdmittedReference(parenthesized.Operand, expectedParameters, aliases);
            case IConditionalOperation conditional:
                return FindByValueAdmittedReference(conditional.WhenTrue, expectedParameters, aliases) ??
                    FindByValueAdmittedReference(conditional.WhenFalse, expectedParameters, aliases);
            case ICoalesceOperation coalesce:
                return FindByValueAdmittedReference(coalesce.Value, expectedParameters, aliases) ??
                    FindByValueAdmittedReference(coalesce.WhenNull, expectedParameters, aliases);
            default:
                return null;
        }
    }

    private static ISymbol? ResolveRootSymbol(SemanticModel model, ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    continue;
                case CheckedExpressionSyntax checkedExpression:
                    expression = checkedExpression.Expression;
                    continue;
                case PostfixUnaryExpressionSyntax postfix when
                    postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    expression = postfix.Operand;
                    continue;
                case RefExpressionSyntax reference:
                    expression = reference.Expression;
                    continue;
                case MemberAccessExpressionSyntax member:
                    expression = member.Expression;
                    continue;
                case ElementAccessExpressionSyntax element:
                    expression = element.Expression;
                    continue;
            }

            return model.GetSymbolInfo(expression).Symbol;
        }
    }

    private static ISymbol? ResolveDirectSymbol(SemanticModel model, ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    continue;
                case CheckedExpressionSyntax checkedExpression:
                    expression = checkedExpression.Expression;
                    continue;
                case PostfixUnaryExpressionSyntax postfix when
                    postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    expression = postfix.Operand;
                    continue;
                case RefExpressionSyntax reference:
                    expression = reference.Expression;
                    continue;
            }

            return model.GetSymbolInfo(expression).Symbol;
        }
    }

    private static ExtractionException ParameterWriteBeforeInvocation(
        string description,
        IParameterSymbol parameter,
        SyntaxNode write) => new(
        $"{description} changes the admitted '{parameter.Name}' parameter before the call at {write.GetLocation().GetLineSpan().StartLinePosition.Line + 1}:{write.GetLocation().GetLineSpan().StartLinePosition.Character + 1}.");

    private static void RequireReceiverParameter(
        IInvocationOperation invocation,
        IParameterSymbol expectedParameter,
        string description)
    {
        IOperation? receiver = invocation.Instance;
        while (receiver is IConversionOperation conversion && conversion.IsImplicit)
        {
            receiver = conversion.Operand;
        }

        if (receiver is not IParameterReferenceOperation parameterReference ||
            !SymbolEqualityComparer.Default.Equals(parameterReference.Parameter, expectedParameter))
        {
            throw new ExtractionException(
                $"{description} receiver must be the unchanged '{expectedParameter.Name}' parameter.");
        }
    }

    private static void RequireReachableSyntax(
        SemanticModel model,
        MethodDeclarationSyntax method,
        SyntaxNode syntax,
        string description)
    {
        if (syntax.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault() is not MethodDeclarationSyntax owner ||
            !ReferenceEquals(owner, method))
        {
            throw new ExtractionException($"{description} must belong directly to its admitted method.");
        }

        IOperation body = model.GetOperation(method)
            ?? throw new ExtractionException($"{description} method body did not bind.");
        ControlFlowGraph graph = body switch
        {
            IMethodBodyOperation methodBody => ControlFlowGraph.Create(methodBody),
            _ => throw new ExtractionException($"{description} method body did not produce a control-flow graph."),
        };
        BasicBlock? containingBlock = graph.Blocks
            .Where(block => BlockContainsSyntax(block, syntax))
            .OrderBy(block => block.Ordinal)
            .FirstOrDefault();
        if (containingBlock is null || !containingBlock.IsReachable)
        {
            throw new ExtractionException($"{description} must be on a reachable control-flow path.");
        }
    }

    private static void RequireMustReachExit(
        SemanticModel model,
        MethodDeclarationSyntax method,
        SyntaxNode syntax,
        string description)
    {
        ControlFlowGraph graph = GetControlFlowGraph(model, method, description);
        BasicBlock entry = graph.Blocks
            .SingleOrDefault(static block => block.Kind.ToString() == "Entry")
            ?? throw new ExtractionException($"{description} method control-flow graph has no entry block.");
        BasicBlock target = RequireBlock(graph, method, syntax, description, requireReachable: true);
        if (CanReachExitWithout(graph, entry, target))
        {
            throw new ExtractionException(
                $"{description} is not on every reachable path to the method exit.");
        }
    }

    private static void RequirePostDominates(
        SemanticModel model,
        MethodDeclarationSyntax method,
        SyntaxNode sourceSyntax,
        SyntaxNode targetSyntax,
        string description)
    {
        ControlFlowGraph graph = GetControlFlowGraph(model, method, description);
        BasicBlock source = RequireBlock(graph, method, sourceSyntax, description, requireReachable: true);
        BasicBlock target = RequireBlock(graph, method, targetSyntax, description, requireReachable: true);
        if (ReferenceEquals(source, target) && targetSyntax.SpanStart < sourceSyntax.SpanStart)
        {
            throw new ExtractionException($"{description} does not follow its required source operation.");
        }

        if (CanReachExitWithout(graph, source, target))
        {
            throw new ExtractionException(
                $"{description} does not postdominate its source operation on every reachable path.");
        }
    }

    private static void RequireConditionalPathMustReach(
        SemanticModel model,
        MethodDeclarationSyntax method,
        IfStatementSyntax condition,
        SyntaxNode targetSyntax,
        string description)
    {
        ControlFlowGraph graph = GetControlFlowGraph(model, method, description);
        BasicBlock conditionBlock = RequireBlock(graph, method, condition.Condition, description, requireReachable: true);
        BasicBlock targetBlock = RequireBlock(graph, method, targetSyntax, description, requireReachable: true);
        BasicBlock[] successors = Successors(conditionBlock)
            .Where(static block => block.IsReachable)
            .Distinct()
            .ToArray();
        BasicBlock[] targetSuccessors = successors
            .Where(successor => CanReachBlock(successor, targetBlock))
            .ToArray();
        if (targetSuccessors.Length != 1)
        {
            throw new ExtractionException(
                $"{description} has {targetSuccessors.Length} condition branches that can reach the admitted forwarding call.");
        }

        if (CanReachExitWithout(graph, targetSuccessors[0], targetBlock))
        {
            throw new ExtractionException(
                $"{description} can leave its selected branch before the admitted forwarding call.");
        }
    }

    private static ControlFlowGraph GetControlFlowGraph(
        SemanticModel model,
        MethodDeclarationSyntax method,
        string description)
    {
        IOperation body = model.GetOperation(method)
            ?? throw new ExtractionException($"{description} method body did not bind.");
        return body switch
        {
            IMethodBodyOperation methodBody => ControlFlowGraph.Create(methodBody),
            _ => throw new ExtractionException($"{description} method body did not produce a control-flow graph."),
        };
    }

    private static BasicBlock RequireBlock(
        ControlFlowGraph graph,
        MethodDeclarationSyntax method,
        SyntaxNode syntax,
        string description,
        bool requireReachable)
    {
        BasicBlock? block = FindBlock(graph, syntax);
        if (block is null || requireReachable && !block.IsReachable)
        {
            throw new ExtractionException($"{description} syntax is not on a reachable control-flow block.");
        }

        return block;
    }

    private static BasicBlock? FindBlock(ControlFlowGraph graph, SyntaxNode syntax) => graph.Blocks
        .Where(candidate => BlockContainsSyntax(candidate, syntax))
        .OrderBy(candidate => candidate.Ordinal)
        .FirstOrDefault();

    private static bool CanReachExitWithout(
        ControlFlowGraph graph,
        BasicBlock start,
        BasicBlock excluded)
    {
        Queue<BasicBlock> pending = new();
        HashSet<BasicBlock> visited = [];
        pending.Enqueue(start);
        while (pending.TryDequeue(out BasicBlock? block))
        {
            if (!block.IsReachable || !visited.Add(block) || ReferenceEquals(block, excluded))
            {
                continue;
            }

            if (block.Kind.ToString() == "Exit")
            {
                return true;
            }

            foreach (BasicBlock successor in Successors(block))
            {
                pending.Enqueue(successor);
            }
        }

        return false;
    }

    private static bool CanReachBlock(BasicBlock start, BasicBlock target)
    {
        Queue<BasicBlock> pending = new();
        HashSet<BasicBlock> visited = [];
        pending.Enqueue(start);
        while (pending.TryDequeue(out BasicBlock? block))
        {
            if (!block.IsReachable || !visited.Add(block))
            {
                continue;
            }

            if (ReferenceEquals(block, target))
            {
                return true;
            }

            foreach (BasicBlock successor in Successors(block))
            {
                pending.Enqueue(successor);
            }
        }

        return false;
    }

    private static bool CanReachBlockAfterLeaving(BasicBlock start, BasicBlock target)
    {
        Queue<BasicBlock> pending = new();
        HashSet<BasicBlock> visited = [];
        foreach (BasicBlock successor in Successors(start))
        {
            pending.Enqueue(successor);
        }

        while (pending.TryDequeue(out BasicBlock? block))
        {
            if (!block.IsReachable || !visited.Add(block))
            {
                continue;
            }

            if (ReferenceEquals(block, target))
            {
                return true;
            }

            foreach (BasicBlock successor in Successors(block))
            {
                pending.Enqueue(successor);
            }
        }

        return false;
    }

    private static IEnumerable<BasicBlock> Successors(BasicBlock block)
    {
        HashSet<int> seen = [];
        ControlFlowBranch?[] branches = [block.FallThroughSuccessor, block.ConditionalSuccessor];
        foreach (ControlFlowBranch? branch in branches)
        {
            if (branch?.Destination is BasicBlock destination && seen.Add(destination.Ordinal))
            {
                yield return destination;
            }
        }
    }

    private static bool BlockContainsSyntax(BasicBlock block, SyntaxNode syntax)
    {
        foreach (IOperation operation in block.Operations)
        {
            if (OperationContainsSyntax(operation, syntax))
            {
                return true;
            }
        }

        return block.BranchValue is not null && OperationContainsSyntax(block.BranchValue, syntax);
    }

    private static bool OperationContainsSyntax(IOperation operation, SyntaxNode syntax) =>
        ReferenceEquals(operation.Syntax.SyntaxTree, syntax.SyntaxTree) &&
        operation.Syntax.FullSpan.Contains(syntax.FullSpan);

    private static void RequireVirtualThisInvocation(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        IMethodSymbol expected,
        string description)
    {
        IInvocationOperation operation = RequireExactInvocation(model, invocation, expected, description);
        if (invocation.Expression is not IdentifierNameSyntax ||
            !operation.IsVirtual ||
            operation.Instance is not IInstanceReferenceOperation
            {
                ReferenceKind: InstanceReferenceKind.ContainingTypeInstance,
            })
        {
            throw new ExtractionException($"{description} no longer performs virtual dispatch on the current processor instance.");
        }
    }

    private static void RequireAutofacBindingChain(
        SemanticModel model,
        MethodDeclarationSyntax bindScoped,
        IMethodSymbol bindScopedSymbol)
    {
        (string Name, string Type)[] expectedCalls =
        [
            ("Register", "Autofac.RegistrationExtensions"),
            ("Resolve", "Autofac.ResolutionExtensions"),
            ("As", "Autofac.Builder.IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle>"),
            ("InstancePerLifetimeScope", "Autofac.Builder.IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle>"),
            ("ExternallyOwned", "Autofac.Builder.IRegistrationBuilder<TLimit, TActivatorData, TRegistrationStyle>"),
        ];
        foreach ((string name, string type) in expectedCalls)
        {
            InvocationExpressionSyntax invocation = RequireNamedInvocation(
                bindScoped,
                name,
                name == "Register" ? 1 : 0,
                $"BindScoped {name} operation");
            IInvocationOperation operation = model.GetOperation(invocation) as IInvocationOperation
                ?? throw new ExtractionException($"BindScoped {name} did not bind as an invocation.");
            IMethodSymbol boundMethod = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol
                ?? operation.TargetMethod;
            IMethodSymbol original = (boundMethod.ReducedFrom ?? boundMethod).OriginalDefinition;
            if (original.Name != name || original.ContainingType.OriginalDefinition.ToDisplayString() != type)
            {
                throw new ExtractionException(
                    $"BindScoped {name} bound to {boundMethod.ToDisplayString()} instead of the admitted Autofac operation.");
            }
        }

        InvocationExpressionSyntax resolve = RequireNamedInvocation(
            bindScoped,
            "Resolve",
            0,
            "BindScoped Resolve operation");
        IMethodSymbol resolveMethod = (IMethodSymbol)model.GetSymbolInfo(resolve).Symbol!;
        if (resolveMethod.TypeArguments is not [ITypeSymbol resolvedType] ||
            !IsMethodTypeParameter(resolvedType, bindScopedSymbol, 1) ||
            resolve.Expression is not MemberAccessExpressionSyntax { Expression: ExpressionSyntax resolveReceiver } ||
            model.GetTypeInfo(resolveReceiver).Type?.ToDisplayString() != "Autofac.IComponentContext")
        {
            throw new ExtractionException("BindScoped no longer resolves its admitted TFrom implementation type.");
        }
        InvocationExpressionSyntax service = RequireNamedInvocation(
            bindScoped,
            "As",
            0,
            "BindScoped As operation");
        IMethodSymbol serviceMethod = (IMethodSymbol)model.GetSymbolInfo(service).Symbol!;
        if (serviceMethod.TypeArguments is not [ITypeSymbol serviceType] ||
            !IsMethodTypeParameter(serviceType, bindScopedSymbol, 0))
        {
            throw new ExtractionException("BindScoped no longer exposes its admitted TTo service type.");
        }
    }

    private static bool IsMethodTypeParameter(ITypeSymbol type, IMethodSymbol declaringMethod, int ordinal)
    {
        if (type is not ITypeParameterSymbol
            {
                TypeParameterKind: TypeParameterKind.Method,
                Ordinal: var actualOrdinal,
            } parameter ||
            actualOrdinal != ordinal ||
            declaringMethod.TypeParameters.Length <= ordinal ||
            parameter.DeclaringSyntaxReferences is not [SyntaxReference actualDeclaration] ||
            declaringMethod.TypeParameters[ordinal].DeclaringSyntaxReferences is not [SyntaxReference expectedDeclaration])
        {
            return false;
        }

        return ReferenceEquals(actualDeclaration.SyntaxTree, expectedDeclaration.SyntaxTree) &&
            actualDeclaration.Span == expectedDeclaration.Span;
    }

    private static string DescribeTypeArgument(ITypeSymbol type) => type is ITypeParameterSymbol parameter
        ? $"{parameter.ToDisplayString()}[ordinal={parameter.Ordinal}, " +
          $"owner={parameter.DeclaringMethod?.ToDisplayString() ?? parameter.DeclaringType?.ToDisplayString() ?? "<none>"}]"
        : type.ToDisplayString();

    private static IMethodSymbol RequireDeclaredMethod(
        SemanticModel model,
        MethodDeclarationSyntax syntax,
        SourceFile source,
        string description)
    {
        if (!ReferenceEquals(syntax.SyntaxTree, source.Tree))
        {
            throw new ExtractionException($"{description} did not originate in its admitted syntax tree.");
        }
        IMethodSymbol symbol = model.GetDeclaredSymbol(syntax)
            ?? throw new ExtractionException($"{description} did not bind as a source method.");
        RequireSourceDeclaration(symbol, source, description, syntax);
        return symbol;
    }

    private static INamedTypeSymbol RequireSourceType(
        CSharpCompilation compilation,
        string metadataName,
        SourceFile source,
        string description)
    {
        INamedTypeSymbol symbol = compilation.GetTypeByMetadataName(metadataName)
            ?? throw new ExtractionException($"The admitted {description} symbol was not found.");
        RequireSourceDeclaration(symbol, source, description);
        return symbol;
    }

    private static IInvocationOperation RequireBoundInvocation(
        SemanticModel model,
        SyntaxNode scope,
        IMethodSymbol expected,
        string description)
    {
        InvocationExpressionSyntax[] matches = scope.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => model.GetOperation(invocation) is IInvocationOperation operation &&
                SameOriginalMethod(operation.TargetMethod, expected))
            .ToArray();
        InvocationExpressionSyntax invocation = RequireSingle(
            matches,
            $"{description} must bind exactly once to {expected.ToDisplayString()} in the admitted syntax tree. " +
            "Bound invocations: " + string.Join(", ", scope.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Select(candidate =>
                    (model.GetOperation(candidate) as IInvocationOperation)?.TargetMethod.ToDisplayString() ??
                    $"<unbound:{candidate}; candidates={string.Join('|', model.GetSymbolInfo(candidate).CandidateSymbols.Select(static symbol => symbol.ToDisplayString()))}; reason={model.GetSymbolInfo(candidate).CandidateReason}>")) +
            ". Diagnostics: " + string.Join(" | ", model.Compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Location.SourceTree == scope.SyntaxTree &&
                    scope.FullSpan.Contains(diagnostic.Location.SourceSpan))
                .Select(static diagnostic => diagnostic.ToString())));
        return (IInvocationOperation)model.GetOperation(invocation)!;
    }

    private static IInvocationOperation RequireExactInvocation(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        IMethodSymbol expected,
        string description,
        bool requireReducedExtension = false)
    {
        IInvocationOperation operation = model.GetOperation(invocation) as IInvocationOperation
            ?? throw new ExtractionException($"{description} did not bind as an invocation.");
        IMethodSymbol boundMethod = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol
            ?? operation.TargetMethod;
        if (requireReducedExtension && boundMethod.ReducedFrom is null)
        {
            throw new ExtractionException(
                $"{description} did not bind as the admitted reduced extension call. " +
                $"Target={operation.TargetMethod.ToDisplayString()}, kind={operation.TargetMethod.MethodKind}, " +
                $"extension={operation.TargetMethod.IsExtensionMethod}, instance={operation.Instance?.Type?.ToDisplayString() ?? "<null>"}. " +
                $"SymbolInfo={boundMethod.ToDisplayString()}, kind={boundMethod.MethodKind}. " +
                "Diagnostics: " + string.Join(" | ", model.Compilation.GetDiagnostics()
                    .Where(diagnostic => diagnostic.Location.SourceTree == invocation.SyntaxTree &&
                        invocation.FullSpan.Contains(diagnostic.Location.SourceSpan))
                    .Select(static diagnostic => diagnostic.ToString())));
        }
        if (!SameOriginalMethod(boundMethod, expected))
        {
            throw new ExtractionException(
                $"{description} bound to {boundMethod.ToDisplayString()} instead of {expected.ToDisplayString()}.");
        }
        return operation;
    }

    private static bool SameOriginalMethod(IMethodSymbol actual, IMethodSymbol expected) =>
        SymbolEqualityComparer.Default.Equals(
            (actual.ReducedFrom ?? actual).OriginalDefinition,
            expected.OriginalDefinition);

    private static void RequireSourceDeclaration(
        ISymbol symbol,
        SourceFile source,
        string description,
        SyntaxNode? exactSyntax = null)
    {
        SyntaxReference[] declarations = symbol.DeclaringSyntaxReferences.ToArray();
        if (declarations.Length != 1 ||
            !ReferenceEquals(declarations[0].SyntaxTree, source.Tree) ||
            !string.Equals(
                Path.GetFullPath(declarations[0].SyntaxTree.FilePath),
                Path.GetFullPath(source.AbsolutePath),
                StringComparison.OrdinalIgnoreCase) ||
            exactSyntax is not null && !ReferenceEquals(declarations[0].GetSyntax(), exactSyntax))
        {
            throw new ExtractionException($"{description} is not declared once by its exact admitted source syntax.");
        }
    }

    private static bool HasSourceDeclaration(ISymbol symbol, SourceFile source) =>
        symbol.DeclaringSyntaxReferences.Any(reference =>
            ReferenceEquals(reference.SyntaxTree, source.Tree) &&
            string.Equals(
                Path.GetFullPath(reference.SyntaxTree.FilePath),
                Path.GetFullPath(source.AbsolutePath),
                StringComparison.OrdinalIgnoreCase));

    private static void RequireNoErrorsAt(CSharpCompilation compilation, IReadOnlyList<SyntaxNode> nodes)
    {
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(static diagnostic => !diagnostic.IsSuppressed &&
                diagnostic.Severity == DiagnosticSeverity.Error &&
                diagnostic.Location.IsInSource)
            .Where(diagnostic => nodes.Any(node =>
                ReferenceEquals(diagnostic.Location.SourceTree, node.SyntaxTree) &&
                node.FullSpan.Contains(diagnostic.Location.SourceSpan)))
            .ToArray();
        if (errors.Length != 0)
        {
            throw new ExtractionException(
                "An admitted routing syntax node has semantic diagnostics: " +
                string.Join(Environment.NewLine, errors.Select(static diagnostic => diagnostic.ToString())));
        }
    }

    private static bool IsTransactionProcessorRegistration(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax member &&
        (member.Name is GenericNameSyntax generic &&
            IsProcessorRegistrationOperation(generic.Identifier.ValueText) &&
            generic.TypeArgumentList.Arguments is [TypeSyntax serviceType, ..] &&
            Canonical(serviceType) == "ITransactionProcessor" ||
            member.Name is IdentifierNameSyntax simpleName &&
            IsProcessorRegistrationOperation(simpleName.Identifier.ValueText) &&
            invocation.ArgumentList.Arguments.Any(static argument =>
                argument.Expression is TypeOfExpressionSyntax typeOf &&
                Canonical(typeOf.Type) == "ITransactionProcessor"));

    private static bool IsProcessorRegistrationOperation(string name) => name is
        "Add" or "AddScoped" or "AddSingleton" or "AddKeyedScoped" or "AddKeyedSingleton" or
        "Bind" or "Map" or "Register" or "RegisterType" or "As" or "Named" or "Keyed";

    private static void RequireCompleteLoadControlFlow(
        SemanticModel model,
        MethodDeclarationSyntax load,
        SourceFile containerRegistration)
    {
        IMethodSymbol loadSymbol = model.GetDeclaredSymbol(load) as IMethodSymbol
            ?? throw new ExtractionException("BlockProcessingModule.Load did not bind as a method.");
        IParameterSymbol builder = loadSymbol.Parameters.SingleOrDefault(parameter =>
                parameter.Name == "builder" && parameter.Type.ToDisplayString() == "Autofac.ContainerBuilder")
            ?? throw new ExtractionException("BlockProcessingModule.Load must have its admitted ContainerBuilder builder parameter.");

        if (load.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any())
        {
            throw new ExtractionException("BlockProcessingModule.Load must not hide builder operations in local functions.");
        }

        foreach (InvocationExpressionSyntax invocation in load.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!ContainsParameterReference(model, invocation, builder))
            {
                continue;
            }

            if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                !ContainsParameterReference(model, member.Expression, builder) ||
                invocation.ArgumentList.Arguments.Any(argument =>
                    ContainsParameterReference(model, argument, builder)))
            {
                throw new ExtractionException(
                    "BlockProcessingModule.Load must use builder only as the receiver of an admitted builder chain.");
            }

            RequireCompleteBuilderChain(model, invocation, builder, containerRegistration);
        }

        foreach (IdentifierNameSyntax reference in load.DescendantNodes().OfType<IdentifierNameSyntax>()
                     .Where(reference => SymbolEqualityComparer.Default.Equals(
                         model.GetSymbolInfo(reference).Symbol,
                         builder)))
        {
            if (reference.Ancestors().Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax))
            {
                throw new ExtractionException("BlockProcessingModule.Load must not capture builder in a deferred function.");
            }

            InvocationExpressionSyntax? owner = reference.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (owner is null || owner.Expression is not MemberAccessExpressionSyntax member ||
                !member.Expression.FullSpan.Contains(reference.Span))
            {
                throw new ExtractionException(
                    "BlockProcessingModule.Load must keep every builder use in a direct receiver chain.");
            }
        }

    }

    private static void RequireCompleteBuilderChain(
        SemanticModel model,
        ExpressionSyntax expression,
        IParameterSymbol builder,
        SourceFile containerRegistration)
    {
        switch (expression)
        {
            case IdentifierNameSyntax reference when
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(reference).Symbol, builder):
                return;
            case InvocationExpressionSyntax invocation when
                invocation.Expression is MemberAccessExpressionSyntax
                {
                    Expression: ExpressionSyntax receiver,
                    Name: SimpleNameSyntax,
                } member:
            {
                string name = InvocationName(invocation)
                    ?? throw new ExtractionException("BlockProcessingModule.Load contains an unnamed builder operation.");
                if (model.GetOperation(invocation) is not IInvocationOperation operation)
                {
                    RequireAdmittedBuilderCandidates(model, invocation, name, containerRegistration);
                    RequireCompleteBuilderChain(model, receiver, builder, containerRegistration);
                    return;
                }

                IMethodSymbol boundMethod = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol ?? operation.TargetMethod;
                IMethodSymbol original = (boundMethod.ReducedFrom ?? boundMethod).OriginalDefinition;
                string expectedType = name is "AddFirst" or "AddLast"
                    ? "Nethermind.Core.Container.OrderedComponentsContainerBuilderExtensions"
                    : "Nethermind.Core.ContainerBuilderExtensions";
                if (!IsCompleteBuilderOperation(name) ||
                    original.Name != name ||
                    original.ContainingType.OriginalDefinition.ToDisplayString() != expectedType ||
                    name is not ("AddFirst" or "AddLast") &&
                    !HasSourceDeclaration(original.ContainingType, containerRegistration))
                {
                    throw new ExtractionException(
                        $"BlockProcessingModule.Load builder operation '{name}' is not an admitted source operation.");
                }

                RequireCompleteBuilderChain(model, receiver, builder, containerRegistration);
                return;
            }
            default:
                throw new ExtractionException(
                    "BlockProcessingModule.Load builder receiver is not a complete direct extension chain.");
        }
    }

    private static void RequireAdmittedBuilderCandidates(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        string name,
        SourceFile containerRegistration)
    {
        SymbolInfo symbolInfo = model.GetSymbolInfo(invocation);
        IMethodSymbol[] candidates = symbolInfo.CandidateSymbols
            .OfType<IMethodSymbol>()
            .ToArray();
        if (candidates.Length == 0 || symbolInfo.CandidateReason == CandidateReason.Ambiguous ||
            !IsCompleteBuilderOperation(name) ||
            candidates.Any(candidate => !IsAdmittedBuilderCandidate(candidate, name, containerRegistration)))
        {
            throw new ExtractionException(
                $"BlockProcessingModule.Load contains an unbound or non-admitted builder operation '{name}'.");
        }
    }

    private static bool IsAdmittedBuilderCandidate(
        IMethodSymbol candidate,
        string name,
        SourceFile containerRegistration)
    {
        IMethodSymbol original = (candidate.ReducedFrom ?? candidate).OriginalDefinition;
        string expectedType = name is "AddFirst" or "AddLast"
            ? "Nethermind.Core.Container.OrderedComponentsContainerBuilderExtensions"
            : "Nethermind.Core.ContainerBuilderExtensions";
        return original.Name == name &&
            original.ContainingType.OriginalDefinition.ToDisplayString() == expectedType &&
            (name is "AddFirst" or "AddLast" ||
             HasSourceDeclaration(original.ContainingType, containerRegistration));
    }

    private static bool ContainsParameterReference(
        SemanticModel model,
        SyntaxNode syntax,
        IParameterSymbol parameter) => syntax.DescendantNodesAndSelf()
        .OfType<IdentifierNameSyntax>()
        .Any(reference => SymbolEqualityComparer.Default.Equals(
            model.GetSymbolInfo(reference).Symbol,
            parameter));

    private static bool IsCompleteBuilderOperation(string name) => name is
        "Add" or "AddFirst" or "AddKeyedSingleton" or "AddLast" or "AddScoped" or
        "AddScopedOpenGeneric" or "AddSingleton" or "Bind" or "Map";

    private static void RequireSemanticLoadRegistrationGraph(
        SemanticModel model,
        MethodDeclarationSyntax load,
        InvocationExpressionSyntax registration,
        ITypeSymbol serviceType,
        SourceFile containerRegistration)
    {
        InvocationExpressionSyntax[] registrations = load.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => IsSemanticTransactionProcessorRegistration(model, invocation, serviceType))
            .OrderBy(static invocation => invocation.SpanStart)
            .ToArray();
        if (registrations.Length != 1 || !ReferenceEquals(registrations[0], registration))
        {
            throw new ExtractionException(
                "BlockProcessingModule.Load must contain exactly one semantically bound ITransactionProcessor registration with no later override.");
        }

        RequireReachableSyntax(model, load, registration, "standard mainnet transaction-processor registration");
        RequireMustReachExit(
            model,
            load,
            registration,
            "standard mainnet transaction-processor registration");
        RequireCompleteLoadControlFlow(model, load, containerRegistration);
    }

    private static bool IsSemanticTransactionProcessorRegistration(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        ITypeSymbol serviceType)
    {
        if (model.GetOperation(invocation) is not IInvocationOperation operation)
        {
            return false;
        }

        IMethodSymbol method = operation.TargetMethod;
        if (IsProcessorRegistrationOperation(method.Name))
        {
            if (method.TypeArguments.Length != 0 &&
                SymbolEqualityComparer.Default.Equals(method.TypeArguments[0], serviceType))
            {
                return true;
            }

            if (operation.Arguments.Any(argument =>
                    argument.Value is ITypeOfOperation typeOf &&
                    SymbolEqualityComparer.Default.Equals(typeOf.TypeOperand, serviceType)))
            {
                return true;
            }
        }

        return false;
    }

    private static void RequireCompleteLoadRegistrationGraph(
        MethodDeclarationSyntax load,
        InvocationExpressionSyntax registration,
        IReadOnlyList<InvocationExpressionSyntax> registrations)
    {
        if (registrations.Count != 1 || !ReferenceEquals(registrations[0], registration))
        {
            throw new ExtractionException(
                "BlockProcessingModule.Load must contain exactly one ITransactionProcessor registration with no later override.");
        }

        SyntaxNode[] precedingExits = load.DescendantNodes()
            .Where(node => node.SpanStart < registration.SpanStart)
            .Where(static node => node is ReturnStatementSyntax or ThrowStatementSyntax or GotoStatementSyntax or
                BreakStatementSyntax or ContinueStatementSyntax)
            .ToArray();
        if (precedingExits.Length != 0)
        {
            throw new ExtractionException(
                "BlockProcessingModule.Load has a control-flow exit before the admitted ITransactionProcessor registration.");
        }
    }

    private static bool IsBuilderChain(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax { Identifier.ValueText: "builder" } => true,
        InvocationExpressionSyntax invocation when
            invocation.Expression is MemberAccessExpressionSyntax { Expression: ExpressionSyntax receiver } &&
            IsAdmittedBuilderOperation(invocation) => IsBuilderChain(receiver),
        _ => false,
    };

    private static bool IsAdmittedBuilderOperation(InvocationExpressionSyntax invocation) =>
        InvocationName(invocation) is
            "AddFirst" or
            "AddKeyedSingleton" or
            "AddLast" or
            "AddScoped" or
            "AddSingleton" or
            "Bind";

    private static void RequireAdmittedBuilderChain(
        SemanticModel model,
        ExpressionSyntax expression,
        SourceFile containerRegistration)
    {
        switch (expression)
        {
            case IdentifierNameSyntax { Identifier.ValueText: "builder" }:
                return;
            case InvocationExpressionSyntax invocation when
                invocation.Expression is MemberAccessExpressionSyntax
                {
                    Expression: ExpressionSyntax receiver,
                    Name: SimpleNameSyntax,
                }:
            {
                string name = InvocationName(invocation)
                    ?? throw new ExtractionException("The standard mainnet DI receiver contains an unnamed operation.");
                if (!IsAdmittedBuilderOperation(invocation))
                {
                    throw new ExtractionException($"The standard mainnet DI receiver operation '{name}' is not admitted.");
                }

                IInvocationOperation operation = model.GetOperation(invocation) as IInvocationOperation
                    ?? throw new ExtractionException($"The standard mainnet DI receiver operation '{name}' did not bind.");
                IMethodSymbol boundMethod = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol ?? operation.TargetMethod;
                IMethodSymbol original = (boundMethod.ReducedFrom ?? boundMethod).OriginalDefinition;
                string expectedType = name is "AddFirst" or "AddLast"
                    ? "Nethermind.Core.Container.OrderedComponentsContainerBuilderExtensions"
                    : "Nethermind.Core.ContainerBuilderExtensions";
                if (original.Name != name || original.ContainingType.OriginalDefinition.ToDisplayString() != expectedType ||
                    name is not ("AddFirst" or "AddLast") && !HasSourceDeclaration(original.ContainingType, containerRegistration))
                {
                    throw new ExtractionException(
                        $"The standard mainnet DI receiver operation '{name}' bound to {original.ToDisplayString()} instead of the admitted builder extension.");
                }

                RequireAdmittedBuilderChain(model, receiver, containerRegistration);
                return;
            }
            default:
                throw new ExtractionException("The standard mainnet DI registration receiver is not the admitted builder chain.");
        }
    }

    private static bool IsDirectBodyExpression(SyntaxNode node, MethodDeclarationSyntax method)
    {
        ExpressionStatementSyntax? statement = node.AncestorsAndSelf()
            .OfType<ExpressionStatementSyntax>()
            .FirstOrDefault();
        if (statement is null || !ReferenceEquals(statement.Parent, method.Body))
        {
            return false;
        }

        for (SyntaxNode? ancestor = node.Parent;
             ancestor is not null && !ReferenceEquals(ancestor, statement);
             ancestor = ancestor.Parent)
        {
            if (ancestor is not MemberAccessExpressionSyntax and not InvocationExpressionSyntax)
            {
                return false;
            }
        }

        return true;
    }

    private static CSharpCompilation CompileAdmittedSources(IReadOnlyList<SourceFile> sources)
    {
        SyntaxTree stubs = CSharpSyntaxTree.ParseText(
            """
            namespace Nethermind.Int256
            {
                public readonly struct UInt256 { }
            }
            namespace Nethermind.Logging
            {
                public interface ILogger { }
                public interface ILogManager { }
            }
            namespace Nethermind.Core
            {
                public class Address
                {
                    public static Address SystemUser { get; } = new();
                }
                public class Transaction
                {
                    public bool IsSystemTransaction { get; set; }
                    public Address? SenderAddress { get; set; }
                    public bool IsOPSystemTransaction { get; set; }
                }
                public sealed class SystemTransaction : Transaction { }
                public class BlockHeader
                {
                    public ulong GasUsed { get; set; }
                }
                public interface IFlag { }
                public struct OffFlag : IFlag { }
                public struct OnFlag : IFlag { }
            }
            namespace Nethermind.Core.Specs
            {
                public interface ISpecProvider
                {
                    ulong ChainId { get; }
                }
                public interface IReleaseSpec { }
            }
            namespace Nethermind.Core.Container
            {
                using Autofac;

                public static class OrderedComponentsContainerBuilderExtensions
                {
                    public static ContainerBuilder AddFirst<T, TImplementation>(this ContainerBuilder builder) => builder;
                    public static ContainerBuilder AddLast<T, TImplementation>(this ContainerBuilder builder) => builder;
                }
            }
            namespace Nethermind.Evm.GasPolicy
            {
                public interface IGasPolicy<T> { }
                public readonly struct EthereumGasPolicy : IGasPolicy<EthereumGasPolicy> { }
            }
            namespace Nethermind.Evm.Tracing
            {
                public interface ITxTracer { }
            }
            namespace Nethermind.Evm.State
            {
                public interface IWorldState { }
            }
            namespace Nethermind.Evm
            {
                using Nethermind.Evm.GasPolicy;

                public interface ICodeInfoRepository { }
                public interface IVirtualMachine<T> where T : struct, IGasPolicy<T> { }
                public interface IVirtualMachine : IVirtualMachine<EthereumGasPolicy> { }
                public readonly struct TransactionSubstate { }
            }
            namespace Nethermind.Evm.TransactionProcessing
            {
                public interface ITransactionProcessor
                {
                    public interface IBlobBaseFeeCalculator { }
                }
                public readonly record struct GasConsumed(
                    ulong SpentGas,
                    ulong OperationGas,
                    ulong BlockGas = 0,
                    ulong BlockStateGas = 0,
                    ulong MaxUsedGas = 0,
                    ulong GasRefund = 0);
            }
            namespace Nethermind.Init.Modules
            {
                public sealed class TxValidator(ulong chainId) : ITxValidator { }
                public interface ITxValidator
                {
                    static string SpecChangeTxValidatorKey => "spec-change";
                }
                public sealed class SpecChangeTxValidator(ulong chainId) : ITxValidator { }
                public interface IBlockValidator { }
                public sealed class BlockValidator : IBlockValidator { }
                public interface IHeaderValidator { }
                public sealed class HeaderValidator : IHeaderValidator { }
                public interface IUnclesValidator { }
                public sealed class UnclesValidator : IUnclesValidator { }
                public interface ITxGossipPolicy { }
                public sealed class SpecDrivenTxGossipPolicy : ITxGossipPolicy { }
                public interface IBlockPreprocessorStep { }
                public sealed class RecoverSignatures : IBlockPreprocessorStep { }
            }
            """,
            ParseOptions,
            path: "SystemTransactionRouting.semantic-stubs.cs");
        return CSharpCompilation.Create(
            "Nethermind.Evm.Lean.SystemTransactionRoutingAdmittedSources",
            sources.Select(static source => source.Tree).Append(stubs),
            SemanticReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                checkOverflow: false,
                allowUnsafe: false,
                nullableContextOptions: NullableContextOptions.Enable,
                deterministic: true));
    }

    private static MetadataReference[] SemanticReferences()
    {
        string? trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trusted))
        {
            throw new ExtractionException("Trusted platform assemblies were unavailable.");
        }

        return trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(global::Autofac.ContainerBuilder).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static SourceFile Read(string root, string relativePath, string subject)
    {
        string absolutePath = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureWithin(root, absolutePath);
        if (!File.Exists(absolutePath))
        {
            throw new ExtractionException($"Pinned {subject} source was not found: {absolutePath}");
        }

        byte[] bytes = File.ReadAllBytes(absolutePath);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true),
            ParseOptions,
            absolutePath);
        Diagnostic[] errors = tree.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
        {
            throw new ExtractionException(
                $"Pinned {subject} source did not parse: " +
                string.Join(Environment.NewLine, errors.Select(static diagnostic => diagnostic.ToString())));
        }

        return new SourceFile(
            Normalize(relativePath),
            absolutePath,
            Sha256(bytes),
            tree,
            tree.GetCompilationUnitRoot());
    }

    private static void RejectVariantSyntax(SourceFile source)
    {
        SyntaxTrivia[] trivia = source.Root.DescendantTrivia(descendIntoTrivia: true).ToArray();
        if (trivia.Any(static item => item.IsKind(SyntaxKind.DisabledTextTrivia)))
        {
            throw new ExtractionException($"Pinned source {source.RelativePath} must not contain disabled source text.");
        }
        if (trivia.Any(static item => item.IsDirective))
        {
            throw new ExtractionException($"Pinned source {source.RelativePath} must not contain preprocessor directives.");
        }
        if (source.Root.DescendantNodes().OfType<ExternAliasDirectiveSyntax>().Any() ||
            source.Root.DescendantNodes().OfType<AliasQualifiedNameSyntax>().Any() ||
            source.Root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(static directive =>
                directive.Alias is not null ||
                directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ||
                directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
        {
            throw new ExtractionException($"Pinned source {source.RelativePath} must not contain aliases, static imports, or global imports.");
        }
        if (source.Root.DescendantTokens(descendIntoTrivia: false)
            .Any(static token => token.IsKind(SyntaxKind.PartialKeyword)))
        {
            throw new ExtractionException($"Pinned source {source.RelativePath} must not contain partial types.");
        }
    }

    private static MethodDeclarationSyntax RequireMethod(TypeDeclarationSyntax type, string name) => RequireSingle(
        type.Members.OfType<MethodDeclarationSyntax>().Where(method => method.Identifier.ValueText == name),
        $"{type.Identifier.ValueText}.{name} must be declared exactly once.");

    private static void RequireMethodShape(
        MethodDeclarationSyntax method,
        string returnType,
        IReadOnlyList<string> parameters)
    {
        if (!method.Modifiers.Any(SyntaxKind.InternalKeyword) ||
            !method.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            Canonical(method.ReturnType) != returnType ||
            !method.ParameterList.Parameters.Select(Canonical).SequenceEqual(parameters, StringComparer.Ordinal))
        {
            throw new ExtractionException($"SystemTransactionRoutingKernel.{method.Identifier.ValueText} signature changed.");
        }
    }

    private static T RequireSingle<T>(IEnumerable<T> items, string message)
    {
        T[] matches = items.Take(2).ToArray();
        if (matches.Length != 1)
        {
            throw new ExtractionException(message);
        }
        return matches[0];
    }

    private static string NamespaceOf(SyntaxNode node)
    {
        IEnumerable<string> names = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(static declaration => declaration.Name.ToString());
        return string.Join('.', names);
    }

    private static string Canonical(SyntaxNode node) =>
        string.Concat(node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

    private static byte[] EmitLean(IrDocument document, string irHash)
    {
        KernelShape shape = document.Program;
        LeanKernelShape leanShape = TranslateKernelShape(shape);
        return Encoding.UTF8.GetBytes($$"""
        -- Generated by SystemTransactionRoutingExtractor {{document.ExtractorVersion}}.
        -- Ancestor baseline Nethermind commit: {{document.AncestorBaselineCommit}}
        -- {{document.SourceIdentityAuthority}}
        -- IR SHA-256: {{irHash}}
        -- This file intentionally contains definitions only. Proofs live in the handwritten refinement module.
        -- Serialized C# formulas:
        -- useSystemProcessor: {{shape.UseSystemProcessor}}
        -- shouldPayOriginalValue: {{shape.ShouldPayOriginalValue}}
        -- getSystemExecutionOptions: {{shape.GetSystemExecutionOptions}}
        -- participatesInNormalBlockCounters: {{shape.ParticipatesInNormalBlockCounters}}

        namespace Eip803x.Generated.SystemTransactionRoutingKernel

        structure Options where
          commit : Bool
          restore : Bool
          skipValidation : Bool
          warmup : Bool
          buildUp : Bool
          deriving DecidableEq, Repr

        def noneValue : Nat := {{shape.None}}
        def commitValue : Nat := {{shape.Commit}}
        def restoreValue : Nat := {{shape.Restore}}
        def skipValidationValue : Nat := {{shape.SkipValidation}}
        def warmupValue : Nat := {{shape.Warmup}}
        def buildUpValue : Nat := {{shape.BuildUp}}

        def useSystemProcessor (isSystemTransaction : Bool) (options : Options) : Bool :=
          {{leanShape.UseSystemProcessor}}

        def shouldPayOriginalValue (options : Options) : Bool :=
          {{leanShape.ShouldPayOriginalValue}}

        def getSystemExecutionOptions (options : Options) (payOriginalValue : Bool) : Options :=
          {{leanShape.GetSystemExecutionOptions}}

        def participatesInNormalBlockCounters (options : Options) (parallel : Bool) : Bool :=
          {{leanShape.ParticipatesInNormalBlockCounters}}

        end Eip803x.Generated.SystemTransactionRoutingKernel
        """ + "\n");
    }

    private static LeanKernelShape TranslateKernelShape(KernelShape shape) => new(
        UseSystemProcessor: shape.UseSystemProcessor switch
        {
            "isSystemTransaction||options==ExecutionOptions.SkipValidation" =>
                "isSystemTransaction || (options.skipValidation && !options.commit && !options.restore && !options.warmup && !options.buildUp)",
            _ => throw new ExtractionException("The serialized route formula has no admitted Lean translation."),
        },
        ShouldPayOriginalValue: shape.ShouldPayOriginalValue switch
        {
            "(coreOptions&ExecutionOptions.SkipValidation)!=ExecutionOptions.SkipValidation&&(coreOptions&ExecutionOptions.SkipValidationAndCommit)!=ExecutionOptions.SkipValidationAndCommit" =>
                "!options.skipValidation",
            _ => throw new ExtractionException("The serialized pay-value formula has no admitted Lean translation."),
        },
        GetSystemExecutionOptions: shape.GetSystemExecutionOptions switch
        {
            "payOriginalValue?options|ExecutionOptions.SkipValidationAndCommit:options" =>
                "if payOriginalValue then { options with commit := true, skipValidation := true } else options",
            _ => throw new ExtractionException("The serialized system-options formula has no admitted Lean translation."),
        },
        ParticipatesInNormalBlockCounters: shape.ParticipatesInNormalBlockCounters switch
        {
            "(options&ExecutionOptions.SkipValidation)!=ExecutionOptions.SkipValidation&&!parallel" =>
                "!options.skipValidation && !parallel",
            _ => throw new ExtractionException("The serialized counter formula has no admitted Lean translation."),
        });

    private static byte[] Serialize<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n");

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            File.WriteAllBytes(path, bytes);
        }
    }

    private static void EnsureWithin(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ExtractionException($"Path escapes the repository root: {path}");
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed record SourceFile(
        string RelativePath,
        string AbsolutePath,
        string Hash,
        SyntaxTree Tree,
        CompilationUnitSyntax Root);

    private sealed record KernelShape(
        int None,
        int Commit,
        int Restore,
        int SkipValidation,
        int Warmup,
        int BuildUp,
        string UseSystemProcessor,
        string ShouldPayOriginalValue,
        string GetSystemExecutionOptions,
        string ParticipatesInNormalBlockCounters);

    private sealed record LeanKernelShape(
        string UseSystemProcessor,
        string ShouldPayOriginalValue,
        string GetSystemExecutionOptions,
        string ParticipatesInNormalBlockCounters);

    private sealed record AdapterShape(
        string Classifier,
        string Route,
        string SystemFactory,
        string SystemOverride,
        string CounterGate,
        string MainnetProcessor,
        string MainnetRegistration,
        bool IsSystemClassifierBound,
        bool KernelCallsBound,
        bool SystemExecuteOverridesBase,
        bool StandardProcessorInheritsDefaultFactory,
        bool MainnetRegistrationBound,
        bool OptionsForwardedToCounterGate,
        bool PayValueUsesAssignedField,
        bool PayValueOverridesBase,
        bool VirtualPayValueCallsBound,
        bool ScopedBindingImplementationBound,
        bool RoutedSystemForwardingMustReach,
        bool AdmittedArgumentBindingsRemainDirect,
        bool BoundReferenceArgumentsFailClosed,
        bool MainnetRegistrationDominatesLoadExit,
        string CounterClaim);

    private sealed record ScopedRegistrationShape(
        MethodDeclarationSyntax AddScoped,
        MethodDeclarationSyntax BindScoped);

    private sealed record IrDocument(
        int SchemaVersion,
        string ExtractorVersion,
        string AncestorBaselineCommit,
        string SourceIdentityAuthority,
        string Kernel,
        KernelShape Program,
        AdapterShape Adapter);

    private sealed record Manifest(
        int SchemaVersion,
        string ExtractorVersion,
        string CompilerVersion,
        string LanguageVersion,
        string AncestorBaselineCommit,
        string SourceIdentityAuthority,
        string Kernel,
        IReadOnlyList<SourceIdentity> Sources,
        IReadOnlyList<AdmissionIdentity> Admissions,
        ArtifactIdentity Ir,
        ArtifactIdentity Lean,
        string CombinedSha256,
        IReadOnlyList<string> SemanticBindings);

    private sealed record SourceIdentity(string Path, string Sha256);

    private sealed record AdmissionIdentity(string Key, string SourceSha256);

    private sealed record ArtifactIdentity(string Path, string Sha256);
}
