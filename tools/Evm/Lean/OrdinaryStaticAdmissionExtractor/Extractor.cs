// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.OrdinaryStaticAdmissionExtractor;

internal static class Extractor
{
    internal const string PackageRelativePath = "tools/Evm/Lean/OrdinaryStaticAdmissionExtractor/";
    internal const string IrFileName = "OrdinaryStaticAdmissionKernel.ir.json";
    internal const string ManifestFileName = "OrdinaryStaticAdmissionKernel.source-manifest.json";
    internal const string LeanFileName = "OrdinaryStaticAdmissionKernel.lean";
    internal const string ExtractorVersion = "1.1.0-static-admission-initialization";
    internal const string KernelName = "standard-mainnet-ethereum-transaction-processor-static-admission-and-initialization";
    internal const string AcceptanceState = "two-return-value-helper-projections-only";
    internal const string CanonicalInitializerGeneratedPath =
        "tools/Evm/Lean/Eip803x/Generated/TransactionGasInitializationKernel.lean";
    internal const string CanonicalInitializerRefinementPath =
        "tools/Evm/Lean/Eip803x/Refinement/TransactionGasInitialization.lean";
    internal const string CanonicalInitializerIrPath =
        "tools/Evm/Lean/Extractor/Generated/TransactionGasInitializationKernel.ir.json";
    internal const string CanonicalInitializerManifestPath =
        "tools/Evm/Lean/Extractor/Generated/TransactionGasInitializationKernel.source-manifest.json";
    internal const string CanonicalTransactionGasPath =
        "tools/Evm/Lean/Eip803x/TransactionGas.lean";

    internal const string ProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal const string InitializationPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs";
    internal const string GasPolicyPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    internal const string GasPolicyInterfacePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    internal const string TransactionPath = "src/Nethermind/Nethermind.Core/Transaction.cs";
    internal const string TransactionTypePath = "src/Nethermind/Nethermind.Core/TxType.cs";
    internal const string TransactionTypeExtensionsPath =
        "src/Nethermind/Nethermind.Core/TxTypeExtensions.cs";
    internal const string TransactionExtensionsPath =
        "src/Nethermind/Nethermind.Core/TransactionExtensions.cs";
    internal const string SetCodeValidationPath =
        "src/Nethermind/Nethermind.Core/Validation/SetCodeTxValidation.cs";
    internal const string ReleaseSpecExtensionsPath =
        "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs";
    internal const string ReleaseSpecPath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs";
    internal const string BlockHeaderPath = "src/Nethermind/Nethermind.Core/BlockHeader.cs";
    internal const string GasLimitCapPath = "src/Nethermind/Nethermind.Core/Eip7825Constants.cs";
    internal const string ExecutionOptionsPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs";
    internal const string RoutingPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs";
    internal const string MainnetRegistrationPath =
        "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string TransactionProcessorFactoryPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessorFactory.cs";
    internal const string BalTransactionProcessorFactoryPath =
        "src/Nethermind/Nethermind.Consensus/Processing/BalTxProcessorFactory.cs";
    internal const string ValidationResultPath =
        "src/Nethermind/Nethermind.Core/ValidationResult.cs";
    internal const string EvmExceptionPath = "src/Nethermind/Nethermind.Evm/EvmException.cs";

    private const string TransactionProcessorOwner =
        "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>";
    private const string EthereumSpecialization =
        "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessor -> " +
        "EthereumTransactionProcessorBase -> TransactionProcessorBase<EthereumGasPolicy>";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly CSharpParseOptions ParseOptions = new(
        LanguageVersion.CSharp14,
        DocumentationMode.Parse,
        SourceCodeKind.Regular);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly SourceSpec[] SourceClosure =
    [
        new(ProcessorPath, "processor", "ValidateStatic, ValidateGas, CalculateAvailableGas and standard Ethereum specialization"),
        new(InitializationPath, "initialization", "TransactionGasInitializationKernel.TryCreate and result fields"),
        new(GasPolicyPath, "gas-policy", "EthereumGasPolicy initializer bridge and direct gas-field projections"),
        new(GasPolicyInterfacePath, "gas-policy-contract", "IntrinsicGas.StandardGas and MinRequiredGasLimit"),
        new(TransactionPath, "transaction-shape", "sender, nonce, To, DataLength, type and authorization-list shape"),
        new(TransactionTypePath, "transaction-shape", "source TxType underlying byte and SetCode value"),
        new(TransactionTypeExtensionsPath, "transaction-shape", "SupportsAuthorizationList predicate"),
        new(TransactionExtensionsPath, "transaction-shape", "IsAboveInitCode predicate"),
        new(SetCodeValidationPath, "transaction-shape", "SetCode no-creation and authorization-list predicates"),
        new(ReleaseSpecExtensionsPath, "spec-shape", "MaxInitCodeSize definition"),
        new(ReleaseSpecPath, "spec-shape", "EIP-3860/EIP-8037 and MaxCodeSize types"),
        new(BlockHeaderPath, "block-shape", "header GasLimit and GasUsed source widths"),
        new(GasLimitCapPath, "gas-constant", "EIP-7825 default transaction gas cap"),
        new(ExecutionOptionsPath, "dispatch", "exact SkipValidation option bit"),
        new(RoutingPath, "dispatch", "system-processor routing boundary"),
        new(MainnetRegistrationPath, "dispatch", "standard ITransactionProcessor registration"),
        new(TransactionProcessorFactoryPath, "dispatch", "standard generic transaction-processor factory"),
        new(BalTransactionProcessorFactoryPath, "dispatch", "BAL worker transaction-processor factory route"),
        new(ValidationResultPath, "result-shape", "SetCode ValidationResult Error, string conversion, success and Boolean projection"),
        new(EvmExceptionPath, "result-shape", "EvmExceptionType integer values including None"),
    ];

    internal static IReadOnlyList<string> SourcePaths => SourceClosure.Select(static source => source.Path).ToArray();

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        ArtifactSet artifacts = BuildArtifacts(root);
        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(output, LeanFileName));
        EnsureWithin(output, leanPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);

        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        WriteIfChanged(irPath, artifacts.IrBytes);
        WriteIfChanged(manifestPath, artifacts.ManifestBytes);
        WriteIfChanged(leanPath, artifacts.LeanBytes);
        return new(
            irPath,
            manifestPath,
            leanPath,
            artifacts.Document.Sources.Length,
            artifacts.Document.Branches.Length,
            Hash(artifacts.IrBytes),
            Hash(artifacts.ManifestBytes),
            Hash(artifacts.LeanBytes));
    }

    internal static void ValidateExistingArtifacts(string repoRoot, string artifactDirectory, string? leanPath = null)
    {
        ArtifactSet artifacts = BuildArtifacts(Path.GetFullPath(repoRoot));
        RequireEqual(Path.Combine(artifactDirectory, IrFileName), artifacts.IrBytes, "IR");
        RequireEqual(Path.Combine(artifactDirectory, ManifestFileName), artifacts.ManifestBytes, "source manifest");
        RequireEqual(leanPath ?? Path.Combine(artifactDirectory, LeanFileName), artifacts.LeanBytes, "Lean");
    }

    internal static (IrDocument Document, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) BuildForTest(string repoRoot)
    {
        ArtifactSet artifacts = BuildArtifacts(Path.GetFullPath(repoRoot));
        return (artifacts.Document, artifacts.IrBytes, artifacts.LeanBytes, artifacts.ManifestBytes);
    }

    internal static void ValidateSerializedIr(byte[] sourceDerivedIr, byte[] candidateIr)
    {
        IrDocument source = Deserialize<IrDocument>(sourceDerivedIr, "IR");
        IrDocument candidate = Deserialize<IrDocument>(candidateIr, "IR");
        ValidateIr(source);
        ValidateIr(candidate);
        if (!Serialize(source).AsSpan().SequenceEqual(Serialize(candidate)))
            throw new ExtractionException("The candidate static-admission IR differs from the source-derived IR.");
    }

    internal static void ValidateSerializedManifest(byte[] sourceDerivedManifest, byte[] candidateManifest)
    {
        Manifest source = Deserialize<Manifest>(sourceDerivedManifest, "source manifest");
        Manifest candidate = Deserialize<Manifest>(candidateManifest, "source manifest");
        ValidateManifest(source);
        ValidateManifest(candidate);
        if (!Serialize(source).AsSpan().SequenceEqual(Serialize(candidate)))
            throw new ExtractionException("The candidate static-admission source manifest differs from the source-derived manifest.");
    }

    private static ArtifactSet BuildArtifacts(string root)
    {
        SourceFile[] sourceFiles = SourceClosure.Select(source => ReadSource(root, source)).ToArray();
        SyntaxTree[] trees = sourceFiles.Select(Parse).ToArray();
        ValidateSourceClosure(sourceFiles, trees);
        ValidateCanonicalInitializerSource(root, sourceFiles[1]);
        SourceBinding[] bindings = BuildBindings(sourceFiles, trees);

        IrDocument skeleton = BuildIr(bindings, trees);
        byte[] skeletonBytes = Serialize(skeleton);
        string semanticIrSha = Hash(skeletonBytes);
        IrDocument document = skeleton with { IrSha256 = semanticIrSha };
        ValidateIr(document);
        byte[] irBytes = Serialize(document);
        string irSha = Hash(irBytes);
        byte[] leanBytes = LeanEmitter.Emit(document, irSha);
        Manifest manifest = new(
            SchemaVersion: 1,
            ExtractorVersion,
            RoslynVersion: typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion: LanguageVersion.CSharp14.ToDisplayString(),
            KernelName,
            AcceptanceState,
            bindings,
            CanonicalDependencies(root),
            new(IrFileName, irSha),
            new(LeanFileName, Hash(leanBytes)),
            CombinedSourceHash(bindings),
            semanticIrSha);
        ValidateManifest(manifest);
        byte[] manifestBytes = Serialize(manifest);
        return new(document, irBytes, leanBytes, manifestBytes);
    }

    private static IrDocument BuildIr(SourceBinding[] bindings, SyntaxTree[] trees)
    {
        SyntaxNode processorRoot = trees[0].GetCompilationUnitRoot();
        SyntaxNode initializationRoot = trees[1].GetCompilationUnitRoot();
        SyntaxNode gasPolicyInterfaceRoot = trees[3].GetCompilationUnitRoot();
        SyntaxNode transactionRoot = trees[4].GetCompilationUnitRoot();
        SyntaxNode transactionTypeRoot = trees[5].GetCompilationUnitRoot();
        SyntaxNode transactionTypeExtensionsRoot = trees[6].GetCompilationUnitRoot();
        SyntaxNode transactionExtensionsRoot = trees[7].GetCompilationUnitRoot();
        SyntaxNode setCodeValidationRoot = trees[8].GetCompilationUnitRoot();
        SyntaxNode releaseSpecExtensionsRoot = trees[9].GetCompilationUnitRoot();
        SyntaxNode releaseSpecRoot = trees[10].GetCompilationUnitRoot();
        SyntaxNode blockHeaderRoot = trees[11].GetCompilationUnitRoot();

        MethodDeclarationSyntax validateStatic = FindMethod(processorRoot, "TransactionProcessorBase", 1, "ValidateStatic");
        MethodDeclarationSyntax validateGas = FindMethod(processorRoot, "TransactionProcessorBase", 1, "ValidateGas");
        MethodDeclarationSyntax calculateAvailableGas = FindMethod(processorRoot, "TransactionProcessorBase", 1, "CalculateAvailableGas");
        MethodDeclarationSyntax tryCreate = FindMethod(initializationRoot, "TransactionGasInitializationKernel", 0, "TryCreate");
        MethodDeclarationSyntax exceedsCap = FindMethod(gasPolicyInterfaceRoot, "IntrinsicGas", 1, "ExceedsCap");
        MethodDeclarationSyntax bridge = FindMethod(trees[2].GetCompilationUnitRoot(), "EthereumGasPolicy", 0, "TryCreateAvailableFromIntrinsic");

        IrField[] staticFields =
        [
            new("senderPresent", "Bool", MemberDefinition(transactionRoot, "SenderAddress")),
            new("nonce", "Nat", MemberDefinition(transactionRoot, "Nonce")),
            new("toPresent", "Bool", MemberDefinition(transactionRoot, "To")),
            new("dataLength", "Nat", MemberDefinition(transactionRoot, "DataLength")),
            new("transactionType", "Nat", MemberDefinition(transactionRoot, "Type")),
            new("authorizationListPresent", "Bool", MemberDefinition(transactionRoot, "AuthorizationList")),
            new("authorizationListLength", "Nat", MemberDefinition(transactionRoot, "AuthorizationList")),
            new("txGasLimit", "Nat", MemberDefinition(transactionRoot, "GasLimit")),
            new("headerGasLimit", "Nat", MemberDefinition(blockHeaderRoot, "GasLimit")),
            new("headerGasUsed", "Nat", MemberDefinition(blockHeaderRoot, "GasUsed")),
            new("skipValidation", "Bool", "ExecutionOptions.SkipValidation flag bit from source"),
            new("processorParallel", "Bool", MemberDefinition(processorRoot, "_parallel")),
            new("eip3860Enabled", "Bool", MemberDefinition(releaseSpecRoot, "IsEip3860Enabled")),
            new("eip8037Enabled", "Bool", MemberDefinition(releaseSpecRoot, "IsEip8037Enabled")),
            new("maxInitCodeSize", "Int", MemberDefinition(releaseSpecExtensionsRoot, "MaxInitCodeSize")),
            new("standardValue", "Nat", "IntrinsicGas.Standard.Value (source policy field)"),
            new("standardStateReservoir", "Int", "IntrinsicGas.Standard.StateReservoir (source policy field)"),
            new("floorValue", "Nat", "IntrinsicGas.FloorGas.Value (source policy field)"),
        ];
        IrField[] initializationFields =
        [
            new("gasLimit", "Nat", "TryCreate.gasLimit (ulong)"),
            new("intrinsicExecutionGas", "Nat", "TryCreate.intrinsicExecutionGas (ulong)"),
            new("intrinsicStateGas", "Int", "TryCreate.intrinsicStateGas (long)"),
            new("eip8037Enabled", "Bool", "TryCreate.eip8037Enabled"),
            new("executionGasLimitCap", "Nat", "TryCreate.executionGasLimitCap (ulong)"),
        ];
        IrField[] resultFields =
        [
            new("outcome", "InitializationOutcome", "TransactionGasInitializationResult.Outcome"),
            new("value", "Nat", "TransactionGasInitializationResult.Value (ulong)"),
            new("stateReservoir", "Int", "TransactionGasInitializationResult.StateReservoir (long)"),
            new("stateGasUsed", "Int", "TransactionGasInitializationResult.StateGasUsed (long)"),
            new("stateGasSpill", "Int", "TransactionGasInitializationResult.StateGasSpill (long)"),
            new("stateGasSpillRefunded", "Int", "TransactionGasInitializationResult.StateGasSpillRefunded (long)"),
        ];

        IrBranch[] branches = DeriveBranches(validateStatic, validateGas, ComparisonOperatorFromBody(exceedsCap));
        IrShape[] shapes = DeriveShapes(
            transactionExtensionsRoot,
            transactionTypeRoot,
            transactionTypeExtensionsRoot,
            setCodeValidationRoot,
            gasPolicyInterfaceRoot,
            validateStatic);
        IrCallMapping[] callMappings = [DeriveBridgeMapping(bridge)];

        IrFunction[] functions =
        [
            Function("validateStatic", TransactionProcessorOwner, "TransactionResult", Parameters(validateStatic), validateStatic),
            Function("validateGas", TransactionProcessorOwner, "TransactionResult", Parameters(validateGas), validateGas),
            Function("calculateAvailableGas", TransactionProcessorOwner, "TransactionResult", Parameters(calculateAvailableGas), calculateAvailableGas),
            Function("tryCreate", "Nethermind.Evm.GasPolicy.TransactionGasInitializationKernel", "TransactionGasInitializationResult", Parameters(tryCreate), tryCreate),
        ];

        return new(
            SchemaVersion: 1,
            ExtractorVersion,
            KernelName,
            AcceptanceState,
            SourceWidthRules:
            [
                "ulong inputs are represented by Nat values <= 2^64-1",
                "long inputs are represented by Int values in [-2^63, 2^63-1]",
                "int-backed data and authorization lengths are represented by Nat values <= 2^31-1",
                "TxType is represented by its byte value <= 255",
                "all-width theorem has no transaction-validity, affordability, cap, or nonnegative-state premise",
            ],
            staticFields,
            initializationFields,
            resultFields,
            branches,
            functions,
            shapes,
            callMappings,
            bindings,
            ExternalObligations:
            [
                "This package contains two return-value helper projections only; ValidateStatic logging side effects are excluded, and this is not contiguous transaction processing or complete admission.",
                "Sender recovery, intrinsic-gas calculation, account/code state, cryptography, VM execution, fees, and finalization are external.",
                "Virtual overrides in SystemTransactionProcessor, Taiko, XDC, and plugin processors are excluded from the standard specialization.",
                "The BlockProcessingModule registration and routing checks bind standard dispatch but do not prove Autofac or CLR behavior.",
                "Only opts.HasFlag(SkipValidation) is projected into the helper input; raw/composite option routing is external.",
                "Implicit StandardGas addition and legacy allowance subtraction use the current default unchecked C# overflow context; compiler-option semantics are an external assumption.",
                "The extractor performs exact Roslyn syntax admission, not complete project SemanticModel binding; symbol resolution outside the pinned receiver/member/source closure is external.",
                "Descriptions and human-readable detail strings are intentionally outside this typed result; Error, EvmExceptionType, and return-site identity are compared fieldwise.",
                "Nonzero intrinsic state reservoirs are helper-domain witnesses; current ordinary helper inputs supply zero state, while later authorization folding is outside these entrypoints.",
                "A reservoir above 2^63 is preserved as a signed-wrap helper-domain boundary and is not asserted reachable in production.",
            ],
            IrSha256: "");
    }

    private static IrFunction Function(
        string name,
        string owner,
        string returnType,
        IrField[] parameters,
        MethodDeclarationSyntax sourceMethod) =>
        new(name, owner, returnType, parameters, Operations(sourceMethod), TokenHash(sourceMethod));

    private static IrField[] Parameters(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters
            .Select(parameter => new IrField(
                parameter.Identifier.Text,
                parameter.Type?.ToString() ?? throw new ExtractionException(
                    $"Source parameter {parameter.Identifier.Text} on {method.Identifier.Text} has no type."),
                Compact(parameter.ToString())))
            .ToArray();

    private static IrOperation[] Operations(MethodDeclarationSyntax method)
    {
        List<IrOperation> operations = [];
        foreach (SyntaxNode node in method.DescendantNodes())
        {
            switch (node)
            {
                case LocalDeclarationStatementSyntax declaration:
                    operations.Add(new(IrOperationKind.Assignment, Compact(declaration.ToString()),
                        Operands(declaration.Declaration, declaration.Declaration.Variables.Select(static variable => variable.Identifier.Text)),
                        ComparisonOperatorIn(declaration.Declaration), Reduction(declaration.Declaration)));
                    break;
                case IfStatementSyntax conditional:
                    operations.Add(new(IrOperationKind.Guard, Compact(conditional.Condition.ToString()),
                        Operands(conditional.Condition, []), ComparisonOperator(conditional.Condition), IrReductionKind.None));
                    break;
                case InvocationExpressionSyntax invocation:
                    operations.Add(new(IrOperationKind.Call, Compact(invocation.ToString()),
                        Operands(invocation, []), IrComparisonOperator.None, Reduction(invocation)));
                    break;
                case ReturnStatementSyntax @return:
                    operations.Add(new(IrOperationKind.Return, Compact(@return.Expression?.ToString() ?? ""),
                        Operands(@return.Expression, []), IrComparisonOperator.None, IrReductionKind.None));
                    break;
            }
        }

        if (operations.Count == 0)
            throw new ExtractionException($"Admitted method {method.Identifier.Text} lowered to no semantic operations.");
        return operations.ToArray();
    }

    private static IrBranch[] DeriveBranches(
        MethodDeclarationSyntax validateStatic,
        MethodDeclarationSyntax validateGas,
        IrComparisonOperator capComparisonOperator)
    {
        BranchSpec[] specifications =
        [
            new("sender-absent", 1, "ValidateStatic", "senderPresent = false", "senderNotSpecified", "ValidateStatic.sender-absent", "tx.SenderAddress is null"),
            new("nonce-overflow", 2, "ValidateStatic", "!skipValidation && nonce = UInt64.max", "nonceOverflow", "ValidateStatic.nonce-overflow", "validate&&tx.Nonce==ulong.MaxValue"),
            new("initcode-oversize", 3, "ValidateStatic", "isCreation && eip3860Enabled && dataLength > maxInitCodeSize", "transactionSizeOverMaxInitCodeSize", "ValidateStatic.initcode-oversize", "tx.IsAboveInitCode(spec)"),
            new("setcode-creation", 4, "ValidateStatic", "isSetCode && isCreation", "malformedTransaction", "ValidateStatic.setcode-creation", "!noCreation"),
            new("setcode-auth-empty", 5, "ValidateStatic", "isSetCode && (!authorizationListPresent || authorizationListLength = 0)", "malformedTransaction", "ValidateStatic.setcode-auth-empty", "!authList"),
            new("intrinsic-cap", 6, "ValidateStatic", "eip8037Enabled && (standardValue > cap || floorValue > cap)", "gasLimitBelowIntrinsicGas", "ValidateStatic.intrinsic-cap", "intrinsicGas.ExceedsCap"),
            new("execution-intrinsic", 7, "ValidateStatic", "txGasLimit < standardValue", "gasLimitBelowIntrinsicGas", "ValidateStatic.execution-intrinsic", "tx.GasLimit<standardGasUsed"),
            new("floor-intrinsic", 8, "ValidateStatic", "txGasLimit < floorValue", "gasLimitBelowFloorGas", "ValidateStatic.floor-intrinsic", "tx.GasLimit<floorGasUsed"),
            new("minimum-intrinsic", 9, "ValidateGas", "txGasLimit < max(standardValue + uncheckedCast(standardStateReservoir), floorValue)", "gasLimitBelowIntrinsicGas", "ValidateGas.minimum-intrinsic", "tx.GasLimit<minGasRequired"),
            new("eip8037-block-limit", 10, "ValidateGas", "!skipValidation && eip8037Enabled && txGasLimit > headerGasLimit", "blockGasLimitExceeded", "ValidateGas.eip8037-block-limit", "tx.GasLimit>header.GasLimit"),
            new("legacy-block-limit", 11, "ValidateGas", "!skipValidation && !eip8037Enabled && txGasLimit > headerGasLimit - (processorParallel ? 0 : headerGasUsed)", "blockGasLimitExceeded", "ValidateGas.legacy-block-limit", "tx.GasLimit>maxTransactionGasLimit"),
            new("ok", 12, "ValidateGas", "all earlier predicates false", "none", "ValidateGas.ok", "returnTransactionResult.Ok"),
        ];

        List<IrBranch> branches = [];
        foreach (BranchSpec specification in specifications)
        {
            MethodDeclarationSyntax method = specification.SourceMethod == "ValidateStatic" ? validateStatic : validateGas;
            (IfStatementSyntax? guard, ReturnStatementSyntax @return, IfStatementSyntax[] ancestorPath) = MatchReturn(method, specification);
            string sourceCondition = guard is null ? "else" : Compact(guard.Condition.ToString());
            string sourceReturn = Compact(@return.Expression?.ToString() ?? "");
            IrComparisonOperator comparisonOperator = specification.Id == "intrinsic-cap"
                ? capComparisonOperator
                : guard is null
                ? IrComparisonOperator.None
                : ComparisonOperator(guard.Condition);
            IrConditionKind conditionKind = ConditionKind(guard?.Condition, comparisonOperator);
            IrGuard[] guardPath = LowerGuardPath(specification, @return, ancestorPath);
            branches.Add(new(
                specification.Id,
                specification.Ordinal,
                specification.SourceMethod,
                specification.Condition,
                sourceCondition,
                sourceReturn,
                guardPath,
                conditionKind,
                Operands(guard?.Condition, []),
                comparisonOperator,
                specification.Error,
                "none",
                specification.ReturnSite));
        }

        return branches.ToArray();
    }

    private static IrShape[] DeriveShapes(
        SyntaxNode transactionExtensionsRoot,
        SyntaxNode transactionTypeRoot,
        SyntaxNode transactionTypeExtensionsRoot,
        SyntaxNode setCodeValidationRoot,
        SyntaxNode gasPolicyInterfaceRoot,
        MethodDeclarationSyntax validateStatic)
    {
        MethodDeclarationSyntax isAboveInitCode = FindAnyMethod(transactionExtensionsRoot, "IsAboveInitCode");
        MethodDeclarationSyntax supportsAuthorizationList = FindAnyMethod(transactionTypeExtensionsRoot, "SupportsAuthorizationList");
        MethodDeclarationSyntax noContractCreation = FindAnyMethod(setCodeValidationRoot, "ValidateNoContractCreation");
        MethodDeclarationSyntax authorizationList = FindAnyMethod(setCodeValidationRoot, "ValidateAuthorizationList");
        MethodDeclarationSyntax exceedsCap = FindMethod(gasPolicyInterfaceRoot, "IntrinsicGas", 1, "ExceedsCap");
        PropertyDeclarationSyntax standardGas = FindProperty(gasPolicyInterfaceRoot, "StandardGas");
        PropertyDeclarationSyntax minRequiredGasLimit = FindProperty(gasPolicyInterfaceRoot, "MinRequiredGasLimit");
        EnumMemberDeclarationSyntax setCode = transactionTypeRoot.DescendantNodes()
            .OfType<EnumMemberDeclarationSyntax>()
            .SingleOrDefault(member => member.Identifier.Text == "SetCode")
            ?? throw new ExtractionException("TxType.SetCode was not found while lowering shape definitions.");
        LocalDeclarationStatementSyntax validate = validateStatic.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .SingleOrDefault(local => local.Declaration.Variables.Any(variable => variable.Identifier.Text == "validate"))
            ?? throw new ExtractionException("ValidateStatic validation-option assignment was not found.");
        ReturnStatementSyntax exceedsCapReturn = exceedsCap.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .SingleOrDefault()
            ?? throw new ExtractionException("IntrinsicGas.ExceedsCap return expression was not found.");

        return
        [
            Shape("isAboveInitCode", "TransactionExtensions.IsAboveInitCode", isAboveInitCode, isAboveInitCode.ExpressionBody?.Expression),
            Shape("supportsAuthorizationList", "TxTypeExtensions.SupportsAuthorizationList", supportsAuthorizationList, supportsAuthorizationList.ExpressionBody?.Expression),
            Shape("setCodeValue", "TxType.SetCode", setCode.ToString(), IrConditionKind.Comparison, IrComparisonOperator.Equal,
                IrReductionKind.None, Operands(setCode, [])),
            Shape("setCodeNoCreation", "SetCodeTxValidation.ValidateNoContractCreation", noContractCreation,
                noContractCreation.ExpressionBody?.Expression),
            PatternShape("setCodeAuthorizationList", "SetCodeTxValidation.ValidateAuthorizationList", authorizationList),
            Shape("intrinsicStandardGas", "IntrinsicGas.StandardGas", standardGas, standardGas.ExpressionBody?.Expression),
            Shape("intrinsicMinRequiredGasLimit", "IntrinsicGas.MinRequiredGasLimit", minRequiredGasLimit,
                minRequiredGasLimit.ExpressionBody?.Expression),
            Shape("intrinsicExceedsCap", "IntrinsicGas.ExceedsCap", exceedsCap, exceedsCapReturn.Expression),
            Shape("skipValidation", "ValidateStatic.validate", validate, validate.Declaration.Variables
                .Single(variable => variable.Identifier.Text == "validate").Initializer?.Value),
        ];
    }

    private static IrShape Shape(
        string id,
        string sourceMember,
        SyntaxNode source,
        ExpressionSyntax? expression) =>
        Shape(id, sourceMember, Compact(source.ToString()), ConditionKind(expression, ComparisonOperatorIn(source)),
            ComparisonOperatorIn(source), Reduction(source), Operands(source, []));

    private static IrShape Shape(
        string id,
        string sourceMember,
        string sourceExpression,
        IrConditionKind conditionKind,
        IrComparisonOperator comparisonOperator,
        IrReductionKind reduction,
        IrOperand[] operands) =>
        new(id, sourceMember, sourceExpression,
            id == "skipValidation" && sourceExpression.Contains("!", StringComparison.Ordinal)
                ? IrConditionKind.NegatedPredicate
                : conditionKind,
            comparisonOperator, reduction, operands);

    private static IrShape PatternShape(string id, string sourceMember, SyntaxNode source)
    {
        string sourceExpression = Compact(source.ToString());
        IrComparisonOperator comparisonOperator = sourceExpression.Contains("Length:0", StringComparison.Ordinal)
            ? IrComparisonOperator.Equal
            : IrComparisonOperator.None;
        IrOperand[] operands = Operands(source, []);
        return new(id, sourceMember, sourceExpression,
            comparisonOperator is IrComparisonOperator.None ? IrConditionKind.Predicate : IrConditionKind.Comparison,
            comparisonOperator, IrReductionKind.None, operands);
    }

    private static IrReductionKind Reduction(SyntaxNode source)
    {
        string compact = Compact(source.ToString());
        if (compact.Contains("Math.Max", StringComparison.Ordinal)) return IrReductionKind.Maximum;
        if (compact.Contains("Math.Min", StringComparison.Ordinal)) return IrReductionKind.Minimum;
        if (compact.Contains("+", StringComparison.Ordinal)) return IrReductionKind.Addition;
        if (compact.Contains("-", StringComparison.Ordinal)) return IrReductionKind.Subtraction;
        return IrReductionKind.None;
    }

    private static IrCallMapping DeriveBridgeMapping(MethodDeclarationSyntax bridge)
    {
        InvocationExpressionSyntax call = bridge.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(invocation => Compact(invocation.Expression.ToString())
                .Contains("TransactionGasInitializationKernel.TryCreate", StringComparison.Ordinal))
            ?? throw new ExtractionException("EthereumGasPolicy bridge lost its TransactionGasInitializationKernel.TryCreate call.");
        InitializerExpressionSyntax initializer = bridge.DescendantNodes()
            .OfType<InitializerExpressionSyntax>()
            .SingleOrDefault(candidate => candidate.IsKind(SyntaxKind.ObjectInitializerExpression) &&
                candidate.Expressions.OfType<AssignmentExpressionSyntax>().Any(assignment => assignment.Left.ToString() == "Value"))
            ?? throw new ExtractionException("EthereumGasPolicy bridge lost the available-policy object initializer.");
        AssignmentExpressionSyntax[] assignments = initializer.Expressions
            .OfType<AssignmentExpressionSyntax>()
            .ToArray();
        string[] outputFields = assignments.Select(assignment => assignment.Left.ToString()).ToArray();
        string[] outputExpressions = assignments.Select(assignment => Compact(assignment.Right.ToString())).ToArray();
        if (!outputFields.ToHashSet(StringComparer.Ordinal).SetEquals(["Value", "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded"]))
            throw new ExtractionException("EthereumGasPolicy bridge output field order changed.");
        AssignmentExpressionSyntax defaultAssignment = bridge.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .SingleOrDefault(assignment => assignment.Left.ToString() == "available" &&
                assignment.Right.IsKind(SyntaxKind.DefaultLiteralExpression))
            ?? throw new ExtractionException("EthereumGasPolicy bridge lost the default out-policy assignment.");
        return new(
            "available-policy-bridge",
            "EthereumGasPolicy.TryCreateAvailableFromIntrinsic",
            call.ArgumentList.Arguments.Select(argument => Compact(argument.Expression.ToString())).ToArray(),
            outputFields,
            outputExpressions,
            Compact(defaultAssignment.Right.ToString()));
    }

    private static (IfStatementSyntax? Guard, ReturnStatementSyntax Return, IfStatementSyntax[] GuardPath) MatchReturn(
        MethodDeclarationSyntax method,
        BranchSpec specification)
    {
        // The source return order is the semantic order.  Binding by an exact
        // condition string would make a supported operator change (for example
        // `<` to `<=`) look like a missing branch instead of changing the
        // lowered comparison.  We still require every return to have the
        // expected source error and a direct enclosing `if` guard.
        ReturnStatementSyntax[] returns = method.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .OrderBy(@return => @return.SpanStart)
            .ToArray();
        ReturnStatementSyntax candidate;
        if (specification.SourceMethod == "ValidateStatic")
        {
            int index = specification.Ordinal - 1;
            if (index < 0 || index >= returns.Length)
                throw new ExtractionException($"Could not lower return for branch {specification.Id} from {method.Identifier.Text}: return count changed.");
            candidate = returns[index];
        }
        else
        {
            ReturnStatementSyntax[] matching = returns.Where(@return => ReturnMatches(@return, specification)).ToArray();
            candidate = specification.Id switch
            {
                "eip8037-block-limit" or "legacy-block-limit" when matching.Length >= 2 =>
                    matching[specification.Id == "eip8037-block-limit" ? 0 : 1],
                "ok" when matching.Length > 0 => matching[^1],
                _ when matching.Length > 0 => matching[0],
                _ => throw new ExtractionException($"Could not lower return for branch {specification.Id} from {method.Identifier.Text}: return mapping changed."),
            };
        }
        if (!ReturnMatches(candidate, specification))
            throw new ExtractionException($"Source return order or error mapping changed at branch {specification.Id}.");

        IfStatementSyntax? guard = specification.Id == "ok"
            ? null
            : candidate.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        if (specification.Id != "ok" && guard is null)
            throw new ExtractionException($"Could not lower guard for branch {specification.Id} from {method.Identifier.Text}.");
        IfStatementSyntax[] guardPath = candidate.Ancestors().OfType<IfStatementSyntax>()
            .OrderBy(static ancestor => ancestor.SpanStart)
            .ToArray();
        ValidateGuardPath(specification, guardPath);
        return (guard, candidate, guardPath);
    }

    private static void ValidateGuardPath(BranchSpec specification, IReadOnlyList<IfStatementSyntax> path)
    {
        switch (specification.Id)
        {
            case "sender-absent":
                RequirePathLength(specification, path, 1);
                RequireConditionExact(path[0], "tx.SenderAddress is null", specification.Id);
                break;
            case "nonce-overflow":
                RequirePathLength(specification, path, 1);
                if (path[0].Condition is not BinaryExpressionSyntax nonceGuard ||
                    !nonceGuard.IsKind(SyntaxKind.LogicalAndExpression))
                    throw new ExtractionException($"Branch {specification.Id} guard connective changed; expected logical AND.");
                RequireExpression(nonceGuard.Left, "validate", $"Branch {specification.Id} validation operand");
                RequireOrderedComparison(nonceGuard.Right, "tx.Nonce", "ulong.MaxValue", specification.Id);
                break;
            case "initcode-oversize":
                RequirePathLength(specification, path, 1);
                RequireConditionExact(path[0], "tx.IsAboveInitCode(spec)", specification.Id);
                break;
            case "setcode-creation":
                RequirePathLength(specification, path, 2);
                RequireConditionExact(path[0], "tx.SupportsAuthorizationList", specification.Id);
                RequireConditionExact(path[1], "!noCreation", specification.Id);
                break;
            case "setcode-auth-empty":
                RequirePathLength(specification, path, 2);
                RequireConditionExact(path[0], "tx.SupportsAuthorizationList", specification.Id);
                RequireConditionExact(path[1], "!authList", specification.Id);
                break;
            case "intrinsic-cap":
                RequirePathLength(specification, path, 1);
                RequireConditionExact(path[0],
                    "spec.IsEip8037Enabled && intrinsicGas.ExceedsCap(Eip7825Constants.DefaultTxGasLimitCap, out ulong execution, out ulong floor)",
                    specification.Id);
                break;
            case "execution-intrinsic":
                RequirePathLength(specification, path, 1);
                RequireOrderedComparison(path[0].Condition, "tx.GasLimit", "standardGasUsed", specification.Id);
                break;
            case "floor-intrinsic":
                RequirePathLength(specification, path, 1);
                RequireOrderedComparison(path[0].Condition, "tx.GasLimit", "floorGasUsed", specification.Id);
                break;
            case "minimum-intrinsic":
                RequirePathLength(specification, path, 1);
                RequireOrderedComparison(path[0].Condition, "tx.GasLimit", "minGasRequired", specification.Id);
                break;
            case "eip8037-block-limit":
                RequirePathLength(specification, path, 3);
                RequireConditionExact(path[0], "validate", specification.Id);
                RequireConditionExact(path[1], "spec.IsEip8037Enabled", specification.Id);
                RequireOrderedComparison(path[2].Condition, "tx.GasLimit", "header.GasLimit", specification.Id);
                break;
            case "legacy-block-limit":
                RequirePathLength(specification, path, 2);
                RequireConditionExact(path[0], "validate", specification.Id);
                RequireOrderedComparison(path[1].Condition, "tx.GasLimit", "maxTransactionGasLimit", specification.Id);
                break;
            case "ok":
                RequirePathLength(specification, path, 0);
                break;
            default:
                throw new ExtractionException($"Unsupported static-admission branch {specification.Id}.");
        }
    }

    private static IrGuard[] LowerGuardPath(
        BranchSpec specification,
        ReturnStatementSyntax @return,
        IReadOnlyList<IfStatementSyntax> ancestorPath)
    {
        List<IrGuard> guards = ancestorPath.Select(ancestor =>
        {
            IrComparisonOperator comparisonOperator = ComparisonOperator(ancestor.Condition);
            return new IrGuard(
                Compact(ancestor.Condition.ToString()),
                ConditionKind(ancestor.Condition, comparisonOperator),
                comparisonOperator,
                Operands(ancestor.Condition, []),
                IsSyntheticFallthrough: false);
        }).ToList();

        if (specification.Id == "legacy-block-limit")
        {
            IfStatementSyntax? eipGuard = ancestorPath[0].Statement.DescendantNodes()
                .OfType<IfStatementSyntax>()
                .Where(candidate => candidate.SpanStart < @return.SpanStart &&
                    ContainsOperand(candidate.Condition, "spec.IsEip8037Enabled"))
                .OrderBy(candidate => candidate.SpanStart)
                .FirstOrDefault();
            if (eipGuard is null || eipGuard.Condition.DescendantNodesAndSelf()
                    .OfType<PrefixUnaryExpressionSyntax>()
                    .Any(prefix => prefix.IsKind(SyntaxKind.LogicalNotExpression) &&
                        ContainsOperand(prefix.Operand, "spec.IsEip8037Enabled")))
                throw new ExtractionException("Legacy ValidateGas branch lost the dominating positive EIP-8037 guard.");
            ReturnStatementSyntax[] dominatedReturns = eipGuard.Statement.DescendantNodes()
                .OfType<ReturnStatementSyntax>()
                .Where(candidate => candidate.SpanStart < @return.SpanStart)
                .ToArray();
            if (!dominatedReturns.Any(ReturnIsOk))
                throw new ExtractionException("Legacy ValidateGas branch lost the EIP-8037 early-Ok fallthrough boundary.");
            guards.Insert(1, new IrGuard(
                $"!({Compact(eipGuard.Condition.ToString())})",
                IrConditionKind.NegatedPredicate,
                IrComparisonOperator.NotEqual,
                Operands(eipGuard.Condition, []),
                IsSyntheticFallthrough: true));
        }

        return guards.ToArray();
    }

    private static bool ReturnIsOk(ReturnStatementSyntax @return) =>
        ReturnExpressionMatches(@return.Expression, "TransactionResult.Ok");

    private static void RequirePathLength(BranchSpec specification, IReadOnlyList<IfStatementSyntax> path, int expected)
    {
        if (path.Count != expected)
            throw new ExtractionException($"Branch {specification.Id} guard nesting changed: expected {expected} ancestors, found {path.Count}.");
    }

    private static void RequireConditionExact(IfStatementSyntax conditional, string expected, string branch)
    {
        if (Compact(conditional.Condition.ToString()) != Compact(expected))
            throw new ExtractionException($"Branch {branch} guard expression changed.");
    }

    private static void RequireOrderedComparison(ExpressionSyntax expression, string left, string right, string branch)
    {
        if (expression is not BinaryExpressionSyntax comparison ||
            ComparisonOperator(comparison) is IrComparisonOperator.None ||
            Compact(comparison.Left.ToString()) != Compact(left) ||
            Compact(comparison.Right.ToString()) != Compact(right))
            throw new ExtractionException($"Branch {branch} comparison operands or order changed.");
    }

    private static bool ReturnMatches(ReturnStatementSyntax @return, BranchSpec specification)
    {
        string expected = specification.Id switch
        {
            "sender-absent" => "TransactionResult.SenderNotSpecified",
            "nonce-overflow" => "TransactionResult.NonceOverflow",
            "initcode-oversize" => "TransactionResult.TransactionSizeOverMaxInitCodeSize",
            "setcode-creation" or "setcode-auth-empty" =>
                "TransactionResult.ErrorType.MalformedTransaction.WithDetail",
            "intrinsic-cap" or "execution-intrinsic" or "minimum-intrinsic" =>
                "TransactionResult.ErrorType.GasLimitBelowIntrinsicGas.WithDetail",
            "floor-intrinsic" => "TransactionResult.ErrorType.GasLimitBelowFloorGas.WithDetail",
            "eip8037-block-limit" or "legacy-block-limit" => "TransactionResult.BlockGasLimitExceeded",
            "ok" => "TransactionResult.Ok",
            _ => string.Empty,
        };
        return ReturnExpressionMatches(@return.Expression, expected);
    }

    private static bool ReturnExpressionMatches(ExpressionSyntax? expression, string expected)
    {
        if (expression is null || expected.Length == 0) return false;
        if (expected.EndsWith(".WithDetail", StringComparison.Ordinal))
        {
            return expression is InvocationExpressionSyntax invocation &&
                Compact(invocation.Expression.ToString()) == expected &&
                invocation.ArgumentList.Arguments.Count == 1;
        }

        if (expected.StartsWith("ValidateGas(", StringComparison.Ordinal))
            return expression is InvocationExpressionSyntax && Compact(expression.ToString()) == expected;

        return expression is MemberAccessExpressionSyntax && Compact(expression.ToString()) == expected;
    }

    private static IrConditionKind ConditionKind(ExpressionSyntax? condition, IrComparisonOperator comparisonOperator)
    {
        if (condition is null) return IrConditionKind.Else;
        if (condition.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>().Any(binary =>
                binary.IsKind(SyntaxKind.LogicalAndExpression)))
            return IrConditionKind.Conjunction;
        if (condition.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>().Any(binary =>
                binary.IsKind(SyntaxKind.LogicalOrExpression)))
            return IrConditionKind.Disjunction;
        if (comparisonOperator is IrComparisonOperator.NotEqual or IrComparisonOperator.IsNotNull)
            return IrConditionKind.NegatedPredicate;
        if (comparisonOperator is IrComparisonOperator.None)
            return IrConditionKind.Predicate;
        return IrConditionKind.Comparison;
    }

    private static IrComparisonOperator ComparisonOperator(ExpressionSyntax condition)
    {
        string compact = Compact(condition.ToString());
        if (compact.Contains("isnull", StringComparison.Ordinal)) return IrComparisonOperator.IsNull;
        if (compact.Contains("isnotnull", StringComparison.Ordinal)) return IrComparisonOperator.IsNotNull;
        SyntaxToken token = condition.DescendantTokens().FirstOrDefault(token => token.Kind() switch
        {
            SyntaxKind.EqualsEqualsToken or SyntaxKind.ExclamationEqualsToken or
            SyntaxKind.LessThanToken or SyntaxKind.LessThanEqualsToken or
            SyntaxKind.GreaterThanToken or SyntaxKind.GreaterThanEqualsToken => true,
            _ => false,
        });
        return token.Kind() switch
        {
            SyntaxKind.EqualsEqualsToken => IrComparisonOperator.Equal,
            SyntaxKind.ExclamationEqualsToken => IrComparisonOperator.NotEqual,
            SyntaxKind.LessThanToken => IrComparisonOperator.LessThan,
            SyntaxKind.LessThanEqualsToken => IrComparisonOperator.LessThanOrEqual,
            SyntaxKind.GreaterThanToken => IrComparisonOperator.GreaterThan,
            SyntaxKind.GreaterThanEqualsToken => IrComparisonOperator.GreaterThanOrEqual,
            _ when compact.StartsWith("!", StringComparison.Ordinal) => IrComparisonOperator.NotEqual,
            _ => IrComparisonOperator.None,
        };
    }

    private static IrComparisonOperator ComparisonOperatorFromBody(MethodDeclarationSyntax method)
    {
        IrComparisonOperator operatorFromBody = method.DescendantNodes()
            .OfType<BinaryExpressionSyntax>()
            .Select(binary => ComparisonOperator(binary))
            .FirstOrDefault(operatorValue => operatorValue is not IrComparisonOperator.None);
        return operatorFromBody is IrComparisonOperator.None
            ? throw new ExtractionException($"Could not lower the comparison operator in {method.Identifier.Text}.")
            : operatorFromBody;
    }

    private static IrComparisonOperator ComparisonOperatorIn(SyntaxNode node) =>
        node.DescendantNodesAndSelf()
            .OfType<BinaryExpressionSyntax>()
            .Select(ComparisonOperator)
            .FirstOrDefault(operatorValue => operatorValue is not IrComparisonOperator.None);

    private static IrOperand[] Operands(SyntaxNode? node, IEnumerable<string> declaredNames)
    {
        if (node is null) return [];
        HashSet<string> declared = declaredNames.ToHashSet(StringComparer.Ordinal);
        List<IrOperand> operands = node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Select(identifier => identifier.Identifier.Text)
            .Distinct(StringComparer.Ordinal)
            .Select(name => new IrOperand(
                name,
                declared.Contains(name) ? IrOperandKind.Derived : IsConstant(name) ? IrOperandKind.Constant : IsCallName(node, name) ? IrOperandKind.Call : IrOperandKind.Field,
                name))
            .ToList();
        foreach (LiteralExpressionSyntax literal in node.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>())
        {
            if (literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                string value = literal.Token.Text;
                if (operands.All(operand => operand.Name != value))
                    operands.Add(new(value, IrOperandKind.Constant, value));
            }
        }

        return operands.ToArray();
    }

    private static bool IsCallName(SyntaxNode node, string name) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation =>
            invocation.Expression.ToString().EndsWith(name, StringComparison.Ordinal));

    private static bool IsConstant(string name) =>
        name is "MaxValue" or "DefaultTxGasLimitCap" or "SkipValidation" or "SetCode" or "None" or "Ok";

    private static string MemberDefinition(SyntaxNode root, string memberName)
    {
        MemberDeclarationSyntax? member = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate switch
            {
                PropertyDeclarationSyntax property => property.Identifier.Text == memberName,
                FieldDeclarationSyntax field => field.Declaration.Variables.Any(variable => variable.Identifier.Text == memberName),
                _ => false,
            });
        return member is null
            ? throw new ExtractionException($"Source-bound member {memberName} was not found.")
            : Compact(member.ToString());
    }

    private static SourceBinding[] BuildBindings(SourceFile[] sources, SyntaxTree[] trees)
    {
        SourceBinding[] bindings = new SourceBinding[sources.Length];
        for (int i = 0; i < sources.Length; i++)
        {
            SourceSpec specification = SourceClosure[i];
            SyntaxNode root = trees[i].GetCompilationUnitRoot();
            (string owner, string member, string signature) = i switch
            {
                0 => (TransactionProcessorOwner, "ValidateStatic;ValidateGas;CalculateAvailableGas;EthereumTransactionProcessor", EthereumSpecialization),
                1 => ("Nethermind.Evm.GasPolicy.TransactionGasInitializationKernel", "TryCreate;TransactionGasInitializationResult", "TryCreate(ulong,ulong,long,bool,ulong) -> all six result fields"),
                2 => ("Nethermind.Evm.GasPolicy.EthereumGasPolicy", "TryCreateAvailableFromIntrinsic;GetRemainingGas;GetStateReservoir", "initializer bridge plus direct Value and StateReservoir projections"),
                3 => ("Nethermind.Evm.GasPolicy.IGasPolicy<TSelf>", "IntrinsicGas<TGasPolicy>", "StandardGas;MinRequiredGasLimit"),
                4 => ("Nethermind.Core.Transaction", "IsContractCreation;DataLength;SupportsAuthorizationList;AuthorizationList", "To;Data;SenderAddress;Nonce;GasLimit;Type"),
                5 => ("Nethermind.Core.TxType", "SetCode", "byte-backed enum value 4"),
                6 => ("Nethermind.Core.TxTypeExtensions", "SupportsAuthorizationList", "TxType == TxType.SetCode"),
                7 => ("Nethermind.Core.TransactionExtensions", "IsAboveInitCode", "creation && EIP-3860 && DataLength > MaxInitCodeSize"),
                8 => ("Nethermind.Core.Validation.SetCodeTxValidation", "ValidateNoContractCreation;ValidateAuthorizationList", "creation; null or Length == 0"),
                9 => ("Nethermind.Core.Specs.IReleaseSpecExtensions", "MaxInitCodeSize", "2 * spec.MaxCodeSize"),
                10 => ("Nethermind.Core.Specs.IReleaseSpec", "IsEip3860Enabled;IsEip8037Enabled;MaxCodeSize", "fork flags and long max code"),
                11 => ("Nethermind.Core.BlockHeader", "GasLimit;GasUsed", "ulong"),
                12 => ("Nethermind.Core.Eip7825Constants", "DefaultTxGasLimitCap", "ulong 16_777_216"),
                13 => ("Nethermind.Evm.TransactionProcessing.ExecutionOptions", "SkipValidation", "exact flag bit"),
                14 => ("Nethermind.Evm.TransactionProcessing.SystemTransactionRoutingKernel", "UseSystemProcessor", "isSystemTransaction || options == SkipValidation"),
                15 => ("Nethermind.Init.Modules.BlockProcessingModule", "ITransactionProcessor registration", "AddScoped<ITransactionProcessor, EthereumTransactionProcessor>"),
                16 => ("Nethermind.Evm.TransactionProcessing.TransactionProcessorFactory<TGasPolicy>", "Create", "new TransactionProcessor<TGasPolicy>(..., parallel)"),
                17 => ("Nethermind.Consensus.Processing.BalTxProcessorFactory", "Create", "TransactionProcessorFactory<EthereumGasPolicy> -> Create(..., parallel)"),
                18 => ("Nethermind.Core.ValidationResult", "Error;Success;op_Implicit;AsBool",
                    "string conversion stores Error; success iff Error is null"),
                19 => ("Nethermind.Evm.EvmExceptionType", "Stop;None;BadInstruction;StackOverflow;StackUnderflow;OutOfGas;InvalidJumpDestination;AccessViolation;StaticCallViolation;PrecompileFailure;TransactionCollision;NotEnoughBalance;Other;Revert;InvalidCode;Suspend",
                    "int-backed exact ordered values from Stop=-1 and None=0"),
                _ => throw new ExtractionException($"Unexpected source-closure index {i}."),
            };
            bindings[i] = new(specification.Path, specification.Role, owner, member, signature,
                sources[i].Sha256, TokenHash(root));
        }

        return bindings;
    }

    private static void ValidateSourceClosure(SourceFile[] sources, SyntaxTree[] trees)
    {
        SyntaxNode processorRoot = trees[0].GetCompilationUnitRoot();
        SyntaxNode initializationRoot = trees[1].GetCompilationUnitRoot();

        ClassDeclarationSyntax processor = FindClass(processorRoot, "TransactionProcessorBase", 1);
        if (processor.BaseList is null || !processor.BaseList.Types.Any(type =>
                type.ToString().Contains("ITransactionProcessor", StringComparison.Ordinal)))
            throw new ExtractionException("The admitted TransactionProcessorBase<TGasPolicy> no longer implements ITransactionProcessor.");
        RequireMethod(processor, "ValidateStatic", "protected virtual TransactionResult");
        RequireMethod(processor, "ValidateGas", "protected virtual TransactionResult");
        RequireMethod(processor, "CalculateAvailableGas", "protected virtual TransactionResult");

        ClassDeclarationSyntax? ethereumProcessor = processorRoot.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(type => type.Identifier.Text == "EthereumTransactionProcessor");
        if (ethereumProcessor is null || !Compact(ethereumProcessor.BaseList?.ToString() ?? "").Contains("EthereumTransactionProcessorBase", StringComparison.Ordinal))
            throw new ExtractionException("Standard EthereumTransactionProcessor specialization drifted.");

        ClassDeclarationSyntax? ethereumBase = processorRoot.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(type => type.Identifier.Text == "EthereumTransactionProcessorBase");
        if (ethereumBase is null || !Compact(ethereumBase.BaseList?.ToString() ?? "").Contains("TransactionProcessorBase<EthereumGasPolicy>", StringComparison.Ordinal))
            throw new ExtractionException("EthereumTransactionProcessorBase no longer closes TransactionProcessorBase<EthereumGasPolicy>.");
        ClassDeclarationSyntax genericProcessor = FindClass(processorRoot, "TransactionProcessor", 1);
        RequireNoHelperOverrides(genericProcessor, "TransactionProcessor<TGasPolicy>");
        RequireNoHelperOverrides(ethereumProcessor, "EthereumTransactionProcessor");
        RequireNoHelperOverrides(ethereumBase, "EthereumTransactionProcessorBase");

        MethodDeclarationSyntax validateStatic = FindMethod(processorRoot, "TransactionProcessorBase", 1, "ValidateStatic");
        MethodDeclarationSyntax validateGas = FindMethod(processorRoot, "TransactionProcessorBase", 1, "ValidateGas");
        MethodDeclarationSyntax calculateAvailableGas = FindMethod(processorRoot, "TransactionProcessorBase", 1, "CalculateAvailableGas");
        RequireNoUnsupportedControlFlow(validateStatic, "ValidateStatic");
        RequireNoUnsupportedControlFlow(validateGas, "ValidateGas");
        RequireNoUnsupportedControlFlow(calculateAvailableGas, "CalculateAvailableGas");
        ValidateStaticShape(validateStatic);
        ValidateGasShape(validateGas);
        ValidateAvailableGasShape(calculateAvailableGas);

        ClassDeclarationSyntax initialization = FindClass(initializationRoot, "TransactionGasInitializationKernel", 0);
        MethodDeclarationSyntax tryCreate = FindMethod(initializationRoot, "TransactionGasInitializationKernel", 0, "TryCreate");
        RequireNoUnsupportedControlFlow(tryCreate, "TransactionGasInitializationKernel.TryCreate");
        ValidateInitializationShape(tryCreate);
        RequireExactResultFields(initializationRoot);

        SyntaxNode gasPolicyRoot = trees[2].GetCompilationUnitRoot();
        MethodDeclarationSyntax bridge = FindMethod(gasPolicyRoot, "EthereumGasPolicy", 0, "TryCreateAvailableFromIntrinsic");
        RequireNoUnsupportedControlFlow(bridge, "EthereumGasPolicy.TryCreateAvailableFromIntrinsic");
        ValidateAvailableGasBridge(bridge);
        MethodDeclarationSyntax getRemainingGas = FindMethod(gasPolicyRoot, "EthereumGasPolicy", 0, "GetRemainingGas");
        RequireExpression(getRemainingGas.ExpressionBody?.Expression ?? throw new ExtractionException(
            "EthereumGasPolicy.GetRemainingGas lost its expression body."),
            "gas.Value", "EthereumGasPolicy.GetRemainingGas projection");
        MethodDeclarationSyntax getStateReservoir = FindMethod(gasPolicyRoot, "EthereumGasPolicy", 0, "GetStateReservoir");
        RequireExpression(getStateReservoir.ExpressionBody?.Expression ?? throw new ExtractionException(
            "EthereumGasPolicy.GetStateReservoir lost its expression body."),
            "gas.StateReservoir", "EthereumGasPolicy.GetStateReservoir projection");
        ValidateStaticSourceWidths(
            processorRoot,
            trees[4].GetCompilationUnitRoot(),
            trees[9].GetCompilationUnitRoot(),
            trees[10].GetCompilationUnitRoot(),
            trees[11].GetCompilationUnitRoot(),
            gasPolicyRoot,
            trees[3].GetCompilationUnitRoot(),
            initializationRoot);

        ValidateTransactionShapes(trees[4].GetCompilationUnitRoot(), trees[5].GetCompilationUnitRoot(),
            trees[6].GetCompilationUnitRoot(), trees[7].GetCompilationUnitRoot(), trees[8].GetCompilationUnitRoot(),
            trees[9].GetCompilationUnitRoot());
        ValidateIntrinsicShapes(trees[3].GetCompilationUnitRoot());
        ValidateTransactionResultShape(processorRoot);
        ValidateValidationResultShape(trees[18].GetCompilationUnitRoot());
        ValidateEvmExceptionTypeShape(trees[19].GetCompilationUnitRoot());

        string routing = StrictUtf8.GetString(sources[14].Bytes);
        RequireText(routing, "isSystemTransaction || options == ExecutionOptions.SkipValidation", "system routing");
        string registration = StrictUtf8.GetString(sources[15].Bytes);
        RequireText(registration, ".AddScoped<ITransactionProcessor, EthereumTransactionProcessor>()", "standard processor registration");
        RequireText(registration, ".AddSingleton<ITransactionProcessorFactory, TransactionProcessorFactory<EthereumGasPolicy>>()", "standard processor-factory registration");
        RequireText(StrictUtf8.GetString(sources[16].Bytes), "new TransactionProcessor<TGasPolicy>", "generic transaction-processor factory construction");
        string balFactory = StrictUtf8.GetString(sources[17].Bytes);
        RequireText(balFactory, "new TransactionProcessorFactory<EthereumGasPolicy>()", "BAL default standard processor factory");
        RequireText(balFactory, "_transactionProcessorFactory.Create(", "BAL worker processor construction");
        RequireText(StrictUtf8.GetString(sources[13].Bytes), "SkipValidation = 4", "SkipValidation bit");

        ValidateGasLimitCap(trees[12].GetCompilationUnitRoot());
    }

    private static void ValidateStaticShape(MethodDeclarationSyntax method)
    {
        LocalDeclarationStatementSyntax validateDeclaration = method.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .SingleOrDefault(local => local.Declaration.Variables.Any(variable => variable.Identifier.Text == "validate"))
            ?? throw new ExtractionException("ValidateStatic lost its validation-option local.");
        VariableDeclaratorSyntax validate = validateDeclaration.Declaration.Variables
            .Single(variable => variable.Identifier.Text == "validate");
        ExpressionSyntax validateInitializer = validate.Initializer?.Value
            ?? throw new ExtractionException("ValidateStatic.validate lost its initializer.");
        if (validateInitializer is not PrefixUnaryExpressionSyntax prefix ||
            !prefix.IsKind(SyntaxKind.LogicalNotExpression))
            throw new ExtractionException("ValidateStatic.validate is no longer the negated SkipValidation flag.");
        InvocationExpressionSyntax hasFlag = RequireInvocation(prefix.Operand, "HasFlag", 1, "ValidateStatic SkipValidation flag");
        if (Compact(hasFlag.Expression.ToString()) != "opts.HasFlag" ||
            Compact(hasFlag.ArgumentList.Arguments[0].Expression.ToString()) != "ExecutionOptions.SkipValidation")
            throw new ExtractionException("ValidateStatic no longer reads ExecutionOptions.SkipValidation exactly.");

        RequirePattern(method, "tx.SenderAddress", "null", "ValidateStatic sender-null guard");
        RequireBinaryWithOperands(method, "validate", "tx.Nonce", "ulong.MaxValue", "ValidateStatic nonce guard");
        InvocationExpressionSyntax isAboveInitCode = RequireInvocation(method, "IsAboveInitCode", 1,
            "ValidateStatic initcode predicate");
        if (Compact(isAboveInitCode.Expression.ToString()) != "tx.IsAboveInitCode" ||
            Compact(isAboveInitCode.ArgumentList.Arguments[0].Expression.ToString()) != "spec")
            throw new ExtractionException("ValidateStatic initcode receiver or argument changed.");
        RequireMemberOperand(method, "tx.SupportsAuthorizationList", "ValidateStatic SetCode dispatch guard");
        RequireInvocation(method, "ValidateNoContractCreation", 1, "ValidateStatic SetCode creation validator");
        RequireInvocation(method, "ValidateAuthorizationList", 1, "ValidateStatic SetCode authorization validator");
        InvocationExpressionSyntax exceedsCap = RequireInvocation(method, "ExceedsCap", 3,
            "ValidateStatic intrinsic cap predicate");
        if (Compact(exceedsCap.Expression.ToString()) != "intrinsicGas.ExceedsCap")
            throw new ExtractionException("ValidateStatic intrinsic cap receiver changed.");
        string[] capArguments = exceedsCap.ArgumentList.Arguments
            .Select(argument => Compact(argument.ToString())).ToArray();
        if (!capArguments.SequenceEqual(new[] {
                "Eip7825Constants.DefaultTxGasLimitCap", "outulongexecution", "outulongfloor",
            }, StringComparer.Ordinal))
            throw new ExtractionException("ValidateStatic intrinsic cap arguments changed.");
        RequireLocalInitializer(method, "standard", "intrinsicGas.Standard", "ValidateStatic standard policy local");
        RequireLocalInitializer(method, "floorGas", "intrinsicGas.FloorGas", "ValidateStatic floor policy local");
        RequireLocalInitializer(method, "standardGasUsed", "TGasPolicy.GetRemainingGas(in standard)",
            "ValidateStatic standard gas local");
        RequireLocalInitializer(method, "floorGasUsed", "TGasPolicy.GetRemainingGas(in floorGas)",
            "ValidateStatic floor gas local");
        RequireLocalInitializer(method, "minGasRequired", "intrinsicGas.MinRequiredGasLimit",
            "ValidateStatic minimum gas local");
        InvocationExpressionSyntax validateGas = RequireInvocation(method, "ValidateGas", 6,
            "ValidateStatic ValidateGas dispatch");
        if (Compact(validateGas.Expression.ToString()) != "ValidateGas")
            throw new ExtractionException("ValidateStatic ValidateGas target changed.");
        string[] validateGasArguments = validateGas.ArgumentList.Arguments
            .Select(argument => Compact(argument.ToString())).ToArray();
        if (!validateGasArguments.SequenceEqual(new[] {
                "tx", "header", "spec", "instandard", "minGasRequired", "validate",
            }, StringComparer.Ordinal))
            throw new ExtractionException("ValidateStatic ValidateGas argument mapping changed.");
        RequireOrderedReturnErrors(method, [
            "TransactionResult.SenderNotSpecified",
            "TransactionResult.NonceOverflow",
            "TransactionResult.TransactionSizeOverMaxInitCodeSize",
            "TransactionResult.ErrorType.MalformedTransaction.WithDetail",
            "TransactionResult.ErrorType.MalformedTransaction.WithDetail",
            "TransactionResult.ErrorType.GasLimitBelowIntrinsicGas.WithDetail",
            "TransactionResult.ErrorType.GasLimitBelowIntrinsicGas.WithDetail",
            "TransactionResult.ErrorType.GasLimitBelowFloorGas.WithDetail",
            "ValidateGas(tx,header,spec,instandard,minGasRequired,validate)",
        ], "ValidateStatic");
    }

    private static void ValidateGasShape(MethodDeclarationSyntax method)
    {
        RequireBinaryWithOperands(method, "tx.GasLimit", "minGasRequired", null, "ValidateGas minimum gas guard");
        RequireIfWithOperand(method, "validate", "ValidateGas validation gate");
        RequireIfWithOperand(method, "spec.IsEip8037Enabled", "ValidateGas EIP-8037 gate");
        RequireBinaryWithOperands(method, "tx.GasLimit", "header.GasLimit", null, "ValidateGas EIP-8037 limit guard");
        VariableDeclaratorSyntax allowance = RequireLocal(method, "gasUsedForAllowance", "ValidateGas allowance local");
        if (allowance.Initializer?.Value is not ConditionalExpressionSyntax allowanceExpression ||
            Compact(allowanceExpression.Condition.ToString()) != "_parallel" ||
            Compact(allowanceExpression.WhenTrue.ToString()) != "0" ||
            Compact(allowanceExpression.WhenFalse.ToString()) != "header.GasUsed")
            throw new ExtractionException("ValidateGas allowance is no longer the source _parallel ? 0 : header.GasUsed conditional.");
        VariableDeclaratorSyntax maximum = RequireLocal(method, "maxTransactionGasLimit", "ValidateGas maximum gas local");
        if (maximum.Initializer?.Value is not BinaryExpressionSyntax subtraction ||
            !subtraction.IsKind(SyntaxKind.SubtractExpression) ||
            Compact(subtraction.Left.ToString()) != "header.GasLimit" ||
            Compact(subtraction.Right.ToString()) != "gasUsedForAllowance")
            throw new ExtractionException("ValidateGas no longer uses unchecked-width header.GasLimit - gasUsedForAllowance.");
        RequireBinaryWithOperands(method, "tx.GasLimit", "maxTransactionGasLimit", null, "ValidateGas legacy limit guard");
        RequireOrderedReturnErrors(method, [
            "TransactionResult.ErrorType.GasLimitBelowIntrinsicGas.WithDetail",
            "TransactionResult.BlockGasLimitExceeded",
            "TransactionResult.Ok",
            "TransactionResult.BlockGasLimitExceeded",
            "TransactionResult.Ok",
        ], "ValidateGas");
    }

    private static void ValidateAvailableGasShape(MethodDeclarationSyntax method)
    {
        InvocationExpressionSyntax invocation = RequireInvocation(method, "TryCreateAvailableFromIntrinsic", 4,
            "CalculateAvailableGas bridge call");
        if (Compact(invocation.Expression.ToString()) != "TGasPolicy.TryCreateAvailableFromIntrinsic")
            throw new ExtractionException("CalculateAvailableGas bridge target changed.");
        string[] arguments = invocation.ArgumentList.Arguments
            .Select(argument => Compact(argument.ToString())).ToArray();
        if (!arguments.SequenceEqual(new[] {
                "tx.GasLimit", "intrinsicGas.Standard", "spec", "outgasAvailable",
            }, StringComparer.Ordinal))
            throw new ExtractionException("CalculateAvailableGas bridge argument mapping changed.");
        if (method.ExpressionBody?.Expression is not ConditionalExpressionSyntax conditional)
            throw new ExtractionException("CalculateAvailableGas changed from its pure expression-bodied conditional.");
        if (conditional.Condition.Span != invocation.Span)
            throw new ExtractionException("CalculateAvailableGas no longer branches directly on the bridge result.");
        RequireExpression(conditional.WhenTrue, "TransactionResult.Ok", "CalculateAvailableGas success result");
        RequireExpression(conditional.WhenFalse, "TransactionResult.GasLimitBelowIntrinsicGas",
            "CalculateAvailableGas failure result");
    }

    private static void ValidateInitializationShape(MethodDeclarationSyntax method)
    {
        // This helper is composed from the separately pinned canonical Lean
        // initializer.  Its source operations are nevertheless checked in full
        // so a changed production kernel cannot silently retain the old model.
        RequireAnchors(method, "TransactionGasInitializationKernel.TryCreate", [
            "unchecked(intrinsicExecutionGas+unchecked((ulong)intrinsicStateGas))",
            "TransactionGasInitializationOutcome.IntrinsicGasExceedsLimit",
            "ulongavailableGas=gasLimit-intrinsicTotal", "if(eip8037Enabled)",
            "Math.Min(availableGas,executionGasAfterIntrinsicCap)", "availableGas-gasLeft",
            "unchecked((long)stateReservoir)", "intrinsicStateGas",
        ]);
        RequireLocalInitializer(method, "intrinsicTotal",
            "unchecked(intrinsicExecutionGas + unchecked((ulong)intrinsicStateGas))", "initializer total local");
        IfStatementSyntax[] conditionals = method.DescendantNodes().OfType<IfStatementSyntax>().ToArray();
        IfStatementSyntax affordability = conditionals.Single(conditional =>
            ContainsOperand(conditional.Condition, "intrinsicTotal"));
        RequireOrderedComparison(affordability.Condition, "gasLimit", "intrinsicTotal",
            "initializer intrinsic affordability guard");
        RequireLocalInitializer(method, "availableGas", "gasLimit - intrinsicTotal", "initializer available gas local");
        RequireLocalInitializer(method, "gasLeft", "availableGas", "initializer gas-left local");
        IfStatementSyntax fork = conditionals.Single(conditional =>
            Compact(conditional.Condition.ToString()) == "eip8037Enabled");
        VariableDeclaratorSyntax executionCap = RequireLocal(method, "executionGasAfterIntrinsicCap",
            "initializer execution-cap local");
        if (executionCap.Initializer?.Value is not ConditionalExpressionSyntax capConditional)
            throw new ExtractionException("Initializer execution-cap local lost its conditional expression.");
        RequireOrderedComparison(capConditional.Condition, "intrinsicExecutionGas", "executionGasLimitCap",
            "initializer execution-cap guard");
        RequireExpression(capConditional.WhenTrue, "0", "initializer exhausted execution-cap result");
        RequireExpression(capConditional.WhenFalse, "executionGasLimitCap - intrinsicExecutionGas",
            "initializer remaining execution-cap result");
        AssignmentExpressionSyntax gasLeftAssignment = fork.Statement.DescendantNodesAndSelf()
            .OfType<AssignmentExpressionSyntax>()
            .Single(assignment => Compact(assignment.Left.ToString()) == "gasLeft");
        RequireExpression(gasLeftAssignment.Right, "Math.Min(availableGas, executionGasAfterIntrinsicCap)",
            "initializer execution gas selection");
        RequireLocalInitializer(method, "stateReservoir", "availableGas - gasLeft",
            "initializer state-reservoir local");
    }

    private static void ValidateAvailableGasBridge(MethodDeclarationSyntax method)
    {
        InvocationExpressionSyntax invocation = RequireInvocation(method, "TransactionGasInitializationKernel.TryCreate", 5,
            "EthereumGasPolicy initializer bridge");
        if (Compact(invocation.Expression.ToString()) != "TransactionGasInitializationKernel.TryCreate")
            throw new ExtractionException("EthereumGasPolicy initializer target changed.");
        string[] expectedArguments = [
            "gasLimit", "intrinsicGas.Value", "intrinsicGas.StateReservoir", "spec.IsEip8037Enabled",
            "Eip7825Constants.DefaultTxGasLimitCap",
        ];
        string[] actualArguments = invocation.ArgumentList.Arguments
            .Select(argument => Compact(argument.Expression.ToString())).ToArray();
        if (!actualArguments.SequenceEqual(expectedArguments, StringComparer.Ordinal))
            throw new ExtractionException("EthereumGasPolicy initializer argument mapping changed.");

        IfStatementSyntax failure = method.DescendantNodes().OfType<IfStatementSyntax>().Single();
        if (Compact(failure.Condition.ToString()) !=
            "result.OutcomeisTransactionGasInitializationOutcome.IntrinsicGasExceedsLimit")
            throw new ExtractionException("EthereumGasPolicy bridge failure-outcome predicate changed.");
        if (failure.Statement is not BlockSyntax failureBlock)
            throw new ExtractionException("EthereumGasPolicy bridge failure branch is no longer a block.");
        StatementSyntax[] failureStatements = failureBlock.Statements.ToArray();
        if (failureStatements.Length != 2 ||
            failureStatements[0] is not ExpressionStatementSyntax failureAssignmentStatement ||
            failureAssignmentStatement.Expression is not AssignmentExpressionSyntax failureAssignment ||
            Compact(failureAssignment.Left.ToString()) != "available" ||
            Compact(failureAssignment.Right.ToString()) != "default" ||
            failureStatements[1] is not ReturnStatementSyntax failureReturn ||
            Compact(failureReturn.Expression?.ToString() ?? string.Empty) != "false")
            throw new ExtractionException("EthereumGasPolicy bridge failure/default mapping changed.");

        InitializerExpressionSyntax initializer = method.DescendantNodes()
            .OfType<InitializerExpressionSyntax>()
            .SingleOrDefault(candidate => candidate.IsKind(SyntaxKind.ObjectInitializerExpression) &&
                candidate.Expressions.OfType<AssignmentExpressionSyntax>().Any(assignment => assignment.Left.ToString() == "Value"))
            ?? throw new ExtractionException("EthereumGasPolicy bridge lost its available-policy initializer.");
        string[] expectedFields = ["Value", "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded"];
        string[] actualFields = initializer.Expressions.OfType<AssignmentExpressionSyntax>()
            .Select(assignment => assignment.Left.ToString()).ToArray();
        if (!actualFields.SequenceEqual(expectedFields, StringComparer.Ordinal))
            throw new ExtractionException("EthereumGasPolicy available-policy field order or membership changed.");
        string[] expectedExpressions = [
            "result.Value", "result.StateReservoir", "result.StateGasUsed", "result.StateGasSpill",
            "result.StateGasSpillRefunded",
        ];
        string[] actualExpressions = initializer.Expressions.OfType<AssignmentExpressionSyntax>()
            .Select(assignment => Compact(assignment.Right.ToString())).ToArray();
        if (!actualExpressions.SequenceEqual(expectedExpressions, StringComparer.Ordinal))
            throw new ExtractionException("EthereumGasPolicy available-policy source mapping changed.");

        ReturnStatementSyntax successReturn = method.Body?.Statements.LastOrDefault() as ReturnStatementSyntax
            ?? throw new ExtractionException("EthereumGasPolicy bridge lost its terminal success return.");
        if (Compact(successReturn.Expression?.ToString() ?? string.Empty) != "true")
            throw new ExtractionException("EthereumGasPolicy bridge terminal success polarity changed.");
    }

    private static void ValidateTransactionShapes(
        SyntaxNode transactionRoot,
        SyntaxNode transactionTypeRoot,
        SyntaxNode transactionTypeExtensionsRoot,
        SyntaxNode transactionExtensionsRoot,
        SyntaxNode setCodeValidationRoot,
        SyntaxNode releaseSpecExtensionsRoot)
    {
        ClassDeclarationSyntax transaction = FindClass(transactionRoot, "Transaction", 0);
        if (transaction.Members.OfType<MethodDeclarationSyntax>()
            .Any(method => method.Identifier.Text == "IsAboveInitCode"))
            throw new ExtractionException("Transaction gained an instance IsAboveInitCode member that shadows the admitted extension.");
        PropertyDeclarationSyntax creation = FindProperty(transactionRoot, "IsContractCreation");
        if (Compact(creation.ExpressionBody?.Expression.ToString() ?? string.Empty) != "Toisnull")
            throw new ExtractionException("Transaction.IsContractCreation source predicate changed.");
        PropertyDeclarationSyntax dataLength = FindProperty(transactionRoot, "DataLength");
        if (Compact(dataLength.ExpressionBody?.Expression.ToString() ?? string.Empty) != "Data.Length")
            throw new ExtractionException("Transaction.DataLength source projection changed.");
        PropertyDeclarationSyntax hasAuthorization = FindProperty(transactionRoot, "SupportsAuthorizationList");
        if (Compact(hasAuthorization.ExpressionBody?.Expression.ToString() ?? string.Empty) !=
            "Type.SupportsAuthorizationList()")
            throw new ExtractionException("Transaction authorization-list type projection changed.");

        EnumMemberDeclarationSyntax setCode = transactionTypeRoot.DescendantNodes().OfType<EnumMemberDeclarationSyntax>()
            .SingleOrDefault(member => member.Identifier.Text == "SetCode")
            ?? throw new ExtractionException("TxType.SetCode source value was removed.");
        EnumDeclarationSyntax transactionType = transactionTypeRoot.DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .Single(type => type.Identifier.Text == "TxType");
        if (transactionType.BaseList?.Types.Count != 1 ||
            Compact(transactionType.BaseList.Types[0].Type.ToString()) != "byte")
            throw new ExtractionException("TxType is no longer byte-backed.");
        if (setCode.EqualsValue?.Value is not LiteralExpressionSyntax literal ||
            !literal.IsKind(SyntaxKind.NumericLiteralExpression) ||
            literal.Token.Value is not int setCodeValue || setCodeValue != 4)
            throw new ExtractionException("TxType.SetCode is no longer the explicit byte value 4.");

        MethodDeclarationSyntax supports = FindAnyMethod(transactionTypeExtensionsRoot, "SupportsAuthorizationList");
        ExpressionSyntax supportsExpression = supports.ExpressionBody?.Expression
            ?? throw new ExtractionException("TxType.SupportsAuthorizationList lost its expression body.");
        RequireComparison(supportsExpression, SyntaxKind.EqualsExpression,
            "txType", "TxType.SetCode", "TxType.SupportsAuthorizationList comparison");

        MethodDeclarationSyntax above = FindAnyMethod(transactionExtensionsRoot, "IsAboveInitCode");
        if (above.ExpressionBody?.Expression is not BinaryExpressionSyntax aboveOuter ||
            !aboveOuter.IsKind(SyntaxKind.LogicalAndExpression) ||
            aboveOuter.Left is not BinaryExpressionSyntax aboveInner ||
            !aboveInner.IsKind(SyntaxKind.LogicalAndExpression))
            throw new ExtractionException("TransactionExtensions.IsAboveInitCode lost its three-way conjunction shape.");
        RequireExpression(aboveInner.Left, "tx.IsContractCreation", "IsAboveInitCode creation gate");
        RequireExpression(aboveInner.Right, "spec.IsEip3860Enabled", "IsAboveInitCode EIP-3860 gate");
        RequireComparison(aboveOuter.Right, SyntaxKind.GreaterThanExpression,
            "tx.DataLength", "spec.MaxInitCodeSize", "IsAboveInitCode size comparison");

        MethodDeclarationSyntax noCreation = FindAnyMethod(setCodeValidationRoot, "ValidateNoContractCreation");
        if (noCreation.ExpressionBody?.Expression is not ConditionalExpressionSyntax noCreationConditional)
            throw new ExtractionException("SetCode no-creation validation lost its conditional expression.");
        RequireExpression(noCreationConditional.Condition, "transaction.IsContractCreation",
            "SetCode no-creation condition");
        RequireExpression(noCreationConditional.WhenTrue, "TxErrorMessages.NotAllowedCreateTransaction",
            "SetCode no-creation failure mapping");
        RequireExpression(noCreationConditional.WhenFalse, "ValidationResult.Success",
            "SetCode no-creation success mapping");

        MethodDeclarationSyntax authorization = FindAnyMethod(setCodeValidationRoot, "ValidateAuthorizationList");
        if (authorization.ExpressionBody?.Expression is not SwitchExpressionSyntax authorizationSwitch)
            throw new ExtractionException("SetCode authorization validation lost its switch expression.");
        if (Compact(authorizationSwitch.GoverningExpression.ToString()) != "transaction.AuthorizationList" ||
            authorizationSwitch.Arms.Count != 2)
            throw new ExtractionException("SetCode authorization-list governing expression or arm count changed.");
        SwitchExpressionArmSyntax emptyAuthorization = authorizationSwitch.Arms[0];
        if (Compact(emptyAuthorization.Pattern.ToString()) != "nullor{Length:0}")
            throw new ExtractionException("SetCode authorization null/empty predicate changed.");
        RequireExpression(emptyAuthorization.Expression, "TxErrorMessages.MissingAuthorizationList",
            "SetCode authorization failure mapping");
        SwitchExpressionArmSyntax nonEmptyAuthorization = authorizationSwitch.Arms[1];
        if (nonEmptyAuthorization.Pattern is not DiscardPatternSyntax)
            throw new ExtractionException("SetCode authorization success arm changed.");
        RequireExpression(nonEmptyAuthorization.Expression, "ValidationResult.Success",
            "SetCode authorization success mapping");

        PropertyDeclarationSyntax maxInitCodeSize = FindProperty(releaseSpecExtensionsRoot, "MaxInitCodeSize");
        if (Compact(maxInitCodeSize.ExpressionBody?.Expression.ToString() ?? string.Empty) != "2*spec.MaxCodeSize")
            throw new ExtractionException("MaxInitCodeSize source arithmetic changed.");
    }

    private static void ValidateStaticSourceWidths(
        SyntaxNode processorRoot,
        SyntaxNode transactionRoot,
        SyntaxNode releaseSpecExtensionsRoot,
        SyntaxNode releaseSpecRoot,
        SyntaxNode blockHeaderRoot,
        SyntaxNode gasPolicyRoot,
        SyntaxNode gasPolicyInterfaceRoot,
        SyntaxNode initializationRoot)
    {
        TypeDeclarationSyntax processor = FindType(processorRoot, "TransactionProcessorBase", 1);
        RequireFieldType(processor, "_parallel", "bool");

        TypeDeclarationSyntax transaction = FindType(transactionRoot, "Transaction", 0);
        RequirePropertyType(transaction, "Nonce", "ulong");
        RequirePropertyType(transaction, "GasLimit", "ulong");
        RequirePropertyType(transaction, "DataLength", "int");
        RequirePropertyType(transaction, "Type", "TxType");
        RequirePropertyType(transaction, "AuthorizationList", "AuthorizationTuple[]?");
        RequirePropertyType(transaction, "To", "Address?");
        RequirePropertyType(transaction, "SenderAddress", "Address?");

        TypeDeclarationSyntax releaseSpec = FindType(releaseSpecRoot, "IReleaseSpec", 0);
        RequirePropertyType(releaseSpec, "MaxCodeSize", "long");
        RequirePropertyType(releaseSpec, "IsEip3860Enabled", "bool");
        RequirePropertyType(releaseSpec, "IsEip8037Enabled", "bool");
        PropertyDeclarationSyntax maxInitCodeSize = FindProperty(releaseSpecExtensionsRoot, "MaxInitCodeSize");
        if (Compact(maxInitCodeSize.Type.ToString()) != "long")
            throw new ExtractionException("IReleaseSpecExtensions.MaxInitCodeSize fixed-width type changed.");

        TypeDeclarationSyntax blockHeader = FindType(blockHeaderRoot, "BlockHeader", 0);
        RequirePropertyType(blockHeader, "GasLimit", "ulong");
        RequirePropertyType(blockHeader, "GasUsed", "ulong");

        TypeDeclarationSyntax gasPolicy = FindType(gasPolicyRoot, "EthereumGasPolicy", 0);
        RequireFieldType(gasPolicy, "Value", "ulong");
        foreach (string field in new[] { "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded" })
            RequireFieldType(gasPolicy, field, "long");
        RequireMethodShape(gasPolicy, "GetRemainingGas", "ulong", ["inEthereumGasPolicygas"]);
        RequireMethodShape(gasPolicy, "GetStateReservoir", "long", ["inEthereumGasPolicygas"]);
        RequireMethodShape(gasPolicy, "TryCreateAvailableFromIntrinsic", "bool",
            ["ulonggasLimit", "inEthereumGasPolicyintrinsicGas", "IReleaseSpecspec", "outEthereumGasPolicyavailable"]);

        TypeDeclarationSyntax intrinsicGas = FindType(gasPolicyInterfaceRoot, "IntrinsicGas", 1);
        RequirePropertyType(intrinsicGas, "StandardGas", "ulong");
        RequirePropertyType(intrinsicGas, "MinRequiredGasLimit", "ulong");
        RequireMethodShape(intrinsicGas, "ExceedsCap", "bool",
            ["ulongcap", "outulongexecution", "outulongfloor"]);

        TypeDeclarationSyntax initializationResult = FindType(initializationRoot,
            "TransactionGasInitializationResult", 0);
        string[] resultParameters = initializationResult.ParameterList?.Parameters
            .Select(parameter => Compact(parameter.ToString())).ToArray() ?? [];
        if (!resultParameters.SequenceEqual(new[] {
                "TransactionGasInitializationOutcomeoutcome", "ulongvalue", "longstateReservoir",
                "longstateGasUsed", "longstateGasSpill", "longstateGasSpillRefunded",
            }, StringComparer.Ordinal))
            throw new ExtractionException("TransactionGasInitializationResult fixed-width parameters changed.");
        RequireFieldType(initializationResult, "Outcome", "TransactionGasInitializationOutcome");
        RequireFieldType(initializationResult, "Value", "ulong");
        foreach (string field in new[] { "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded" })
            RequireFieldType(initializationResult, field, "long");
        TypeDeclarationSyntax initializationKernel = FindType(initializationRoot,
            "TransactionGasInitializationKernel", 0);
        RequireMethodShape(initializationKernel, "TryCreate", "TransactionGasInitializationResult",
            ["ulonggasLimit", "ulongintrinsicExecutionGas", "longintrinsicStateGas",
                "booleip8037Enabled", "ulongexecutionGasLimitCap"]);
        EnumDeclarationSyntax initializationOutcome = initializationRoot.DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .Single(type => type.Identifier.Text == "TransactionGasInitializationOutcome");
        RequireEnumLayout(initializationOutcome, "byte",
            [("Success", null), ("IntrinsicGasExceedsLimit", null)]);
    }

    private static void ValidateIntrinsicShapes(SyntaxNode gasPolicyInterfaceRoot)
    {
        PropertyDeclarationSyntax standardGas = FindProperty(gasPolicyInterfaceRoot, "StandardGas");
        if (standardGas.ExpressionBody?.Expression is not BinaryExpressionSyntax standardAddition ||
            !standardAddition.IsKind(SyntaxKind.AddExpression))
            throw new ExtractionException("IntrinsicGas.StandardGas lost its unchecked addition shape.");
        RequireExpression(standardAddition.Left, "TGasPolicy.GetRemainingGas(Standard)",
            "IntrinsicGas.StandardGas execution operand");
        RequireExpression(standardAddition.Right, "(ulong)TGasPolicy.GetStateReservoir(Standard)",
            "IntrinsicGas.StandardGas signed-reservoir cast");

        PropertyDeclarationSyntax minimum = FindProperty(gasPolicyInterfaceRoot, "MinRequiredGasLimit");
        if (minimum.ExpressionBody?.Expression is not InvocationExpressionSyntax maxInvocation ||
            Compact(maxInvocation.Expression.ToString()) != "Math.Max" ||
            maxInvocation.ArgumentList.Arguments.Count != 2)
            throw new ExtractionException("IntrinsicGas.MinRequiredGasLimit lost its Math.Max shape.");
        RequireExpression(maxInvocation.ArgumentList.Arguments[0].Expression, "StandardGas",
            "IntrinsicGas.MinRequiredGasLimit standard operand");
        RequireExpression(maxInvocation.ArgumentList.Arguments[1].Expression,
            "TGasPolicy.GetRemainingGas(FloorGas)", "IntrinsicGas.MinRequiredGasLimit floor operand");

        MethodDeclarationSyntax cap = FindMethod(gasPolicyInterfaceRoot, "IntrinsicGas", 1, "ExceedsCap");
        RequireLocalInitializer(cap, "standard", "Standard", "IntrinsicGas.ExceedsCap standard local");
        RequireLocalInitializer(cap, "floorGas", "FloorGas", "IntrinsicGas.ExceedsCap floor local");
        AssignmentExpressionSyntax executionAssignment = cap.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(assignment => Compact(assignment.Left.ToString()) == "execution");
        RequireExpression(executionAssignment.Right, "TGasPolicy.GetRemainingGas(in standard)",
            "IntrinsicGas.ExceedsCap execution projection");
        AssignmentExpressionSyntax floorAssignment = cap.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(assignment => Compact(assignment.Left.ToString()) == "floor");
        RequireExpression(floorAssignment.Right, "TGasPolicy.GetRemainingGas(in floorGas)",
            "IntrinsicGas.ExceedsCap floor projection");
        ReturnStatementSyntax capReturn = cap.DescendantNodes().OfType<ReturnStatementSyntax>().SingleOrDefault()
            ?? throw new ExtractionException("IntrinsicGas.ExceedsCap lost its return expression.");
        if (capReturn.Expression is not BinaryExpressionSyntax capOr ||
            !capOr.IsKind(SyntaxKind.LogicalOrExpression))
            throw new ExtractionException("IntrinsicGas.ExceedsCap must be the source execution-or-floor cap predicate.");
        RequireComparison(capOr.Left, SyntaxKind.GreaterThanExpression, "execution", "cap",
            "IntrinsicGas.ExceedsCap execution comparison");
        RequireComparison(capOr.Right, SyntaxKind.GreaterThanExpression, "floor", "cap",
            "IntrinsicGas.ExceedsCap floor comparison");
    }

    private static void ValidateGasLimitCap(SyntaxNode gasLimitCapRoot)
    {
        ClassDeclarationSyntax constants = FindClass(gasLimitCapRoot, "Eip7825Constants", 0);
        FieldDeclarationSyntax field = constants.Members.OfType<FieldDeclarationSyntax>()
            .Single(declaration => declaration.Declaration.Variables.Any(variable =>
                variable.Identifier.Text == "DefaultTxGasLimitCap"));
        if (field.Declaration.Variables.Count != 1 ||
            Compact(field.Declaration.Type.ToString()) != "ulong" ||
            !field.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !field.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            !field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            throw new ExtractionException("EIP-7825 transaction gas cap declaration shape changed.");
        VariableDeclaratorSyntax variable = field.Declaration.Variables[0];
        if (variable.Initializer?.Value is not LiteralExpressionSyntax literal ||
            !literal.IsKind(SyntaxKind.NumericLiteralExpression) ||
            Convert.ToUInt64(literal.Token.Value) != 16_777_216UL)
            throw new ExtractionException("EIP-7825 transaction gas cap value changed.");
    }

    private static void ValidateTransactionResultShape(SyntaxNode processorRoot)
    {
        StructDeclarationSyntax result = processorRoot.DescendantNodes()
            .OfType<StructDeclarationSyntax>()
            .Single(type => type.Identifier.Text == "TransactionResult");
        EnumDeclarationSyntax errorType = result.Members.OfType<EnumDeclarationSyntax>()
            .Single(type => type.Identifier.Text == "ErrorType");
        RequireEnumLayout(errorType, "int",
        [
            ("None", null),
            ("BlockGasLimitExceeded", null),
            ("GasLimitBelowIntrinsicGas", null),
            ("GasLimitBelowFloorGas", null),
            ("InsufficientMaxFeePerGasForSenderBalance", null),
            ("InsufficientSenderBalance", null),
            ("MalformedTransaction", null),
            ("MaxFeePerGasBelowBaseFee", null),
            ("MinerPremiumNegative", null),
            ("NonceOverflow", null),
            ("SenderHasDeployedCode", null),
            ("SenderNotSpecified", null),
            ("TransactionSizeOverMaxInitCodeSize", null),
            ("TransactionNonceTooHigh", null),
            ("TransactionNonceTooLow", null),
        ]);
        ConstructorDeclarationSyntax constructor = result.Members.OfType<ConstructorDeclarationSyntax>().Single();
        string[] constructorParameters = constructor.ParameterList.Parameters
            .Select(parameter => Compact(parameter.ToString())).ToArray();
        if (!constructorParameters.SequenceEqual(new[] {
                "ErrorTypeerror=ErrorType.None",
                "EvmExceptionTypeevmException=EvmExceptionType.None",
                "stringerrorDescription=\"\"",
            }, StringComparer.Ordinal))
            throw new ExtractionException("TransactionResult constructor parameter shape or defaults changed.");
        string[] constructorStatements = constructor.Body?.Statements
            .Select(statement => Compact(statement.ToString())).ToArray() ?? [];
        if (!constructorStatements.SequenceEqual(new[] {
                "Error=error;", "EvmExceptionType=evmException;", "ErrorDescription=errorDescription;",
            }, StringComparer.Ordinal))
            throw new ExtractionException("TransactionResult constructor field assignments changed.");

        Dictionary<string, string?> expectedFields = new(StringComparer.Ordinal)
        {
            ["Ok"] = null,
            ["BlockGasLimitExceeded"] = "ErrorType.BlockGasLimitExceeded",
            ["GasLimitBelowIntrinsicGas"] = "ErrorType.GasLimitBelowIntrinsicGas",
            ["GasLimitBelowFloorGas"] = "ErrorType.GasLimitBelowFloorGas",
            ["MalformedTransaction"] = "ErrorType.MalformedTransaction",
            ["NonceOverflow"] = "ErrorType.NonceOverflow",
            ["SenderNotSpecified"] = "ErrorType.SenderNotSpecified",
            ["TransactionSizeOverMaxInitCodeSize"] = "ErrorType.TransactionSizeOverMaxInitCodeSize",
        };
        foreach ((string fieldName, string? expectedError) in expectedFields)
        {
            VariableDeclaratorSyntax field = result.Members.OfType<FieldDeclarationSyntax>()
                .SelectMany(declaration => declaration.Declaration.Variables)
                .Single(variable => variable.Identifier.Text == fieldName);
            if (field.Initializer?.Value is not ImplicitObjectCreationExpressionSyntax creation)
                throw new ExtractionException($"TransactionResult.{fieldName} lost its direct constructor.");
            string[] arguments = creation.ArgumentList?.Arguments
                .Select(argument => Compact(argument.ToString())).ToArray() ?? [];
            if (expectedError is null)
            {
                if (arguments.Length != 0)
                    throw new ExtractionException("TransactionResult.Ok no longer uses the default result.");
            }
            else if (arguments.Length != 2 || arguments[0] != expectedError ||
                     creation.ArgumentList!.Arguments[0].NameColon is not null ||
                     Compact(creation.ArgumentList.Arguments[1].NameColon?.Name.ToString() ?? string.Empty) !=
                     "errorDescription")
            {
                throw new ExtractionException($"TransactionResult.{fieldName} error/EVM mapping changed.");
            }
        }

        MethodDeclarationSyntax withDetail = result.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.Text == "WithDetail");
        if (Compact(withDetail.ExpressionBody?.Expression.ToString() ?? string.Empty) !=
            "new(errorType,errorDescription:detail)")
            throw new ExtractionException("TransactionResult.WithDetail mapping changed.");
        ClassDeclarationSyntax extensions = FindClass(processorRoot, "TransactionResultExtensions", 0);
        MethodDeclarationSyntax extension = extensions.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.Text == "WithDetail");
        if (Compact(extension.ExpressionBody?.Expression.ToString() ?? string.Empty) !=
            "TransactionResult.WithDetail(errorType,detail)")
            throw new ExtractionException("TransactionResult ErrorType.WithDetail forwarding changed.");
    }

    private static void ValidateValidationResultShape(SyntaxNode validationRoot)
    {
        RecordDeclarationSyntax result = validationRoot.DescendantNodes()
            .OfType<RecordDeclarationSyntax>()
            .Single(type => type.Identifier.Text == "ValidationResult");
        ParameterSyntax[] primaryParameters = result.ParameterList?.Parameters.ToArray() ?? [];
        if (primaryParameters.Length != 1 ||
            Compact(primaryParameters[0].Type?.ToString() ?? string.Empty) != "string?" ||
            primaryParameters[0].Identifier.Text != "Error")
            throw new ExtractionException("ValidationResult positional Error field changed.");
        PropertyDeclarationSyntax success = result.Members.OfType<PropertyDeclarationSyntax>()
            .Single(property => property.Identifier.Text == "Success");
        if (Compact(success.ExpressionBody?.Expression.ToString() ?? string.Empty) != "new(null)")
            throw new ExtractionException("ValidationResult.Success mapping changed.");
        ConversionOperatorDeclarationSyntax boolConversion = result.Members
            .OfType<ConversionOperatorDeclarationSyntax>()
            .Single(declaration => declaration.Type.ToString() == "bool");
        if (boolConversion.ParameterList.Parameters.Count != 1 ||
            Compact(boolConversion.ParameterList.Parameters[0].ToString()) != "ValidationResultresult" ||
            Compact(boolConversion.ExpressionBody?.Expression.ToString() ?? string.Empty) != "result.AsBool()")
            throw new ExtractionException("ValidationResult Boolean conversion changed.");
        ConversionOperatorDeclarationSyntax stringConversion = result.Members
            .OfType<ConversionOperatorDeclarationSyntax>()
            .Single(declaration => declaration.Type.ToString() == "ValidationResult");
        if (stringConversion.ParameterList.Parameters.Count != 1 ||
            Compact(stringConversion.ParameterList.Parameters[0].ToString()) != "stringerror" ||
            Compact(stringConversion.ExpressionBody?.Expression.ToString() ?? string.Empty) != "new(error)")
            throw new ExtractionException("ValidationResult string conversion changed.");
        MethodDeclarationSyntax asBool = result.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.Text == "AsBool");
        if (Compact(asBool.ExpressionBody?.Expression.ToString() ?? string.Empty) != "Errorisnull")
            throw new ExtractionException("ValidationResult.AsBool mapping changed.");
    }

    private static void ValidateEvmExceptionTypeShape(SyntaxNode evmExceptionRoot)
    {
        EnumDeclarationSyntax exceptionType = evmExceptionRoot.DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .Single(type => type.Identifier.Text == "EvmExceptionType");
        RequireEnumLayout(exceptionType, "int",
        [
            ("Stop", "-1"),
            ("None", "0"),
            ("BadInstruction", null),
            ("StackOverflow", null),
            ("StackUnderflow", null),
            ("OutOfGas", null),
            ("InvalidJumpDestination", null),
            ("AccessViolation", null),
            ("StaticCallViolation", null),
            ("PrecompileFailure", null),
            ("TransactionCollision", null),
            ("NotEnoughBalance", null),
            ("Other", null),
            ("Revert", null),
            ("InvalidCode", null),
            ("Suspend", null),
        ]);
    }

    private static void RequireEnumLayout(
        EnumDeclarationSyntax declaration,
        string expectedUnderlyingType,
        IReadOnlyList<(string Name, string? ExplicitValue)> expectedMembers)
    {
        string actualUnderlyingType = declaration.BaseList is null
            ? "int"
            : declaration.BaseList.Types.Count == 1
                ? Compact(declaration.BaseList.Types[0].Type.ToString())
                : string.Empty;
        if (actualUnderlyingType != expectedUnderlyingType ||
            declaration.Members.Count != expectedMembers.Count)
            throw new ExtractionException($"{declaration.Identifier.Text} enum width or member count changed.");
        for (int index = 0; index < expectedMembers.Count; index++)
        {
            EnumMemberDeclarationSyntax member = declaration.Members[index];
            string? actualValue = member.EqualsValue is null
                ? null
                : Compact(member.EqualsValue.Value.ToString());
            if (member.Identifier.Text != expectedMembers[index].Name ||
                actualValue != expectedMembers[index].ExplicitValue)
                throw new ExtractionException($"{declaration.Identifier.Text}.{expectedMembers[index].Name} enum value/order changed.");
        }
    }

    private static void RequireOrderedReturnErrors(
        MethodDeclarationSyntax method,
        IReadOnlyList<string> expected,
        string name)
    {
        ReturnStatementSyntax[] returns = method.DescendantNodes().OfType<ReturnStatementSyntax>()
            .OrderBy(@return => @return.SpanStart).ToArray();
        if (returns.Length != expected.Count)
            throw new ExtractionException($"{name} return count changed: expected {expected.Count}, found {returns.Length}.");
        for (int index = 0; index < expected.Count; index++)
            if (!ReturnExpressionMatches(returns[index].Expression, expected[index]))
                throw new ExtractionException($"{name} return/error mapping changed at position {index + 1}.");
    }

    private static void RequireNoHelperOverrides(TypeDeclarationSyntax type, string name)
    {
        string[] helperNames = ["ValidateStatic", "ValidateGas", "CalculateAvailableGas"];
        string[] overrides = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => helperNames.Contains(method.Identifier.Text, StringComparer.Ordinal))
            .Select(method => method.Identifier.Text)
            .ToArray();
        if (overrides.Length != 0)
            throw new ExtractionException($"{name} overrides admitted base helper(s): {string.Join(", ", overrides)}.");
    }

    private static VariableDeclaratorSyntax RequireLocal(MethodDeclarationSyntax method, string name, string description)
    {
        VariableDeclaratorSyntax? variable = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .SingleOrDefault(candidate => candidate.Identifier.Text == name);
        return variable ?? throw new ExtractionException($"{description} is missing.");
    }

    private static VariableDeclaratorSyntax RequireLocalInitializer(
        MethodDeclarationSyntax method,
        string name,
        string expected,
        string description)
    {
        VariableDeclaratorSyntax variable = RequireLocal(method, name, description);
        RequireExpression(variable.Initializer?.Value ?? throw new ExtractionException(
            $"{description} lost its initializer."), expected, description);
        return variable;
    }

    private static InvocationExpressionSyntax RequireInvocation(
        SyntaxNode node,
        string suffix,
        int argumentCount,
        string description)
    {
        InvocationExpressionSyntax[] matches = node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(candidate => candidate.Expression.ToString().EndsWith("." + suffix, StringComparison.Ordinal) ||
                string.Equals(candidate.Expression.ToString(), suffix, StringComparison.Ordinal))
            .ToArray();
        InvocationExpressionSyntax? invocation = matches.SingleOrDefault();
        if (invocation is null || invocation.ArgumentList.Arguments.Count != argumentCount)
            throw new ExtractionException($"{description} source shape changed.");
        return invocation;
    }

    private static void RequireMemberOperand(SyntaxNode node, string member, string description)
    {
        if (!Compact(node.ToString()).Contains(Compact(member), StringComparison.Ordinal))
            throw new ExtractionException($"{description} source operand changed.");
    }

    private static void RequireExpression(ExpressionSyntax expression, string expected, string description)
    {
        if (Compact(expression.ToString()) != Compact(expected))
            throw new ExtractionException($"{description} source expression changed.");
    }

    private static void RequireComparison(
        ExpressionSyntax expression,
        SyntaxKind comparisonKind,
        string left,
        string right,
        string description)
    {
        if (expression is not BinaryExpressionSyntax comparison ||
            !comparison.IsKind(comparisonKind) ||
            Compact(comparison.Left.ToString()) != Compact(left) ||
            Compact(comparison.Right.ToString()) != Compact(right))
            throw new ExtractionException($"{description} source comparison changed.");
    }

    private static void RequireIfWithOperand(SyntaxNode node, string operand, string description)
    {
        if (!node.DescendantNodes().OfType<IfStatementSyntax>()
            .Any(ifNode => ContainsOperand(ifNode.Condition, operand)))
            throw new ExtractionException($"{description} source guard changed.");
    }

    private static void RequirePattern(SyntaxNode node, string operand, string pattern, string description)
    {
        if (!node.DescendantNodes().OfType<IsPatternExpressionSyntax>().Any(expression =>
                ContainsOperand(expression.Expression, operand) &&
                Compact(expression.Pattern.ToString()).Contains(pattern, StringComparison.Ordinal)))
            throw new ExtractionException($"{description} source pattern changed.");
    }

    private static void RequireBinaryWithOperands(
        SyntaxNode node,
        string left,
        string right,
        string? third,
        string description)
    {
        bool found = node.DescendantNodes().OfType<BinaryExpressionSyntax>().Any(binary =>
            ContainsOperand(binary, left) && ContainsOperand(binary, right) &&
            (third is null || ContainsOperand(binary, third)));
        if (!found)
            throw new ExtractionException($"{description} source comparison changed.");
    }

    private static bool ContainsOperand(SyntaxNode? node, string operand) =>
        node is not null && Compact(node.ToString()).Contains(Compact(operand), StringComparison.Ordinal);

    private static void RequireExactResultFields(SyntaxNode root)
    {
        StructDeclarationSyntax result = root.DescendantNodes()
            .OfType<StructDeclarationSyntax>()
            .SingleOrDefault(type => type.Identifier.Text == "TransactionGasInitializationResult")
            ?? throw new ExtractionException("TransactionGasInitializationResult was not found.");
        (string Name, string Type, string Initializer)[] expected =
        [
            ("Outcome", "TransactionGasInitializationOutcome", "outcome"),
            ("Value", "ulong", "value"),
            ("StateReservoir", "long", "stateReservoir"),
            ("StateGasUsed", "long", "stateGasUsed"),
            ("StateGasSpill", "long", "stateGasSpill"),
            ("StateGasSpillRefunded", "long", "stateGasSpillRefunded"),
        ];
        FieldDeclarationSyntax[] fields = result.Members.OfType<FieldDeclarationSyntax>().ToArray();
        if (fields.Length != expected.Length)
            throw new ExtractionException("TransactionGasInitializationResult field count changed.");
        for (int index = 0; index < expected.Length; index++)
        {
            FieldDeclarationSyntax field = fields[index];
            VariableDeclaratorSyntax[] variables = field.Declaration.Variables.ToArray();
            if (variables.Length != 1 || variables[0].Identifier.Text != expected[index].Name ||
                Compact(field.Declaration.Type.ToString()) != expected[index].Type ||
                !field.Modifiers.Any(SyntaxKind.PublicKeyword) ||
                !field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) ||
                Compact(variables[0].Initializer?.Value.ToString() ?? string.Empty) != expected[index].Initializer)
                throw new ExtractionException($"TransactionGasInitializationResult field {expected[index].Name} changed.");
        }
    }

    private static void RequireMethod(ClassDeclarationSyntax owner, string methodName, string signaturePrefix)
    {
        MethodDeclarationSyntax method = owner.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(candidate => candidate.Identifier.Text == methodName)
            ?? throw new ExtractionException($"Admitted method {methodName} was not found on {owner.Identifier.Text}.");
        if (!Compact(method.Modifiers.ToString() + method.ReturnType).StartsWith(Compact(signaturePrefix), StringComparison.Ordinal))
            throw new ExtractionException($"Admitted method {methodName} signature drifted.");
    }

    private static MethodDeclarationSyntax FindMethod(SyntaxNode root, string className, int typeParameterCount, string methodName)
    {
        TypeDeclarationSyntax owner = FindType(root, className, typeParameterCount);
        return owner.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(method => method.Identifier.Text == methodName)
            ?? throw new ExtractionException($"Admitted method {className}.{methodName} was not found.");
    }

    private static MethodDeclarationSyntax FindAnyMethod(SyntaxNode root, string methodName) =>
        root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(method => method.Identifier.Text == methodName)
            ?? throw new ExtractionException($"Admitted source method {methodName} was not found.");

    private static PropertyDeclarationSyntax FindProperty(SyntaxNode root, string propertyName) =>
        root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .SingleOrDefault(property => property.Identifier.Text == propertyName)
            ?? throw new ExtractionException($"Admitted source property {propertyName} was not found.");

    private static void RequirePropertyType(TypeDeclarationSyntax owner, string propertyName, string expectedType)
    {
        PropertyDeclarationSyntax property = owner.Members.OfType<PropertyDeclarationSyntax>()
            .SingleOrDefault(candidate => candidate.Identifier.Text == propertyName)
            ?? throw new ExtractionException($"Admitted source property {owner.Identifier.Text}.{propertyName} was not found.");
        if (Compact(property.Type.ToString()) != Compact(expectedType))
            throw new ExtractionException($"{owner.Identifier.Text}.{propertyName} fixed-width type changed.");
    }

    private static void RequireFieldType(TypeDeclarationSyntax owner, string fieldName, string expectedType)
    {
        FieldDeclarationSyntax field = owner.Members.OfType<FieldDeclarationSyntax>()
            .SingleOrDefault(declaration => declaration.Declaration.Variables.Any(variable =>
                variable.Identifier.Text == fieldName))
            ?? throw new ExtractionException($"Admitted source field {owner.Identifier.Text}.{fieldName} was not found.");
        if (field.Declaration.Variables.Count != 1 ||
            Compact(field.Declaration.Type.ToString()) != Compact(expectedType))
            throw new ExtractionException($"{owner.Identifier.Text}.{fieldName} fixed-width type or declaration shape changed.");
    }

    private static void RequireMethodShape(
        TypeDeclarationSyntax owner,
        string methodName,
        string expectedReturnType,
        IReadOnlyList<string> expectedParameters)
    {
        MethodDeclarationSyntax method = owner.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(candidate => candidate.Identifier.Text == methodName)
            ?? throw new ExtractionException($"Admitted source method {owner.Identifier.Text}.{methodName} was not found.");
        string[] parameters = method.ParameterList.Parameters
            .Select(parameter => Compact(parameter.ToString())).ToArray();
        if (Compact(method.ReturnType.ToString()) != Compact(expectedReturnType) ||
            !parameters.SequenceEqual(expectedParameters, StringComparer.Ordinal))
            throw new ExtractionException($"{owner.Identifier.Text}.{methodName} fixed-width signature changed.");
    }

    private static TypeDeclarationSyntax FindType(SyntaxNode root, string typeName, int typeParameterCount) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .SingleOrDefault(type => type.Identifier.Text == typeName &&
                (type.TypeParameterList?.Parameters.Count ?? 0) == typeParameterCount)
            ?? throw new ExtractionException($"Admitted type {typeName}/{typeParameterCount} was not found.");

    private static ClassDeclarationSyntax FindClass(SyntaxNode root, string className, int typeParameterCount) =>
        root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(type => type.Identifier.Text == className &&
                (type.TypeParameterList?.Parameters.Count ?? 0) == typeParameterCount)
            ?? throw new ExtractionException($"Admitted class {className}/{typeParameterCount} was not found.");

    private static void RequireAnchors(SyntaxNode node, string name, IReadOnlyList<string> anchors)
    {
        string compact = Compact(node.ToString());
        foreach (string anchor in anchors)
            if (!compact.Contains(anchor, StringComparison.Ordinal))
                throw new ExtractionException($"{name} is missing source-bound anchor '{anchor}'.");
    }

    private static void RequireNoUnsupportedControlFlow(SyntaxNode node, string name)
    {
        Type[] unsupported =
        [
            typeof(ForStatementSyntax), typeof(ForEachStatementSyntax), typeof(WhileStatementSyntax),
            typeof(DoStatementSyntax), typeof(SwitchStatementSyntax), typeof(TryStatementSyntax),
            typeof(LocalFunctionStatementSyntax), typeof(LockStatementSyntax), typeof(UsingStatementSyntax),
            typeof(AwaitExpressionSyntax),
        ];
        foreach (SyntaxNode child in node.DescendantNodesAndSelf())
            if (unsupported.Any(type => type.IsInstanceOfType(child)))
                throw new ExtractionException($"{name} contains unsupported source syntax {child.Kind()}.");
    }

    private static SourceFile ReadSource(string root, SourceSpec specification)
    {
        string path = Resolve(root, specification.Path);
        if (!File.Exists(path))
            throw new ExtractionException($"Missing admitted source {specification.Path}.");
        byte[] bytes = File.ReadAllBytes(path);
        _ = StrictUtf8.GetString(bytes);
        return new(specification.Path, specification.Role, bytes, Hash(bytes));
    }

    private static SyntaxTree Parse(SourceFile source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(StrictUtf8.GetString(source.Bytes), ParseOptions, source.Path, StrictUtf8);
        Diagnostic? error = tree.GetDiagnostics().FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (error is not null)
            throw new ExtractionException($"Roslyn rejected {source.Path}: {error.Id} {error.GetMessage()}.");
        return tree;
    }

    private static string TokenHash(SyntaxNode node)
    {
        StringBuilder builder = new();
        foreach (SyntaxToken token in node.DescendantTokens(descendIntoTrivia: false))
        {
            builder.Append(token.RawKind).Append(':').Append(token.Text).Append('|');
        }

        return Hash(StrictUtf8.GetBytes(builder.ToString()));
    }

    private static string Compact(string text) => new(text.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private static string Resolve(string root, string relativePath)
    {
        string full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithin(root, full);
        return full;
    }

    private static void EnsureWithin(string root, string path)
    {
        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string pathFull = Path.GetFullPath(path);
        if (!pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException($"Path escaped repository/output root: {path}.");
    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static T Deserialize<T>(byte[] bytes, string name)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new ExtractionException($"Serialized {name} was empty.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized {name} is invalid JSON: {exception.Message}");
        }
    }

    private static void ValidateIr(IrDocument document)
    {
        if (document.SchemaVersion != 1 || document.ExtractorVersion != ExtractorVersion ||
            document.Kernel != KernelName || document.AcceptanceState != AcceptanceState ||
            document.StaticInputFields.Length != 18 || document.InitializationInputFields.Length != 5 ||
            document.ResultFields.Length != 6 || document.Branches.Length != 12 ||
            document.Functions.Length != 4 || document.Sources.Length != SourceClosure.Length)
            throw new ExtractionException("Static-admission IR header, field shape, or branch count changed.");
        if (!document.Branches.Select(static branch => branch.Ordinal).SequenceEqual(Enumerable.Range(1, 12)))
            throw new ExtractionException("Static-admission branch order is not contiguous 1..12.");
        string[] expectedBranchIds =
        [
            "sender-absent", "nonce-overflow", "initcode-oversize", "setcode-creation",
            "setcode-auth-empty", "intrinsic-cap", "execution-intrinsic", "floor-intrinsic",
            "minimum-intrinsic", "eip8037-block-limit", "legacy-block-limit", "ok",
        ];
        if (!document.Branches.Select(static branch => branch.Id)
                .SequenceEqual(expectedBranchIds, StringComparer.Ordinal))
            throw new ExtractionException("Static-admission branch identity/order changed.");
        int[] expectedGuardDepths = [1, 1, 1, 2, 2, 1, 1, 1, 1, 3, 3, 0];
        if (!document.Branches.Select(static branch => branch.GuardPath.Length)
                .SequenceEqual(expectedGuardDepths))
            throw new ExtractionException("Static-admission enclosing guard paths changed.");
        if (document.Branches.Any(static branch => branch.GuardPath.Any(static guard => string.IsNullOrEmpty(guard.Source)) ||
                string.IsNullOrEmpty(branch.SourceReturnExpression)))
            throw new ExtractionException("Static-admission IR lost source guard-path or return-site identity.");
        if (document.Branches.Any(static branch => branch.Id != "legacy-block-limit" &&
                branch.GuardPath.Any(static guard => guard.IsSyntheticFallthrough)) ||
            document.Branches.Count(static branch => branch.Id == "legacy-block-limit") != 1 ||
            document.Branches.Single(static branch => branch.Id == "legacy-block-limit").GuardPath.Count(static guard => guard.IsSyntheticFallthrough) != 1)
            throw new ExtractionException("Static-admission fallthrough guard identity changed.");
        if (!document.ResultFields.Select(static field => field.Name).SequenceEqual(
                ["outcome", "value", "stateReservoir", "stateGasUsed", "stateGasSpill", "stateGasSpillRefunded"], StringComparer.Ordinal))
            throw new ExtractionException("Initializer result field order changed.");
        if (document.ExternalObligations.Length < 7 || document.SourceWidthRules.Length < 5)
            throw new ExtractionException("Static-admission IR lost scope or width obligations.");
        if (document.Branches.Any(branch => branch.Operands.Length == 0 && branch.ConditionKind is not IrConditionKind.Else) ||
            document.Functions.Any(function => function.Parameters.Length == 0 || function.Operations.Length == 0))
            throw new ExtractionException("Static-admission semantic IR lost typed operands or source operations.");
        string[] expectedShapeIds =
        [
            "isAboveInitCode", "supportsAuthorizationList", "setCodeValue", "setCodeNoCreation",
            "setCodeAuthorizationList", "intrinsicStandardGas", "intrinsicMinRequiredGasLimit",
            "intrinsicExceedsCap", "skipValidation",
        ];
        if (!document.Shapes.Select(static shape => shape.Id).SequenceEqual(expectedShapeIds, StringComparer.Ordinal))
            throw new ExtractionException("Static-admission source shape identity/order changed.");
        if (document.CallMappings.Length != 1 || document.CallMappings[0].Id != "available-policy-bridge" ||
            document.CallMappings[0].ArgumentExpressions.Length != 5 ||
            document.CallMappings[0].OutputFields.Length != 5 ||
            document.CallMappings[0].OutputExpressions.Length != 5 ||
            document.CallMappings[0].FailureDefaultExpression != "default")
            throw new ExtractionException("Static-admission bridge mapping is incomplete.");
        if (document.Functions.Any(static function => function.Operations.Any(operation =>
                string.IsNullOrEmpty(operation.Source) ||
                operation.Kind is not (IrOperationKind.Assignment or IrOperationKind.Guard or IrOperationKind.Call or IrOperationKind.Return))))
            throw new ExtractionException("Static-admission IR contains an unlowered operation.");
        foreach (SourceBinding source in document.Sources)
        {
            RequireSha(source.Sha256, source.Path);
            RequireSha(source.TokenSha256, $"{source.Path} token identity");
        }
    }

    private static void ValidateManifest(Manifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.LanguageVersion != LanguageVersion.CSharp14.ToDisplayString() || manifest.Kernel != KernelName ||
            manifest.AcceptanceState != AcceptanceState || manifest.Sources.Length != SourceClosure.Length ||
            manifest.Dependencies.Length != 5)
            throw new ExtractionException("Static-admission source manifest header changed.");
        RequireSha(manifest.Ir.Sha256, "manifest IR");
        RequireSha(manifest.Lean.Sha256, "manifest Lean");
        RequireSha(manifest.CombinedSourceSha256, "manifest combined source");
        RequireSha(manifest.SemanticIrSha256, "manifest semantic IR");
        if (!manifest.Sources.Select(static source => source.Path).SequenceEqual(SourceClosure.Select(static source => source.Path), StringComparer.Ordinal))
            throw new ExtractionException("Static-admission source manifest path/order changed.");
        if (manifest.CombinedSourceSha256 != CombinedSourceHash(manifest.Sources))
            throw new ExtractionException("Static-admission combined source fingerprint changed.");
        string[] expectedDependencies = [
            CanonicalInitializerIrPath,
            CanonicalInitializerManifestPath,
            CanonicalInitializerGeneratedPath,
            CanonicalInitializerRefinementPath,
            CanonicalTransactionGasPath,
        ];
        if (!manifest.Dependencies.Select(static dependency => dependency.Path)
                .SequenceEqual(expectedDependencies, StringComparer.Ordinal))
            throw new ExtractionException("Static-admission Lean dependency paths changed.");
        foreach (ArtifactIdentity dependency in manifest.Dependencies)
            RequireSha(dependency.Sha256, $"manifest dependency {dependency.Path}");
    }

    private static string CombinedSourceHash(IEnumerable<SourceBinding> bindings) =>
        Hash(StrictUtf8.GetBytes(string.Join("\n", bindings.Select(static binding =>
            $"{binding.Path}|{binding.Role}|{binding.Owner}|{binding.Member}|{binding.Signature}|{binding.Sha256}|{binding.TokenSha256}"))));

    private static void ValidateCanonicalInitializerSource(string root, SourceFile initializationSource)
    {
        string manifestPath = Resolve(root, CanonicalInitializerManifestPath);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement source = manifest.RootElement.GetProperty("source");
        if (source.GetProperty("path").GetString() != InitializationPath ||
            source.GetProperty("sha256").GetString() != initializationSource.Sha256)
            throw new ExtractionException("Canonical initializer manifest is not bound to the admitted production source.");

        JsonElement artifact = manifest.RootElement.GetProperty("artifact");
        if (artifact.GetProperty("sha256").GetString() !=
            Hash(File.ReadAllBytes(Resolve(root, CanonicalInitializerIrPath))))
            throw new ExtractionException("Canonical initializer IR identity drifted from its manifest.");

        JsonElement leanArtifact = manifest.RootElement.GetProperty("leanArtifact");
        if (leanArtifact.GetProperty("sha256").GetString() !=
            Hash(File.ReadAllBytes(Resolve(root, CanonicalInitializerGeneratedPath))))
            throw new ExtractionException("Canonical initializer Lean identity drifted from its manifest.");
    }

    private static ArtifactIdentity[] CanonicalDependencies(string root) =>
        new[] {
            CanonicalInitializerIrPath,
            CanonicalInitializerManifestPath,
            CanonicalInitializerGeneratedPath,
            CanonicalInitializerRefinementPath,
            CanonicalTransactionGasPath,
        }
            .Select(path =>
            {
                string fullPath = Resolve(root, path);
                if (!File.Exists(fullPath))
                    throw new ExtractionException($"Missing pinned Lean dependency {path}.");
                return new ArtifactIdentity(path, Hash(File.ReadAllBytes(fullPath)));
            })
            .ToArray();

    private static void RequireSha(string value, string name)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ExtractionException($"{name} is not a SHA-256 identity.");
    }

    private static void RequireText(string source, string expected, string name)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
            throw new ExtractionException($"{name} source definition drifted; missing '{expected}'.");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void WriteIfChanged(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(path, bytes);
    }

    private static void RequireEqual(string path, byte[] expected, string artifactName)
    {
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
            throw new ExtractionException($"Checked-in {artifactName} artifact drifted: {path}.");
    }

    private sealed record SourceSpec(string Path, string Role, string Description);
    private sealed record BranchSpec(
        string Id,
        int Ordinal,
        string SourceMethod,
        string Condition,
        string Error,
        string ReturnSite,
        string ConditionAnchor);
    private sealed record ArtifactSet(IrDocument Document, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes);
}
