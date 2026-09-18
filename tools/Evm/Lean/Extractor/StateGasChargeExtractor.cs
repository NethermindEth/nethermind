// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using System.Globalization;
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

namespace Nethermind.Evm.Lean.Extractor;

internal sealed class ExtractionException(string message) : Exception(message);

internal sealed record ExtractionResult(
    string SourcePath,
    string IrPath,
    string ManifestPath,
    string LeanPath,
    int MethodCount);

internal static class StateGasChargeExtractor
{
    private const int SchemaVersion = 3;
    private const string ExtractorVersion = "1.2.0";
    private const string RootTypeMetadataName = "Nethermind.Evm.GasPolicy.StateGasChargeKernel";
    private const string RootMethodName = "TryCharge";
    private const string SourceRelativePath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs";
    private const string IrFileName = "StateGasChargeKernel.ir.json";
    private const string ManifestFileName = "StateGasChargeKernel.source-manifest.json";
    private const string LeanRelativePath = "tools/Evm/Lean/Eip803x/Generated/StateGasChargeKernel.lean";
    private const string TransitionTypeMetadataName = "Nethermind.Evm.GasPolicy.StateGasTransitionKernel";
    private const string TransitionSourceRelativePath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs";
    private const string TransitionIrFileName = "StateGasTransitionKernel.ir.json";
    private const string TransitionManifestFileName = "StateGasTransitionKernel.source-manifest.json";
    private const string TransitionLeanRelativePath = "tools/Evm/Lean/Eip803x/Generated/StateGasTransitionKernel.lean";
    private const string TransitionAdapterTypeMetadataName = "Nethermind.Evm.GasPolicy.StateGasTransitionAdapterKernel";
    private const string TransitionAdapterSourceRelativePath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs";
    private const string EthereumGasPolicySourceRelativePath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string TransitionAdapterIrFileName = "StateGasTransitionAdapterKernel.ir.json";
    private const string TransitionAdapterManifestFileName = "StateGasTransitionAdapterKernel.source-manifest.json";
    private const string TransitionAdapterLeanRelativePath = "tools/Evm/Lean/Eip803x/Generated/StateGasTransitionAdapterKernel.lean";
    private const string TransactionTypeMetadataName = "Nethermind.Evm.GasPolicy.TransactionGasInitializationKernel";
    private const string BlockAccountingTypeMetadataName = "Nethermind.Evm.GasPolicy.BlockGasAccountingKernel";
    private const string TransactionSourceRelativePath = "src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs";
    private const string TransactionIrFileName = "TransactionGasInitializationKernel.ir.json";
    private const string TransactionManifestFileName = "TransactionGasInitializationKernel.source-manifest.json";
    private const string TransactionLeanRelativePath = "tools/Evm/Lean/Eip803x/Generated/TransactionGasInitializationKernel.lean";
    private const string TransactionSettlementTypeMetadataName = "Nethermind.Evm.TransactionProcessing.TransactionSettlementKernel";
    private const string TransactionSettlementSourceRelativePath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionSettlementKernel.cs";
    private const string TransactionSettlementIrFileName = "TransactionSettlementKernel.ir.json";
    private const string TransactionSettlementManifestFileName = "TransactionSettlementKernel.source-manifest.json";
    private const string TransactionSettlementLeanRelativePath = "tools/Evm/Lean/Eip803x/Generated/TransactionSettlementKernel.lean";
    private const string BlockGasInclusionTypeMetadataName = "Nethermind.Evm.GasPolicy.Eip8037BlockGasInclusionCheck";
    private const string BlockGasInclusionSourceRelativePath = "src/Nethermind/Nethermind.Evm/GasPolicy/Eip8037BlockGasInclusionCheck.cs";
    private const string Eip7825ConstantsSourceRelativePath = "src/Nethermind/Nethermind.Core/Eip7825Constants.cs";
    private const string UInt64ExtensionsSourceRelativePath = "src/Nethermind/Nethermind.Core/Extensions/UInt64Extensions.cs";
    private const string BlockGasInclusionIrFileName = "Eip8037BlockGasInclusionCheck.ir.json";
    private const string BlockGasInclusionManifestFileName = "Eip8037BlockGasInclusionCheck.source-manifest.json";
    private const string BlockGasInclusionLeanRelativePath = "tools/Evm/Lean/Eip803x/Generated/Eip8037BlockGasInclusionCheck.lean";
    private const string BlockReceiptGasAccountingTypeMetadataName = "Nethermind.Blockchain.Tracing.BlockReceiptGasAccountingKernel";
    private const string BlockReceiptGasAccountingSourceRelativePath =
        "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs";
    private const string BlockReceiptsTracerSourceRelativePath =
        "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs";
    private const string BlockReceiptGasAccountingIrFileName = "BlockReceiptGasAccountingKernel.ir.json";
    private const string BlockReceiptGasAccountingManifestFileName = "BlockReceiptGasAccountingKernel.source-manifest.json";
    private const string BlockReceiptGasAccountingLeanRelativePath =
        "tools/Evm/Lean/Eip803x/Generated/BlockReceiptGasAccountingKernel.lean";
    private const string SStorePricingTypeMetadataName = "Nethermind.Evm.GasPolicy.SStorePricingKernel";
    private const string SStorePricingSourceRelativePath = "src/Nethermind/Nethermind.Evm/GasPolicy/SStorePricingKernel.cs";
    private const string SStorePricingAdapterSourceRelativePath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs";
    private const string SStorePricingIrFileName = "SStorePricingKernel.ir.json";
    private const string SStorePricingManifestFileName = "SStorePricingKernel.source-manifest.json";
    private const string SStorePricingLeanRelativePath = "tools/Evm/Lean/Eip803x/Generated/SStorePricingKernel.lean";
    private const string AccountAccessPricingTypeMetadataName = "Nethermind.Evm.GasPolicy.AccountAccessPricingKernel";
    private const string AccountAccessPricingSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessPricingKernel.cs";
    private const string AccountAccessKindSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessKind.cs";
    private const string AccountAccessPricingAdapterSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string AccountAccessPricingCallSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs";
    private const string AccountAccessPricingSpecSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Spec.cs";
    private const string AccountAccessPricingIrFileName = "AccountAccessPricingKernel.ir.json";
    private const string AccountAccessPricingManifestFileName = "AccountAccessPricingKernel.source-manifest.json";
    private const string AccountAccessPricingLeanRelativePath =
        "tools/Evm/Lean/Eip803x/Generated/AccountAccessPricingKernel.lean";
    private const string ExtendedStackDecoderTypeMetadataName = "Nethermind.Evm.ExtendedStackDecoderKernel";
    private const string ExtendedStackDecoderSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/Instructions/ExtendedStackDecoderKernel.cs";
    private const string ExtendedStackDecoderAdapterSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Stack.cs";
    private const string ExtendedStackDecoderEvmSourceRelativeDirectory =
        "src/Nethermind/Nethermind.Evm";
    private const string ExtendedStackDecoderIrFileName = "ExtendedStackDecoderKernel.ir.json";
    private const string ExtendedStackDecoderManifestFileName = "ExtendedStackDecoderKernel.source-manifest.json";
    private const string ExtendedStackDecoderLeanRelativePath =
        "tools/Evm/Lean/Eip803x/Generated/ExtendedStackDecoderKernel.lean";
    private const string IdentityPrecompileTypeMetadataName =
        "Nethermind.Evm.Precompiles.IdentityPrecompileKernel";
    private const string IdentityPrecompileSourceRelativePath =
        "src/Nethermind/Nethermind.Evm.Precompiles/IdentityPrecompileKernel.cs";
    private const string IdentityPrecompileAdapterSourceRelativePath =
        "src/Nethermind/Nethermind.Evm.Precompiles/IdentityPrecompile.cs";
    private const string IdentityPrecompileIrFileName = "IdentityPrecompileKernel.ir.json";
    private const string IdentityPrecompileManifestFileName = "IdentityPrecompileKernel.source-manifest.json";
    private const string IdentityPrecompileLeanRelativePath =
        "tools/Evm/Lean/Eip803x/Generated/IdentityPrecompileKernel.lean";
    private const string PrecompileGasPricingTypeMetadataName =
        "Nethermind.Evm.GasPolicy.PrecompileGasPricingKernel";
    private const string PrecompileGasPricingSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs";
    private const string PrecompileGasPricingAdapterSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string PrecompileGasPricingContractSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    private const string PrecompileGasPricingFullFrameSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    private const string PrecompileGasPricingInlineSourceRelativePath =
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs";
    private const string PrecompileGasPricingMainnetDiSourceRelativePath =
        "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    private const string PrecompileGasPricingMainnetDiExtensionSourceRelativePath =
        "src/Nethermind/Nethermind.Core/ContainerBuilderExtensions.cs";
    private const string PrecompileGasPricingIrFileName = "PrecompileGasPricingKernel.ir.json";
    private const string PrecompileGasPricingManifestFileName = "PrecompileGasPricingKernel.source-manifest.json";
    private const string PrecompileGasPricingLeanRelativePath =
        "tools/Evm/Lean/Eip803x/Generated/PrecompileGasPricingKernel.lean";

    private const string TransactionSettlementPrelude = """
        using System;

        namespace Nethermind.Evm.GasPolicy
        {
            internal static class Eip8037BlockGasInclusionCheck
            {
                internal static ulong CalculateBlockExecutionGas(
                    ulong preRefundGas,
                    ulong blockStateGas,
                    ulong calldataFloor) =>
                    Math.Max(preRefundGas > blockStateGas ? preRefundGas - blockStateGas : 0UL, calldataFloor);
            }
        }
        """;

    private const string BlockGasInclusionPrelude = """
        namespace Nethermind.Core
        {
            public static class Eip7825Constants
            {
                public static readonly ulong DefaultTxGasLimitCap = 16_777_216;
            }
        }

        namespace Nethermind.Core.Extensions
        {
            public static class UInt64Extensions
            {
                public static ulong SaturatingSub(this ulong a, ulong b) => a > b ? a - b : 0UL;
            }
        }
        """;

    private const string BlockReceiptGasAccountingPrelude = """
        namespace Nethermind.Evm.GasPolicy
        {
            public static class EthereumGasPolicy
            {
                public static ulong CombineBlockGas(ulong blockExecutionGas, ulong blockStateGas) => 0UL;
            }
        }
        """;

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType |
                       SymbolDisplayMemberOptions.IncludeParameters |
                       SymbolDisplayMemberOptions.IncludeType |
                       SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                          SymbolDisplayParameterOptions.IncludeName |
                          SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        KernelExtraction kernel = PrepareKernel(repoRoot, SourceRelativePath, FindChargeRoots);
        IMethodSymbol root = kernel.Roots.Single();
        IrDocument document = new(
            SchemaVersion,
            RootTypeMetadataName + "." + RootMethodName,
            Display(root),
            kernel.Extracted.DataTypes,
            kernel.Extracted.Methods);

        byte[] irBytes = SerializeCanonical(document);
        return CompleteExtraction(
            kernel,
            outputDirectory,
            leanOutputPath,
            IrFileName,
            ManifestFileName,
            LeanRelativePath,
            RootTypeMetadataName + "." + RootMethodName,
            irBytes,
            (compilerVersion, sourceHash, irHash) => StateGasChargeLeanEmitter.Emit(
                kernel.SemanticModel,
                kernel.Extracted.Symbols,
                ExtractorVersion,
                compilerVersion,
                NormalizePath(SourceRelativePath),
                sourceHash,
                irHash));
    }

    public static ExtractionResult ExtractTransitions(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null)
    {
        KernelExtraction kernel = PrepareKernel(repoRoot, TransitionSourceRelativePath, FindTransitionRoots);
        TransitionKernelModel model = StateGasTransitionLeanEmitter.Normalize(
            kernel.SemanticModel,
            kernel.Roots,
            kernel.Extracted.Symbols);
        TransitionIrDocument document = new(
            SchemaVersion,
            TransitionTypeMetadataName,
            kernel.Roots.Select(static root => new IrRoot(root.Name, Display(root))).ToArray(),
            model);
        byte[] irBytes = SerializeCanonical(document);
        return CompleteExtraction(
            kernel,
            outputDirectory,
            leanOutputPath,
            TransitionIrFileName,
            TransitionManifestFileName,
            TransitionLeanRelativePath,
            TransitionTypeMetadataName,
            irBytes,
            (compilerVersion, sourceHash, irHash) => StateGasTransitionLeanEmitter.Emit(
                model,
                ExtractorVersion,
                compilerVersion,
                NormalizePath(TransitionSourceRelativePath),
                sourceHash,
                irHash));
    }

    public static ExtractionResult ExtractStateGasTransitionAdapter(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null)
    {
        KernelExtraction kernel = PrepareKernel(
            repoRoot,
            TransitionAdapterSourceRelativePath,
            FindStateGasTransitionAdapterRoots,
            ExtractionProfile.StateGasTransitionAdapter,
            [TransitionSourceRelativePath, EthereumGasPolicySourceRelativePath]);
        StateGasTransitionAdapterKernelModel model = StateGasTransitionAdapterLeanEmitter.Normalize(
            kernel.SemanticModel,
            kernel.Roots,
            kernel.Extracted.Symbols,
            Path.Combine(kernel.CanonicalRoot, EthereumGasPolicySourceRelativePath));
        StateGasTransitionAdapterIrDocument document = new(
            SchemaVersion,
            TransitionAdapterTypeMetadataName,
            kernel.Roots.Select(static root => new IrRoot(root.Name, Display(root))).ToArray(),
            model);
        byte[] irBytes = SerializeCanonical(document);
        return CompleteExtraction(
            kernel,
            outputDirectory,
            leanOutputPath,
            TransitionAdapterIrFileName,
            TransitionAdapterManifestFileName,
            TransitionAdapterLeanRelativePath,
            TransitionAdapterTypeMetadataName,
            irBytes,
            (compilerVersion, sourceHash, irHash) => StateGasTransitionAdapterLeanEmitter.Emit(
                model,
                ExtractorVersion,
                compilerVersion,
                NormalizePath(TransitionAdapterSourceRelativePath),
                sourceHash,
                irHash));
    }

    public static ExtractionResult ExtractTransactionGasInitialization(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null)
    {
        KernelExtraction kernel = PrepareKernel(repoRoot, TransactionSourceRelativePath, FindTransactionRoots);
        TransactionGasInitializationKernelModel model = TransactionGasInitializationLeanEmitter.Normalize(
            kernel.SemanticModel,
            kernel.Roots,
            kernel.Extracted.Symbols);
        TransactionGasInitializationIrDocument document = new(
            SchemaVersion,
            TransactionTypeMetadataName,
            kernel.Roots.Select(static root => new IrRoot(root.Name, Display(root))).ToArray(),
            model);
        byte[] irBytes = SerializeCanonical(document);
        return CompleteExtraction(
            kernel,
            outputDirectory,
            leanOutputPath,
            TransactionIrFileName,
            TransactionManifestFileName,
            TransactionLeanRelativePath,
            TransactionTypeMetadataName,
            irBytes,
            (compilerVersion, sourceHash, irHash) => TransactionGasInitializationLeanEmitter.Emit(
                model,
                ExtractorVersion,
                compilerVersion,
                NormalizePath(TransactionSourceRelativePath),
                sourceHash,
                irHash));
    }

    public static ExtractionResult ExtractBlockGasInclusion(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null)
    {
        KernelExtraction kernel = PrepareKernel(
            repoRoot,
            BlockGasInclusionSourceRelativePath,
            FindBlockGasInclusionRoots,
            ExtractionProfile.BlockGasInclusion,
            [Eip7825ConstantsSourceRelativePath, UInt64ExtensionsSourceRelativePath]);
        BlockGasInclusionKernelModel model = BlockGasInclusionLeanEmitter.Normalize(
            kernel.SemanticModel,
            kernel.Roots,
            kernel.Extracted.Symbols,
            Path.Combine(kernel.CanonicalRoot, Eip7825ConstantsSourceRelativePath),
            Path.Combine(kernel.CanonicalRoot, UInt64ExtensionsSourceRelativePath));
        BlockGasInclusionIrDocument document = new(
            SchemaVersion,
            BlockGasInclusionTypeMetadataName,
            kernel.Roots.Select(static root => new IrRoot(root.Name, Display(root))).ToArray(),
            model);
        byte[] irBytes = SerializeCanonical(document);
        return CompleteExtraction(
            kernel,
            outputDirectory,
            leanOutputPath,
            BlockGasInclusionIrFileName,
            BlockGasInclusionManifestFileName,
            BlockGasInclusionLeanRelativePath,
            BlockGasInclusionTypeMetadataName,
            irBytes,
            (compilerVersion, sourceHash, irHash) => BlockGasInclusionLeanEmitter.Emit(
                model,
                ExtractorVersion,
                compilerVersion,
                NormalizePath(BlockGasInclusionSourceRelativePath),
                sourceHash,
                irHash));
    }

    public static ExtractionResult ExtractBlockReceiptGasAccounting(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null)
    {
        KernelExtraction kernel = PrepareKernel(
            repoRoot,
            BlockReceiptGasAccountingSourceRelativePath,
            FindBlockReceiptGasAccountingRoots,
            ExtractionProfile.BlockReceiptGasAccounting,
            [
                TransactionSourceRelativePath,
                EthereumGasPolicySourceRelativePath,
                BlockReceiptsTracerSourceRelativePath,
            ]);
        BlockReceiptGasAccountingKernelModel model = BlockReceiptGasAccountingLeanEmitter.Normalize(
            kernel.SemanticModel,
            kernel.Roots,
            kernel.Extracted.Symbols,
            Path.Combine(kernel.CanonicalRoot, TransactionSourceRelativePath),
            Path.Combine(kernel.CanonicalRoot, EthereumGasPolicySourceRelativePath),
            Path.Combine(kernel.CanonicalRoot, BlockReceiptsTracerSourceRelativePath));
        BlockReceiptGasAccountingIrDocument document = new(
            SchemaVersion,
            BlockReceiptGasAccountingTypeMetadataName,
            kernel.Roots.Select(static root => new IrRoot(root.Name, Display(root))).ToArray(),
            model);
        byte[] irBytes = SerializeCanonical(document);
        return CompleteExtraction(
            kernel,
            outputDirectory,
            leanOutputPath,
            BlockReceiptGasAccountingIrFileName,
            BlockReceiptGasAccountingManifestFileName,
            BlockReceiptGasAccountingLeanRelativePath,
            BlockReceiptGasAccountingTypeMetadataName,
            irBytes,
            (compilerVersion, sourceHash, irHash) => BlockReceiptGasAccountingLeanEmitter.Emit(
                model,
                ExtractorVersion,
                compilerVersion,
                NormalizePath(BlockReceiptGasAccountingSourceRelativePath),
                sourceHash,
                irHash));
    }

    public static ExtractionResult ExtractTransactionSettlement(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null)
    {
        KernelExtraction kernel = PrepareKernel(
            repoRoot,
            TransactionSettlementSourceRelativePath,
            FindTransactionSettlementRoots,
            ExtractionProfile.TransactionSettlement,
            [BlockGasInclusionSourceRelativePath, UInt64ExtensionsSourceRelativePath]);
        TransactionSettlementKernelModel model = TransactionSettlementLeanEmitter.Normalize(
            kernel.SemanticModel,
            kernel.Roots,
            kernel.Extracted.Symbols,
            Path.Combine(kernel.CanonicalRoot, BlockGasInclusionSourceRelativePath),
            Path.Combine(kernel.CanonicalRoot, UInt64ExtensionsSourceRelativePath));
        TransactionSettlementIrDocument document = new(
            SchemaVersion,
            TransactionSettlementTypeMetadataName,
            kernel.Roots.Select(static root => new IrRoot(root.Name, Display(root))).ToArray(),
            kernel.Extracted.DataTypes,
            kernel.Extracted.Methods,
            model);
        byte[] irBytes = SerializeCanonical(document);
        return CompleteExtraction(
            kernel,
            outputDirectory,
            leanOutputPath,
            TransactionSettlementIrFileName,
            TransactionSettlementManifestFileName,
            TransactionSettlementLeanRelativePath,
            TransactionSettlementTypeMetadataName,
            irBytes,
            (compilerVersion, sourceHash, irHash) => TransactionSettlementLeanEmitter.Emit(
                model,
                ExtractorVersion,
                compilerVersion,
                NormalizePath(TransactionSettlementSourceRelativePath),
                sourceHash,
                irHash));
    }

    public static ExtractionResult ExtractSStorePricing(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null)
    {
        KernelExtraction kernel = PrepareKernel(
            repoRoot,
            SStorePricingSourceRelativePath,
            FindSStorePricingRoots,
            ExtractionProfile.SStorePricing,
            [SStorePricingAdapterSourceRelativePath]);
        SStorePricingKernelModel model = SStorePricingLeanEmitter.Normalize(
            kernel.SemanticModel,
            kernel.Roots,
            kernel.Extracted.Symbols);
        SStorePricingAdapterShape adapter = SStorePricingAdapterValidator.Validate(
            Path.Combine(kernel.CanonicalRoot, SStorePricingAdapterSourceRelativePath));
        SStorePricingIrDocument document = new(
            SchemaVersion,
            SStorePricingTypeMetadataName,
            kernel.Roots.Select(static root => new IrRoot(root.Name, Display(root))).ToArray(),
            kernel.Extracted.DataTypes,
            kernel.Extracted.Methods,
            model,
            adapter);
        byte[] irBytes = SerializeCanonical(document);
        return CompleteExtraction(
            kernel,
            outputDirectory,
            leanOutputPath,
            SStorePricingIrFileName,
            SStorePricingManifestFileName,
            SStorePricingLeanRelativePath,
            SStorePricingTypeMetadataName,
            irBytes,
            (compilerVersion, sourceHash, irHash) => SStorePricingLeanEmitter.Emit(
                model,
                ExtractorVersion,
                compilerVersion,
                NormalizePath(SStorePricingSourceRelativePath),
                sourceHash,
                irHash));
    }

    public static ExtractionResult ExtractAccountAccessPricing(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null)
    {
        KernelExtraction kernel = PrepareKernel(
            repoRoot,
            AccountAccessPricingSourceRelativePath,
            FindAccountAccessPricingRoots,
            ExtractionProfile.AccountAccessPricing,
            [
                AccountAccessKindSourceRelativePath,
                AccountAccessPricingAdapterSourceRelativePath,
                AccountAccessPricingCallSourceRelativePath,
                AccountAccessPricingSpecSourceRelativePath,
            ]);
        AccountAccessPricingKernelModel model = AccountAccessPricingLeanEmitter.Normalize(
            kernel.SemanticModel,
            kernel.Roots,
            kernel.Extracted.Symbols);
        AccountAccessPricingAdapterShape adapter = AccountAccessPricingAdapterValidator.Validate(
            Path.Combine(kernel.CanonicalRoot, AccountAccessPricingAdapterSourceRelativePath),
            Path.Combine(kernel.CanonicalRoot, AccountAccessPricingCallSourceRelativePath),
            Path.Combine(kernel.CanonicalRoot, AccountAccessPricingSpecSourceRelativePath));
        AccountAccessPricingIrDocument document = new(
            SchemaVersion,
            AccountAccessPricingTypeMetadataName,
            kernel.Roots.Select(static root => new IrRoot(root.Name, Display(root))).ToArray(),
            kernel.Extracted.DataTypes,
            kernel.Extracted.Methods,
            model,
            adapter);
        byte[] irBytes = SerializeCanonical(document);
        return CompleteExtraction(
            kernel,
            outputDirectory,
            leanOutputPath,
            AccountAccessPricingIrFileName,
            AccountAccessPricingManifestFileName,
            AccountAccessPricingLeanRelativePath,
            AccountAccessPricingTypeMetadataName,
            irBytes,
            (compilerVersion, sourceHash, irHash) => AccountAccessPricingLeanEmitter.Emit(
                model,
                ExtractorVersion,
                compilerVersion,
                NormalizePath(AccountAccessPricingSourceRelativePath),
                sourceHash,
                irHash));
    }

    public static ExtractionResult ExtractExtendedStackDecoder(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null)
    {
        IReadOnlyList<string> adapterSources = FindExtendedStackDecoderAdapterSources(repoRoot);
        KernelExtraction kernel = PrepareKernel(
            repoRoot,
            ExtendedStackDecoderSourceRelativePath,
            FindExtendedStackDecoderRoots,
            ExtractionProfile.ExtendedStackDecoder,
            adapterSources);
        ExtendedStackDecoderKernelModel model = ExtendedStackDecoderLeanEmitter.Normalize(
            kernel.SemanticModel,
            kernel.Roots,
            kernel.Extracted.Symbols);
        CSharpCompilation adapterCompilation = CreateExtendedStackDecoderAdapterCompilation(
            kernel,
            out SyntaxTree adapterSyntaxTree);
        ExtendedStackDecoderAdapterShape adapter = ExtendedStackDecoderAdapterValidator.Validate(
            adapterCompilation,
            adapterSyntaxTree,
            kernel.SourcePath);
        ExtendedStackDecoderIrDocument document = new(
            SchemaVersion,
            ExtendedStackDecoderTypeMetadataName,
            kernel.Roots.Select(static root => new IrRoot(root.Name, Display(root))).ToArray(),
            kernel.Extracted.DataTypes,
            kernel.Extracted.Methods,
            model,
            adapter);
        byte[] irBytes = SerializeCanonical(document);
        return CompleteExtraction(
            kernel,
            outputDirectory,
            leanOutputPath,
            ExtendedStackDecoderIrFileName,
            ExtendedStackDecoderManifestFileName,
            ExtendedStackDecoderLeanRelativePath,
            ExtendedStackDecoderTypeMetadataName,
            irBytes,
            (compilerVersion, sourceHash, irHash) => ExtendedStackDecoderLeanEmitter.Emit(
                model,
                ExtractorVersion,
                compilerVersion,
                NormalizePath(ExtendedStackDecoderSourceRelativePath),
                sourceHash,
                irHash));
    }

    public static ExtractionResult ExtractIdentityPrecompile(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null)
    {
        KernelExtraction kernel = PrepareKernel(
            repoRoot,
            IdentityPrecompileSourceRelativePath,
            FindIdentityPrecompileRoots,
            ExtractionProfile.IdentityPrecompile,
            [IdentityPrecompileAdapterSourceRelativePath]);
        IdentityPrecompileKernelModel model = IdentityPrecompileLeanEmitter.Normalize(
            kernel.Roots,
            kernel.Extracted.Symbols);
        IdentityPrecompileAdapterShape adapter = IdentityPrecompileAdapterValidator.Validate(
            Path.Combine(kernel.CanonicalRoot, IdentityPrecompileAdapterSourceRelativePath),
            kernel.SourcePath);
        IdentityPrecompileIrDocument document = new(
            SchemaVersion,
            IdentityPrecompileTypeMetadataName,
            kernel.Roots.Select(static root => new IrRoot(root.Name, Display(root))).ToArray(),
            kernel.Extracted.DataTypes,
            kernel.Extracted.Methods,
            model,
            adapter);
        byte[] irBytes = SerializeCanonical(document);
        return CompleteExtraction(
            kernel,
            outputDirectory,
            leanOutputPath,
            IdentityPrecompileIrFileName,
            IdentityPrecompileManifestFileName,
            IdentityPrecompileLeanRelativePath,
            IdentityPrecompileTypeMetadataName,
            irBytes,
            (compilerVersion, sourceHash, irHash) => IdentityPrecompileLeanEmitter.Emit(
                model,
                ExtractorVersion,
                compilerVersion,
                NormalizePath(IdentityPrecompileSourceRelativePath),
                sourceHash,
                irHash));
    }

    public static ExtractionResult ExtractPrecompileGasPricing(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null)
    {
        KernelExtraction kernel = PrepareKernel(
            repoRoot,
            PrecompileGasPricingSourceRelativePath,
            FindPrecompileGasPricingRoots,
            ExtractionProfile.PrecompileGasPricing,
            [
                PrecompileGasPricingAdapterSourceRelativePath,
                PrecompileGasPricingContractSourceRelativePath,
                PrecompileGasPricingFullFrameSourceRelativePath,
                PrecompileGasPricingInlineSourceRelativePath,
                PrecompileGasPricingMainnetDiSourceRelativePath,
                PrecompileGasPricingMainnetDiExtensionSourceRelativePath,
            ]);
        PrecompileGasPricingKernelModel model = PrecompileGasPricingLeanEmitter.Normalize(
            kernel.SemanticModel,
            kernel.Roots,
            kernel.Extracted.Symbols);
        PrecompileGasPricingAdapterShape adapter = PrecompileGasPricingAdapterValidator.Validate(
            Path.Combine(kernel.CanonicalRoot, PrecompileGasPricingAdapterSourceRelativePath),
            Path.Combine(kernel.CanonicalRoot, PrecompileGasPricingContractSourceRelativePath),
            Path.Combine(kernel.CanonicalRoot, PrecompileGasPricingFullFrameSourceRelativePath),
            Path.Combine(kernel.CanonicalRoot, PrecompileGasPricingInlineSourceRelativePath),
            Path.Combine(kernel.CanonicalRoot, PrecompileGasPricingMainnetDiSourceRelativePath),
            Path.Combine(kernel.CanonicalRoot, PrecompileGasPricingMainnetDiExtensionSourceRelativePath),
            kernel.SourcePath);
        PrecompileGasPricingIrDocument document = new(
            SchemaVersion,
            PrecompileGasPricingTypeMetadataName,
            kernel.Roots.Select(static root => new IrRoot(root.Name, Display(root))).ToArray(),
            kernel.Extracted.DataTypes,
            kernel.Extracted.Methods,
            model,
            adapter);
        byte[] irBytes = SerializeCanonical(document);
        return CompleteExtraction(
            kernel,
            outputDirectory,
            leanOutputPath,
            PrecompileGasPricingIrFileName,
            PrecompileGasPricingManifestFileName,
            PrecompileGasPricingLeanRelativePath,
            PrecompileGasPricingTypeMetadataName,
            irBytes,
            (compilerVersion, sourceHash, irHash) => PrecompileGasPricingLeanEmitter.Emit(
                model,
                ExtractorVersion,
                compilerVersion,
                NormalizePath(PrecompileGasPricingSourceRelativePath),
                sourceHash,
                irHash));
    }

    private static KernelExtraction PrepareKernel(
        string repoRoot,
        string sourceRelativePath,
        RootFinder findRoots,
        ExtractionProfile profile = ExtractionProfile.Standard,
        IReadOnlyList<string>? supportingSourceRelativePaths = null)
    {
        string canonicalRoot = Path.GetFullPath(repoRoot);
        string sourcePath = Path.GetFullPath(Path.Combine(canonicalRoot, sourceRelativePath));
        EnsurePathWithinRoot(canonicalRoot, sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new ExtractionException($"Pinned production source was not found: {sourcePath}");
        }

        byte[] sourceBytes = File.ReadAllBytes(sourcePath);
        SourceText sourceText = SourceText.From(sourceBytes, sourceBytes.Length, Encoding.UTF8, canBeEmbedded: true);
        CSharpParseOptions parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.CSharp14)
                .WithDocumentationMode(profile is ExtractionProfile.BlockGasInclusion or
                ExtractionProfile.TransactionSettlement or ExtractionProfile.StateGasTransitionAdapter
                or ExtractionProfile.BlockReceiptGasAccounting
                or ExtractionProfile.AccountAccessPricing or ExtractionProfile.ExtendedStackDecoder
                or ExtractionProfile.IdentityPrecompile or ExtractionProfile.PrecompileGasPricing
                ? DocumentationMode.Parse
                : DocumentationMode.Diagnose)
            .WithKind(SourceCodeKind.Regular);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(sourceText, parseOptions, sourcePath);
        List<SourceIdentity> supportingSources = [];
        List<SyntaxTree> supportingSyntaxTrees = [];
        foreach (string supportingSourceRelativePath in (supportingSourceRelativePaths ?? [])
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            string supportingSourcePath = Path.GetFullPath(Path.Combine(canonicalRoot, supportingSourceRelativePath));
            EnsurePathWithinRoot(canonicalRoot, supportingSourcePath);
            if (!File.Exists(supportingSourcePath))
            {
                throw new ExtractionException($"Pinned supporting source was not found: {supportingSourcePath}");
            }

            byte[] supportingSourceBytes = File.ReadAllBytes(supportingSourcePath);
            supportingSources.Add(new SourceIdentity(
                NormalizePath(supportingSourceRelativePath),
                Sha256(supportingSourceBytes)));
            if ((profile is ExtractionProfile.StateGasTransitionAdapter &&
                 supportingSourceRelativePath == TransitionSourceRelativePath) ||
                (profile is ExtractionProfile.AccountAccessPricing &&
                 supportingSourceRelativePath == AccountAccessKindSourceRelativePath))
            {
                SourceText supportingSourceText = SourceText.From(
                    supportingSourceBytes,
                    supportingSourceBytes.Length,
                    Encoding.UTF8,
                    canBeEmbedded: true);
                supportingSyntaxTrees.Add(CSharpSyntaxTree.ParseText(
                    supportingSourceText,
                    parseOptions,
                    supportingSourcePath));
            }
        }

        SyntaxTree[] syntaxTrees = profile switch
        {
            ExtractionProfile.BlockGasInclusion =>
                [syntaxTree, CSharpSyntaxTree.ParseText(BlockGasInclusionPrelude, parseOptions, "<block-gas-inclusion-prelude>")],
            ExtractionProfile.TransactionSettlement =>
                [syntaxTree, CSharpSyntaxTree.ParseText(TransactionSettlementPrelude, parseOptions, "<transaction-settlement-prelude>")],
            ExtractionProfile.StateGasTransitionAdapter => [syntaxTree, .. supportingSyntaxTrees],
            ExtractionProfile.BlockReceiptGasAccounting =>
                [syntaxTree, CSharpSyntaxTree.ParseText(BlockReceiptGasAccountingPrelude, parseOptions, "<block-receipt-gas-accounting-prelude>")],
            ExtractionProfile.AccountAccessPricing => [syntaxTree, .. supportingSyntaxTrees],
            _ => [syntaxTree],
        };
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Nethermind.Evm.Lean.ExtractedSource",
            syntaxTrees,
            GetPlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                checkOverflow: false,
                allowUnsafe: false,
                nullableContextOptions: NullableContextOptions.Enable,
                deterministic: true));

        RejectDiagnostics(compilation);
        SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: false);
        IReadOnlyList<IMethodSymbol> roots = findRoots(compilation, syntaxTree);
        ExtractedGraph extracted = ExtractClosedCallGraph(compilation, semanticModel, roots, profile);
        return new KernelExtraction(
            canonicalRoot,
            sourcePath,
            sourceRelativePath,
            Sha256(sourceBytes),
            supportingSources,
            semanticModel,
            roots,
            extracted);
    }

    private static IReadOnlyList<string> FindExtendedStackDecoderAdapterSources(string repoRoot)
    {
        string canonicalRoot = Path.GetFullPath(repoRoot);
        string evmSourceDirectory = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            ExtendedStackDecoderEvmSourceRelativeDirectory));
        EnsurePathWithinRoot(canonicalRoot, evmSourceDirectory);
        if (!Directory.Exists(evmSourceDirectory))
        {
            throw new ExtractionException(
                $"Extended-stack adapter source directory was not found: {evmSourceDirectory}");
        }

        string[] sources = Directory.EnumerateFiles(evmSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => IsMainnetEvmProjectSource(path, evmSourceDirectory))
            .Where(path => DeclaresPartialEvmInstructions(path))
            .Select(path => NormalizePath(Path.GetRelativePath(canonicalRoot, path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (sources.Length == 0 ||
            !sources.Contains(ExtendedStackDecoderAdapterSourceRelativePath, StringComparer.Ordinal))
        {
            throw new ExtractionException(
                "Extended-stack adapter source set must contain EvmInstructions.Stack.cs and every standard partial EvmInstructions declaration.");
        }

        return sources;
    }

    private static CSharpCompilation CreateExtendedStackDecoderAdapterCompilation(
        KernelExtraction kernel,
        out SyntaxTree adapterSyntaxTree)
    {
        CSharpParseOptions parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.CSharp14)
            .WithDocumentationMode(DocumentationMode.Parse)
            .WithKind(SourceCodeKind.Regular);
        List<SyntaxTree> syntaxTrees = [ParseSourceTree(kernel.SourcePath, parseOptions)];
        foreach (SourceIdentity source in kernel.SupportingSources)
        {
            syntaxTrees.Add(ParseSourceTree(Path.Combine(kernel.CanonicalRoot, source.Path), parseOptions));
        }

        RejectSyntaxDiagnostics(syntaxTrees);
        adapterSyntaxTree = syntaxTrees.SingleOrDefault(tree =>
            string.Equals(
                Path.GetFullPath(tree.FilePath),
                Path.GetFullPath(Path.Combine(kernel.CanonicalRoot, ExtendedStackDecoderAdapterSourceRelativePath)),
                StringComparison.OrdinalIgnoreCase))
            ?? throw new ExtractionException("Extended-stack semantic adapter compilation did not include EvmInstructions.Stack.cs.");
        return CSharpCompilation.Create(
            "Nethermind.Evm.Lean.ExtendedStackAdapter",
            syntaxTrees,
            GetPlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                checkOverflow: false,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable,
                deterministic: true));
    }

    private static SyntaxTree ParseSourceTree(string path, CSharpParseOptions parseOptions)
    {
        if (!File.Exists(path))
        {
            throw new ExtractionException($"Extended-stack semantic source was not found: {path}");
        }

        byte[] bytes = File.ReadAllBytes(path);
        return CSharpSyntaxTree.ParseText(
            SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true),
            parseOptions,
            path);
    }

    private static bool IsMainnetEvmProjectSource(string path, string evmSourceDirectory)
    {
        string relative = Path.GetRelativePath(evmSourceDirectory, path);
        string fileName = Path.GetFileName(relative);
        if (fileName.EndsWith(".zkevm.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(static segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                   segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                                   segment.Equals("zkevm", StringComparison.OrdinalIgnoreCase));
    }

    private static bool DeclaresPartialEvmInstructions(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true),
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14));
        return syntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Any(static declaration =>
            declaration.Identifier.ValueText == "EvmInstructions" &&
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword) &&
            !declaration.Ancestors().OfType<TypeDeclarationSyntax>().Any() &&
            GetContainingNamespace(declaration) == "Nethermind.Evm");
    }

    private static string GetContainingNamespace(SyntaxNode declaration) =>
        string.Join(
            ".",
            declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                .Reverse()
                .Select(static @namespace => @namespace.Name.ToString()));

    private static ExtractionResult CompleteExtraction(
        KernelExtraction kernel,
        string outputDirectory,
        string? leanOutputPath,
        string irFileName,
        string manifestFileName,
        string leanRelativePath,
        string rootIdentity,
        byte[] irBytes,
        LeanEmitter emitLean)
    {
        string irHash = Sha256(irBytes);
        string compilerVersion = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown";
        byte[] leanBytes = emitLean(compilerVersion, kernel.SourceHash, irHash);
        string leanHash = Sha256(leanBytes);
        string[] rootSignatures = kernel.Roots.Select(Display).Order(StringComparer.Ordinal).ToArray();
        SourceManifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            compilerVersion,
            LanguageVersion.CSharp14.ToDisplayString(),
            rootIdentity,
            rootSignatures[0],
            rootSignatures,
            new SourceIdentity(NormalizePath(kernel.SourceRelativePath), kernel.SourceHash),
            new ArtifactIdentity(irFileName, irHash),
            new ArtifactIdentity(NormalizePath(leanRelativePath), leanHash),
            kernel.Extracted.Methods.Select(static method => method.Signature).ToArray(),
            kernel.SupportingSources.Count == 0 ? null : kernel.SupportingSources);
        byte[] manifestBytes = SerializeCanonical(manifest);

        Directory.CreateDirectory(outputDirectory);
        string leanPath = leanOutputPath is null
            ? Path.GetFullPath(Path.Combine(kernel.CanonicalRoot, leanRelativePath))
            : Path.GetFullPath(leanOutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        string irPath = Path.Combine(outputDirectory, irFileName);
        string manifestPath = Path.Combine(outputDirectory, manifestFileName);
        WriteIfChanged(irPath, irBytes);
        WriteIfChanged(leanPath, leanBytes);
        WriteIfChanged(manifestPath, manifestBytes);

        return new ExtractionResult(
            kernel.SourcePath,
            irPath,
            manifestPath,
            leanPath,
            kernel.Extracted.Methods.Count);
    }

    private static IReadOnlyList<IMethodSymbol> FindChargeRoots(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree) =>
        [FindRoot(compilation, syntaxTree)];

    private static IReadOnlyList<IMethodSymbol> FindTransitionRoots(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree)
    {
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(TransitionTypeMetadataName)
            ?? throw new ExtractionException($"Required root type '{TransitionTypeMetadataName}' was not found.");
        string[] expectedPublicRoots =
        [
            "AddStateGasRefundToReservoir",
            "DiscardStateGas",
            "Refund",
            "RefundStateGas",
            "RemoveStateGasRefundFromReservoir",
            "RepayStateGasSpill",
            "RestoreChildStateGas",
            "RestoreChildStateGasOnHalt",
            "RevertRefundToHalt",
        ];
        string[] expectedHelpers =
        [
            "ClampToRefundAmount",
            "GetUnrefundedStateGasSpill",
            "PositivePart",
        ];
        IMethodSymbol[] methods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        string[] expectedMethods = expectedPublicRoots.Concat(expectedHelpers).Order(StringComparer.Ordinal).ToArray();
        if (methods.Length != expectedMethods.Length ||
            !methods.Select(static method => method.Name).SequenceEqual(expectedMethods, StringComparer.Ordinal) ||
            methods.Any(method => method.DeclaringSyntaxReferences.Length != 1 ||
                                  method.DeclaringSyntaxReferences[0].SyntaxTree != syntaxTree))
        {
            throw new ExtractionException(
                $"Root container '{TransitionTypeMetadataName}' does not match the pinned transition method set.");
        }

        IMethodSymbol[] roots = methods
            .Where(static method => method.DeclaredAccessibility == Accessibility.Public)
            .ToArray();
        IMethodSymbol[] helpers = methods
            .Where(static method => method.DeclaredAccessibility == Accessibility.Private)
            .ToArray();
        if (!roots.Select(static method => method.Name).SequenceEqual(expectedPublicRoots, StringComparer.Ordinal) ||
            !helpers.Select(static method => method.Name).SequenceEqual(expectedHelpers, StringComparer.Ordinal))
        {
            throw new ExtractionException(
                $"Root container '{TransitionTypeMetadataName}' does not expose the pinned public/private method set.");
        }

        if (type.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not ClassDeclarationSyntax typeDeclaration ||
            typeDeclaration.AttributeLists.Count != 0 ||
            typeDeclaration.BaseList is not null ||
            typeDeclaration.Members.Count != expectedMethods.Length ||
            typeDeclaration.Members.Any(static member => member is not MethodDeclarationSyntax))
        {
            throw new ExtractionException(
                $"Root container '{TransitionTypeMetadataName}' must contain only the pinned transition methods.");
        }

        StateGasTransitionLeanEmitter.ValidateRoots(roots);
        return roots;
    }

    private static IReadOnlyList<IMethodSymbol> FindStateGasTransitionAdapterRoots(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree)
    {
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(TransitionAdapterTypeMetadataName)
            ?? throw new ExtractionException($"Required root type '{TransitionAdapterTypeMetadataName}' was not found.");
        IMethodSymbol[] methods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        if (methods.Length != 9 ||
            methods.Any(method => method.DeclaredAccessibility != Accessibility.Public ||
                                  method.DeclaringSyntaxReferences.Length != 1 ||
                                  method.DeclaringSyntaxReferences[0].SyntaxTree != syntaxTree) ||
            type.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not ClassDeclarationSyntax typeDeclaration ||
            typeDeclaration.AttributeLists.Count != 0 ||
            typeDeclaration.BaseList is not null ||
            typeDeclaration.Members.Count != methods.Length ||
            typeDeclaration.Members.Any(static member => member is not MethodDeclarationSyntax))
        {
            throw new ExtractionException(
                $"Root container '{TransitionAdapterTypeMetadataName}' does not match the pinned adapter method set.");
        }

        StateGasTransitionAdapterLeanEmitter.ValidateRoots(methods);
        return methods;
    }

    private static IReadOnlyList<IMethodSymbol> FindTransactionRoots(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree)
    {
        IMethodSymbol initialize = FindPinnedStaticMethod(
            compilation,
            syntaxTree,
            TransactionTypeMetadataName,
            "TryCreate",
            "Nethermind.Evm.GasPolicy.TransactionGasInitializationResult",
            [
                SpecialType.System_UInt64,
                SpecialType.System_UInt64,
                SpecialType.System_Int64,
                SpecialType.System_Boolean,
                SpecialType.System_UInt64,
            ]);
        IMethodSymbol combine = FindPinnedStaticMethod(
            compilation,
            syntaxTree,
            BlockAccountingTypeMetadataName,
            "Combine",
            "ulong",
            [SpecialType.System_UInt64, SpecialType.System_UInt64]);
        TransactionGasInitializationLeanEmitter.ValidateRoots([initialize, combine]);
        return [initialize, combine];
    }

    private static IReadOnlyList<IMethodSymbol> FindSStorePricingRoots(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree)
    {
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(SStorePricingTypeMetadataName)
            ?? throw new ExtractionException($"Required root type '{SStorePricingTypeMetadataName}' was not found.");
        IMethodSymbol[] methods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        if (methods.Length != 2 ||
            !methods.Select(static method => method.Name).SequenceEqual(["Price", "PriceAfterAccess"], StringComparer.Ordinal) ||
            methods.Any(method => method.DeclaringSyntaxReferences.Length != 1 ||
                                  method.DeclaringSyntaxReferences[0].SyntaxTree != syntaxTree) ||
            type.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not ClassDeclarationSyntax typeDeclaration ||
            typeDeclaration.AttributeLists.Count != 0 || typeDeclaration.BaseList is not null ||
            typeDeclaration.Members.Count != 2 || typeDeclaration.Members.Any(static member => member is not MethodDeclarationSyntax))
        {
            throw new ExtractionException(
                $"Root container '{SStorePricingTypeMetadataName}' must contain only the pinned pricing methods.");
        }

        IMethodSymbol root = methods[0];
        SStorePricingLeanEmitter.ValidateRoots([root]);
        return [root];
    }

    private static IReadOnlyList<IMethodSymbol> FindAccountAccessPricingRoots(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree)
    {
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(AccountAccessPricingTypeMetadataName)
            ?? throw new ExtractionException($"Required root type '{AccountAccessPricingTypeMetadataName}' was not found.");
        IMethodSymbol[] methods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        if (methods.Length != 1 || methods[0].Name != "Price" ||
            methods.Any(method => method.DeclaringSyntaxReferences.Length != 1 ||
                                  method.DeclaringSyntaxReferences[0].SyntaxTree != syntaxTree) ||
            type.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not ClassDeclarationSyntax typeDeclaration ||
            typeDeclaration.AttributeLists.Count != 0 || typeDeclaration.BaseList is not null ||
            typeDeclaration.Members.Count != 1 || typeDeclaration.Members[0] is not MethodDeclarationSyntax)
        {
            throw new ExtractionException(
                $"Root container '{AccountAccessPricingTypeMetadataName}' must contain only the pinned Price method.");
        }

        IMethodSymbol root = methods[0];
        AccountAccessPricingLeanEmitter.ValidateRoots([root]);
        return [root];
    }

    private static IReadOnlyList<IMethodSymbol> FindExtendedStackDecoderRoots(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree)
    {
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(ExtendedStackDecoderTypeMetadataName)
            ?? throw new ExtractionException($"Required root type '{ExtendedStackDecoderTypeMetadataName}' was not found.");
        IMethodSymbol[] methods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        if (!type.IsStatic || methods.Length != 2 ||
            !methods.Select(static method => method.Name).SequenceEqual(["DecodePair", "DecodeSingle"], StringComparer.Ordinal) ||
            methods.Any(method => method.DeclaringSyntaxReferences.Length != 1 ||
                                  method.DeclaringSyntaxReferences[0].SyntaxTree != syntaxTree) ||
            type.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not ClassDeclarationSyntax typeDeclaration ||
            typeDeclaration.AttributeLists.Count != 0 || typeDeclaration.BaseList is not null ||
            typeDeclaration.Members.Count != 2 || typeDeclaration.Members.Any(static member => member is not MethodDeclarationSyntax))
        {
            throw new ExtractionException(
                $"Root container '{ExtendedStackDecoderTypeMetadataName}' must contain only the pinned decoder methods.");
        }

        ExtendedStackDecoderLeanEmitter.ValidateRoots(methods);
        return methods;
    }

    private static IReadOnlyList<IMethodSymbol> FindIdentityPrecompileRoots(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree)
    {
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(IdentityPrecompileTypeMetadataName)
            ?? throw new ExtractionException(
                $"Required root type '{IdentityPrecompileTypeMetadataName}' was not found.");
        IMethodSymbol[] methods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        if (!type.IsStatic || type.DeclaredAccessibility != Accessibility.Internal || methods.Length != 2 ||
            !methods.Select(static method => method.Name)
                .SequenceEqual(["BaseGasCost", "DataGasCost"], StringComparer.Ordinal) ||
            methods.Any(method => method.DeclaringSyntaxReferences.Length != 1 ||
                                  method.DeclaringSyntaxReferences[0].SyntaxTree != syntaxTree) ||
            type.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not ClassDeclarationSyntax typeDeclaration ||
            typeDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            typeDeclaration.AttributeLists.Count != 0 || typeDeclaration.BaseList is not null ||
            typeDeclaration.Members.Count != 2 ||
            typeDeclaration.Members.Any(static member => member is not MethodDeclarationSyntax))
        {
            throw new ExtractionException(
                $"Root container '{IdentityPrecompileTypeMetadataName}' must contain only the pinned pricing methods.");
        }

        IdentityPrecompileLeanEmitter.ValidateRoots(methods);
        return methods;
    }

    private static IReadOnlyList<IMethodSymbol> FindPrecompileGasPricingRoots(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree)
    {
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(PrecompileGasPricingTypeMetadataName)
            ?? throw new ExtractionException(
                $"Required root type '{PrecompileGasPricingTypeMetadataName}' was not found.");
        IMethodSymbol[] methods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        if (!type.IsStatic || type.DeclaredAccessibility != Accessibility.Internal || methods.Length != 1 ||
            methods[0].Name != "TryConsume" ||
            methods[0].DeclaringSyntaxReferences.Length != 1 ||
            methods[0].DeclaringSyntaxReferences[0].SyntaxTree != syntaxTree ||
            type.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not ClassDeclarationSyntax typeDeclaration ||
            typeDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            typeDeclaration.AttributeLists.Count != 0 || typeDeclaration.BaseList is not null ||
            typeDeclaration.Members.Count != 1 || typeDeclaration.Members[0] is not MethodDeclarationSyntax)
        {
            throw new ExtractionException(
                $"Root container '{PrecompileGasPricingTypeMetadataName}' must contain only the pinned TryConsume method.");
        }

        PrecompileGasPricingLeanEmitter.ValidateRoots(methods);
        return methods;
    }

    private static IReadOnlyList<IMethodSymbol> FindBlockGasInclusionRoots(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree)
    {
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(BlockGasInclusionTypeMetadataName)
            ?? throw new ExtractionException($"Required root type '{BlockGasInclusionTypeMetadataName}' was not found.");
        IMethodSymbol[] methods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        if (methods.Length != 2 ||
            !methods.Select(static method => method.Name).SequenceEqual(
                ["CalculateBlockExecutionGas", "Validate"], StringComparer.Ordinal) ||
            methods.Any(method => method.DeclaringSyntaxReferences.Length != 1 ||
                                  method.DeclaringSyntaxReferences[0].SyntaxTree != syntaxTree))
        {
            throw new ExtractionException(
                $"Root container '{BlockGasInclusionTypeMetadataName}' must contain exactly the pinned inclusion methods.");
        }

        IMethodSymbol validate = methods.Single(static method => method.Name == "Validate");
        IMethodSymbol calculate = methods.Single(static method => method.Name == "CalculateBlockExecutionGas");
        ValidatePinnedStaticMethod(
            validate,
            "Nethermind.Evm.GasPolicy.Eip8037BlockGasInclusionCheck.Outcome",
            [SpecialType.System_UInt64, SpecialType.System_UInt64, SpecialType.System_UInt64, SpecialType.System_UInt64]);
        ValidatePinnedStaticMethod(
            calculate,
            "ulong",
            [SpecialType.System_UInt64, SpecialType.System_UInt64, SpecialType.System_UInt64]);

        INamedTypeSymbol outcome = type.GetTypeMembers("Outcome").SingleOrDefault()
            ?? throw new ExtractionException($"Root container '{BlockGasInclusionTypeMetadataName}' is missing Outcome.");
        if (outcome.TypeKind != TypeKind.Enum ||
            !outcome.GetMembers().OfType<IFieldSymbol>().Where(static field => field.HasConstantValue)
                .Select(static field => field.Name)
                .SequenceEqual(["Ok", "ExecutionDimensionExceeded", "StateDimensionExceeded"], StringComparer.Ordinal) ||
            type.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not ClassDeclarationSyntax typeDeclaration ||
            typeDeclaration.AttributeLists.Count != 0 ||
            typeDeclaration.BaseList is not null ||
            typeDeclaration.Members.Count != 3 ||
            typeDeclaration.Members.Any(static member => member is not MethodDeclarationSyntax and not EnumDeclarationSyntax))
        {
            throw new ExtractionException(
                $"Root container '{BlockGasInclusionTypeMetadataName}' does not match the pinned Outcome and method shape.");
        }

        BlockGasInclusionLeanEmitter.ValidateRoots([validate, calculate]);
        return [validate, calculate];
    }

    private static IReadOnlyList<IMethodSymbol> FindBlockReceiptGasAccountingRoots(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree)
    {
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(BlockReceiptGasAccountingTypeMetadataName)
            ?? throw new ExtractionException(
                $"Required root type '{BlockReceiptGasAccountingTypeMetadataName}' was not found.");
        IMethodSymbol[] methods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        if (methods.Length != 2 ||
            !methods.Select(static method => method.Name).SequenceEqual(
                ["Accumulate", "FromTotals"], StringComparer.Ordinal) ||
            methods.Any(method => method.DeclaringSyntaxReferences.Length != 1 ||
                                  method.DeclaringSyntaxReferences[0].SyntaxTree != syntaxTree) ||
            type.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not ClassDeclarationSyntax typeDeclaration ||
            typeDeclaration.AttributeLists.Count != 0 ||
            typeDeclaration.BaseList is not null ||
            typeDeclaration.Members.Count != 2 ||
            typeDeclaration.Members.Any(static member => member is not MethodDeclarationSyntax))
        {
            throw new ExtractionException(
                $"Root container '{BlockReceiptGasAccountingTypeMetadataName}' does not match the pinned accounting method set.");
        }

        IMethodSymbol accumulate = methods[0];
        IMethodSymbol fromTotals = methods[1];
        ValidatePinnedStaticMethod(
            accumulate,
            "Nethermind.Blockchain.Tracing.BlockReceiptGasAccountingResult",
            [
                SpecialType.System_UInt64,
                SpecialType.System_UInt64,
                SpecialType.System_UInt64,
                SpecialType.System_UInt64,
                SpecialType.System_UInt64,
                SpecialType.System_UInt64,
            ]);
        ValidatePinnedStaticMethod(
            fromTotals,
            "Nethermind.Blockchain.Tracing.BlockReceiptGasAccountingResult",
            [
                SpecialType.System_UInt64,
                SpecialType.System_UInt64,
                SpecialType.System_UInt64,
            ]);
        BlockReceiptGasAccountingLeanEmitter.ValidateRoots([accumulate, fromTotals]);
        return [accumulate, fromTotals];
    }

    private static IReadOnlyList<IMethodSymbol> FindTransactionSettlementRoots(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree)
    {
        IMethodSymbol calculate = FindPinnedStaticMethod(
            compilation,
            syntaxTree,
            TransactionSettlementTypeMetadataName,
            "Calculate",
            "Nethermind.Evm.TransactionProcessing.TransactionSettlementResult",
            [
                SpecialType.System_UInt64,
                SpecialType.System_UInt64,
                SpecialType.System_Int64,
                SpecialType.System_Int32,
                SpecialType.System_UInt64,
                SpecialType.System_UInt64,
                SpecialType.System_UInt64,
                SpecialType.System_Int64,
                SpecialType.System_UInt64,
                SpecialType.System_Boolean,
                SpecialType.System_Boolean,
                SpecialType.System_Boolean,
                SpecialType.System_Boolean,
            ]);
        TransactionSettlementLeanEmitter.ValidateRoots([calculate]);
        return [calculate];
    }

    private static void ValidatePinnedStaticMethod(
        IMethodSymbol method,
        string returnType,
        IReadOnlyList<SpecialType> parameterTypes)
    {
        if (!method.IsStatic || method.IsGenericMethod || method.ReturnsVoid || method.ReturnsByRef || method.ReturnsByRefReadonly ||
            method.ReturnType.ToDisplayString() != returnType ||
            method.Parameters.Length != parameterTypes.Count ||
            method.Parameters.Where((parameter, index) => parameter.Type.SpecialType != parameterTypes[index]).Any())
        {
            throw new ExtractionException($"Root '{Display(method)}' does not match its pinned signature.");
        }
    }

    private static IMethodSymbol FindPinnedStaticMethod(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree,
        string typeMetadataName,
        string methodName,
        string returnType,
        IReadOnlyList<SpecialType> parameterTypes)
    {
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(typeMetadataName)
            ?? throw new ExtractionException($"Required root type '{typeMetadataName}' was not found.");
        IMethodSymbol[] methods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .ToArray();
        if (methods.Length != 1 || methods[0].Name != methodName)
        {
            throw new ExtractionException(
                $"Root container '{typeMetadataName}' must contain exactly '{methodName}'.");
        }

        IMethodSymbol root = methods[0];
        if (!root.IsStatic || root.IsGenericMethod || root.ReturnsVoid || root.ReturnsByRef || root.ReturnsByRefReadonly ||
            root.ReturnType.ToDisplayString() != returnType ||
            root.Parameters.Length != parameterTypes.Count ||
            root.Parameters.Where((parameter, index) => parameter.Type.SpecialType != parameterTypes[index]).Any() ||
            root.DeclaringSyntaxReferences.Length != 1 || root.DeclaringSyntaxReferences[0].SyntaxTree != syntaxTree)
        {
            throw new ExtractionException($"Root '{Display(root)}' does not match its pinned signature.");
        }

        if (type.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not ClassDeclarationSyntax typeDeclaration ||
            typeDeclaration.AttributeLists.Count != 0 ||
            typeDeclaration.BaseList is not null ||
            typeDeclaration.Members.Count != 1 ||
            typeDeclaration.Members[0] is not MethodDeclarationSyntax)
        {
            throw new ExtractionException(
                $"Root container '{typeMetadataName}' must contain only its pinned method declaration.");
        }

        return root;
    }

    private static IMethodSymbol FindRoot(CSharpCompilation compilation, SyntaxTree syntaxTree)
    {
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(RootTypeMetadataName)
            ?? throw new ExtractionException($"Required root type '{RootTypeMetadataName}' was not found.");
        IMethodSymbol[] candidates = type.GetMembers(RootMethodName)
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .ToArray();

        if (candidates.Length != 1)
        {
            throw new ExtractionException(
                $"Expected exactly one '{RootTypeMetadataName}.{RootMethodName}' method, found {candidates.Length}.");
        }

        IMethodSymbol root = candidates[0];
        if (!root.IsStatic || root.IsGenericMethod || root.ReturnsVoid || root.ReturnsByRef || root.ReturnsByRefReadonly)
        {
            throw new ExtractionException($"Root '{Display(root)}' must be a non-generic static value function.");
        }

        SpecialType[] expectedParameters =
        [
            SpecialType.System_UInt64,
            SpecialType.System_Int64,
            SpecialType.System_Int64,
            SpecialType.System_Int64,
            SpecialType.System_Int64,
            SpecialType.System_Int64,
        ];
        if (root.ReturnType.ToDisplayString() != "Nethermind.Evm.GasPolicy.StateGasChargeResult" ||
            root.Parameters.Length != expectedParameters.Length ||
            root.Parameters.Where((parameter, index) => parameter.Type.SpecialType != expectedParameters[index]).Any())
        {
            throw new ExtractionException(
                $"Root '{Display(root)}' does not match the pinned TryCharge(ulong, long, long, long, long, long) signature.");
        }

        if (root.DeclaringSyntaxReferences.Length != 1 || root.DeclaringSyntaxReferences[0].SyntaxTree != syntaxTree)
        {
            throw new ExtractionException($"Root '{Display(root)}' is not uniquely declared in the pinned source.");
        }

        if (root.ContainingType.DeclaringSyntaxReferences is not [SyntaxReference typeReference] ||
            typeReference.GetSyntax() is not ClassDeclarationSyntax typeDeclaration ||
            typeDeclaration.AttributeLists.Count != 0 ||
            typeDeclaration.BaseList is not null ||
            typeDeclaration.Members.Count != 2 ||
            typeDeclaration.Members.Any(static member => member is not MethodDeclarationSyntax))
        {
            throw new ExtractionException(
                $"Root container '{RootTypeMetadataName}' must contain only the two pinned method declarations.");
        }

        return root;
    }

    private static ExtractedGraph ExtractClosedCallGraph(
        CSharpCompilation compilation,
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> roots,
        ExtractionProfile profile)
    {
        Queue<IMethodSymbol> pending = new();
        Dictionary<string, IrMethod> extracted = new(StringComparer.Ordinal);
        Dictionary<string, IMethodSymbol> extractedSymbols = new(StringComparer.Ordinal);
        Dictionary<string, IrDataType> dataTypes = new(StringComparer.Ordinal);
        foreach (IMethodSymbol root in roots)
        {
            pending.Enqueue(root);
        }

        while (pending.TryDequeue(out IMethodSymbol? method))
        {
            string signature = Display(method);
            if (extracted.ContainsKey(signature))
            {
                continue;
            }

            ValidateMethodShape(method);
            SyntaxNode declaration = method.DeclaringSyntaxReferences.Single().GetSyntax();
            SyntaxSubsetValidator.Validate(declaration);
            ValidateMethodAttributes(declaration, semanticModel, signature);
            IOperation body = semanticModel.GetOperation(declaration)
                ?? throw new ExtractionException($"Roslyn did not produce an operation body for '{signature}'.");
            ControlFlowGraph graph = CreateControlFlowGraph(body, signature);
            OperationSubsetValidator operationValidator = new(compilation, semanticModel, pending, dataTypes, profile);
            operationValidator.Validate(graph, signature);

            IrMethod irMethod = new(
                signature,
                Display(method.ReturnType),
                method.Parameters.Select(static parameter => new IrParameter(
                    parameter.Ordinal,
                    parameter.Name,
                    Display(parameter.Type))).ToArray(),
                graph.Blocks.Select(ToIrBlock).ToArray());
            extracted.Add(signature, irMethod);
            extractedSymbols.Add(signature, method);
        }

        return new ExtractedGraph(
            extracted.Values.OrderBy(static method => method.Signature, StringComparer.Ordinal).ToArray(),
            dataTypes.Values.OrderBy(static type => type.Type, StringComparer.Ordinal).ToArray(),
            extractedSymbols.Values.OrderBy(Display, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateMethodShape(IMethodSymbol method)
    {
        if (method.DeclaringSyntaxReferences.Length != 1 ||
            method.IsAsync ||
            method.IsExtern ||
            method.IsAbstract ||
            method.IsGenericMethod ||
            method.ReturnsByRef ||
            method.ReturnsByRefReadonly ||
            method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
        {
            throw new ExtractionException($"Method '{Display(method)}' is outside the restricted method subset.");
        }

        if (method.MethodKind == MethodKind.Ordinary && !method.IsStatic ||
            method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor))
        {
            throw new ExtractionException($"Method '{Display(method)}' is neither a static function nor a value constructor.");
        }

        INamedTypeSymbol containingType = method.ContainingType;
        bool validContainer = containingType.IsStatic || containingType.TypeKind == TypeKind.Struct && containingType.IsReadOnly;
        if (!validContainer)
        {
            throw new ExtractionException($"Containing type '{Display(containingType)}' must be static or a readonly struct.");
        }
    }

    private static void ValidateMethodAttributes(
        SyntaxNode declaration,
        SemanticModel semanticModel,
        string signature)
    {
        if (declaration is not BaseMethodDeclarationSyntax method)
        {
            throw new ExtractionException($"Declaration for '{signature}' is not a method.");
        }

        foreach (AttributeSyntax attribute in method.AttributeLists.SelectMany(static list => list.Attributes))
        {
            IMethodSymbol constructor = semanticModel.GetSymbolInfo(attribute).Symbol as IMethodSymbol
                ?? throw new ExtractionException($"Roslyn did not bind attribute '{attribute}' on '{signature}'.");
            SeparatedSyntaxList<AttributeArgumentSyntax> arguments = attribute.ArgumentList?.Arguments ?? default;
            Optional<object?> value = arguments.Count == 1
                ? semanticModel.GetConstantValue(arguments[0].Expression)
                : default;
            bool isAggressiveInlining =
                constructor.ContainingType.ToDisplayString() == typeof(System.Runtime.CompilerServices.MethodImplAttribute).FullName &&
                arguments.Count == 1 &&
                arguments[0].NameColon is null &&
                arguments[0].NameEquals is null &&
                value.HasValue &&
                Convert.ToInt32(value.Value, CultureInfo.InvariantCulture) ==
                    (int)System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining;
            if (!isAggressiveInlining)
            {
                throw new ExtractionException(
                    $"Attribute '{attribute}' on '{signature}' is outside the restricted subset.");
            }
        }
    }

    private static ControlFlowGraph CreateControlFlowGraph(IOperation body, string signature) =>
        body switch
        {
            IMethodBodyOperation methodBody => ControlFlowGraph.Create(methodBody),
            IConstructorBodyOperation constructorBody => ControlFlowGraph.Create(constructorBody),
            _ => throw new ExtractionException(
                $"Operation root '{body.Kind}' for '{signature}' is outside the restricted body subset."),
        };

    private static IrBlock ToIrBlock(BasicBlock block) => new(
        block.Ordinal,
        block.Kind.ToString(),
        block.ConditionKind.ToString(),
        block.Predecessors
            .Select(static branch => new IrPredecessor(branch.Source.Ordinal, branch.Semantics.ToString()))
            .OrderBy(static predecessor => predecessor.SourceOrdinal)
            .ThenBy(static predecessor => predecessor.Semantics, StringComparer.Ordinal)
            .ToArray(),
        block.Operations.Select(ToIrOperation).ToArray(),
        block.BranchValue is null ? null : ToIrOperation(block.BranchValue),
        ToIrBranch(block.FallThroughSuccessor),
        ToIrBranch(block.ConditionalSuccessor));

    private static IrBranch? ToIrBranch(ControlFlowBranch? branch)
    {
        if (branch is null)
        {
            return null;
        }

        return new IrBranch(branch.Semantics.ToString(), branch.Destination?.Ordinal);
    }

    private static IrOperation ToIrOperation(IOperation operation)
    {
        List<IrProperty> properties = [];
        switch (operation)
        {
            case IBinaryOperation binary:
                properties.Add(new("operator", binary.OperatorKind.ToString()));
                properties.Add(new("checked", binary.IsChecked.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()));
                properties.Add(new("lifted", binary.IsLifted.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()));
                break;
            case IUnaryOperation unary:
                properties.Add(new("operator", unary.OperatorKind.ToString()));
                properties.Add(new("checked", unary.IsChecked.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()));
                break;
            case IConversionOperation conversion:
                properties.Add(new("checked", conversion.IsChecked.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()));
                properties.Add(new("conversion", DescribeConversion(conversion.Conversion)));
                if (conversion.OperatorMethod is not null)
                {
                    properties.Add(new("operatorMethod", Display(conversion.OperatorMethod)));
                }
                break;
            case IInvocationOperation invocation:
                properties.Add(new("target", Display(invocation.TargetMethod)));
                break;
            case IObjectCreationOperation creation:
                properties.Add(new("constructor", creation.Constructor is null ? "null" : Display(creation.Constructor)));
                break;
            case IFieldReferenceOperation fieldReference:
                properties.Add(new("field", Display(fieldReference.Field)));
                break;
            case ILocalReferenceOperation localReference:
                properties.Add(new("local", LocalIdentity(localReference.Local)));
                break;
            case IParameterReferenceOperation parameterReference:
                properties.Add(new("parameter", parameterReference.Parameter.Ordinal.ToString(CultureInfo.InvariantCulture)));
                break;
            case IVariableDeclaratorOperation declarator:
                properties.Add(new("local", LocalIdentity(declarator.Symbol)));
                break;
            case IArgumentOperation argument:
                properties.Add(new("argumentKind", argument.ArgumentKind.ToString()));
                properties.Add(new("parameter", argument.Parameter?.Ordinal.ToString(CultureInfo.InvariantCulture) ?? "null"));
                break;
            case IFlowCaptureOperation capture:
                properties.Add(new("capture", capture.Id.ToString() ?? throw new ExtractionException("Roslyn returned an unnamed flow capture.")));
                break;
            case IFlowCaptureReferenceOperation captureReference:
                properties.Add(new("capture", captureReference.Id.ToString() ?? throw new ExtractionException("Roslyn returned an unnamed flow capture reference.")));
                break;
            case ISimpleAssignmentOperation assignment:
                properties.Add(new("ref", assignment.IsRef.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()));
                break;
        }

        return new IrOperation(
            operation.Kind.ToString(),
            operation.Type is null ? null : Display(operation.Type),
            operation.ConstantValue.HasValue ? FormatConstant(operation.ConstantValue.Value) : null,
            operation.IsImplicit,
            properties.OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray(),
            operation.ChildOperations.Select(ToIrOperation).ToArray());
    }

    private static string DescribeConversion(CommonConversion conversion) => string.Join(
        ",",
        conversion.Exists ? "exists" : "missing",
        conversion.IsIdentity ? "identity" : "nonidentity",
        conversion.IsNumeric ? "numeric" : "nonnumeric",
        conversion.IsReference ? "reference" : "nonreference",
        conversion.IsImplicit ? "implicit" : "explicit",
        conversion.IsNullable ? "nullable" : "nonnullable",
        conversion.IsUserDefined ? "user" : "builtin");

    private static string? FormatConstant(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static string LocalIdentity(ILocalSymbol local)
    {
        SyntaxReference? syntax = local.DeclaringSyntaxReferences.FirstOrDefault();
        return syntax is null ? local.Name : $"{local.Name}@{syntax.Span.Start.ToString(CultureInfo.InvariantCulture)}";
    }

    private static void RejectDiagnostics(CSharpCompilation compilation)
    {
        Diagnostic[] diagnostics = compilation.GetDiagnostics()
            .Where(static diagnostic => !diagnostic.IsSuppressed && diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            .OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)
            .ThenBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ToArray();
        if (diagnostics.Length == 0)
        {
            return;
        }

        string details = string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString()));
        throw new ExtractionException("Pinned source did not compile without diagnostics:" + Environment.NewLine + details);
    }

    private static void RejectSyntaxDiagnostics(IEnumerable<SyntaxTree> syntaxTrees)
    {
        Diagnostic[] diagnostics = syntaxTrees.SelectMany(static tree => tree.GetDiagnostics())
            .Where(static diagnostic => !diagnostic.IsSuppressed && diagnostic.Severity == DiagnosticSeverity.Error)
            .OrderBy(static diagnostic => diagnostic.Location.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Location.SourceSpan.Start)
            .ThenBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ToArray();
        if (diagnostics.Length == 0)
        {
            return;
        }

        string details = string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString()));
        throw new ExtractionException(
            "Extended-stack semantic adapter source did not parse without diagnostics:" + Environment.NewLine + details);
    }

    internal static MetadataReference[] GetPlatformReferences()
    {
        string trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new ExtractionException("The runtime did not provide TRUSTED_PLATFORM_ASSEMBLIES.");
        return trustedAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static byte[] SerializeCanonical<T>(T value)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        return Utf8WithoutBom.GetBytes(json);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
        {
            return;
        }

        File.WriteAllBytes(path, content);
    }

    private static void EnsurePathWithinRoot(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ExtractionException($"Pinned source escaped repository root '{root}'.");
        }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    internal static string Display(ISymbol symbol) => symbol.ToDisplayString(SignatureFormat);

    private sealed class OperationSubsetValidator(
        CSharpCompilation compilation,
        SemanticModel semanticModel,
        Queue<IMethodSymbol> pending,
        Dictionary<string, IrDataType> dataTypes,
        ExtractionProfile profile)
    {
        private readonly CSharpCompilation _compilation = compilation;
        private readonly SemanticModel _semanticModel = semanticModel;
        private readonly Queue<IMethodSymbol> _pending = pending;
        private readonly Dictionary<string, IrDataType> _dataTypes = dataTypes;
        private readonly ExtractionProfile _profile = profile;

        public void Validate(ControlFlowGraph graph, string signature)
        {
            foreach (BasicBlock block in graph.Blocks)
            {
                if (block.EnclosingRegion.Kind is not (ControlFlowRegionKind.Root or ControlFlowRegionKind.LocalLifetime))
                {
                    throw new ExtractionException(
                        $"Control-flow region '{block.EnclosingRegion.Kind}' in '{signature}' is unsupported.");
                }

                foreach (IOperation operation in block.Operations)
                {
                    Validate(operation, signature);
                }

                if (block.BranchValue is not null)
                {
                    Validate(block.BranchValue, signature);
                }
            }
        }

        private void Validate(IOperation operation, string signature)
        {
            bool allowed = operation is
                ILiteralOperation or
                IParameterReferenceOperation or
                ILocalReferenceOperation or
                IFieldReferenceOperation or
                IConversionOperation or
                IBinaryOperation or
                IUnaryOperation or
                IInvocationOperation or
                IObjectCreationOperation or
                IArgumentOperation or
                ISimpleAssignmentOperation or
                IExpressionStatementOperation or
                IVariableDeclarationGroupOperation or
                IVariableDeclarationOperation or
                IVariableDeclaratorOperation or
                IVariableInitializerOperation or
                IReturnOperation or
                IConditionalOperation or
                IDefaultValueOperation or
                IFlowCaptureOperation or
                IFlowCaptureReferenceOperation;
            if (!allowed || operation.Kind == OperationKind.Invalid)
            {
                throw new ExtractionException(
                    $"Operation '{operation.Kind}' in '{signature}' is outside the restricted subset at {Location(operation)}.");
            }

            switch (operation)
            {
                case IFieldReferenceOperation fieldReference
                    when !IsAllowedField(fieldReference.Field):
                    throw new ExtractionException(
                        $"Field '{Display(fieldReference.Field)}' in '{signature}' is not a local enum constant.");
                case ISimpleAssignmentOperation assignment
                    when assignment.IsRef ||
                         _profile is not ExtractionProfile.TransactionSettlement &&
                         assignment.Target is not ILocalReferenceOperation:
                    throw new ExtractionException(
                        $"Assignment in '{signature}' is not an implicit local initialization at {Location(operation)}.");
                case IInvocationOperation invocation:
                    ValidateCalledMethod(invocation.TargetMethod, signature);
                    break;
                case IObjectCreationOperation { Constructor: not null } creation:
                    ValidateCalledMethod(creation.Constructor, signature);
                    break;
                case IConversionOperation { OperatorMethod: not null } conversion:
                    ValidateCalledMethod(conversion.OperatorMethod, signature);
                    break;
                case IBinaryOperation { OperatorMethod: not null } binary:
                    ValidateCalledMethod(binary.OperatorMethod, signature);
                    break;
                case IUnaryOperation { OperatorMethod: not null } unary:
                    ValidateCalledMethod(unary.OperatorMethod, signature);
                    break;
            }

            foreach (IOperation child in operation.ChildOperations)
            {
                Validate(child, signature);
            }
        }

        private void ValidateCalledMethod(IMethodSymbol method, string caller)
        {
            if (IsAllowedProfileIntrinsic(method) || IsAllowedMathIntrinsic(method))
            {
                return;
            }

            if (SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, _compilation.Assembly))
            {
                if (method.DeclaringSyntaxReferences.Length != 1)
                {
                    throw new ExtractionException($"Local dependency '{Display(method)}' from '{caller}' is not uniquely declared.");
                }

                SyntaxNode declaration = method.DeclaringSyntaxReferences[0].GetSyntax();
                if (method.MethodKind == MethodKind.Constructor && declaration is StructDeclarationSyntax structDeclaration)
                {
                    ValidatePrimaryDataConstructor(method, structDeclaration);
                }
                else
                {
                    _pending.Enqueue(method);
                }
                return;
            }

            throw new ExtractionException(
                $"External call '{Display(method)}' from '{caller}' is not an allowed intrinsic.");
        }

        private bool IsAllowedField(IFieldSymbol field) =>
            field.HasConstantValue &&
            field.Type.TypeKind == TypeKind.Enum &&
            SymbolEqualityComparer.Default.Equals(field.ContainingAssembly, _compilation.Assembly) ||
            _profile is ExtractionProfile.SStorePricing &&
            field.ContainingType.ToDisplayString() is
                "Nethermind.Evm.GasPolicy.SStorePricingInput" or
                "Nethermind.Evm.GasPolicy.SStoreAccessPricingSchedule" or
                "Nethermind.Evm.GasPolicy.SStorePostAccessPricingSchedule" &&
            !field.IsStatic && field.IsReadOnly && field.DeclaredAccessibility == Accessibility.Public ||
            _profile is ExtractionProfile.AccountAccessPricing &&
            field.ContainingType.ToDisplayString() == "Nethermind.Evm.GasPolicy.AccountAccessPricingResult" &&
            !field.IsStatic && field.IsReadOnly && field.DeclaredAccessibility == Accessibility.Public ||
            _profile is ExtractionProfile.BlockGasInclusion &&
            field.ContainingType.ToDisplayString() == "Nethermind.Core.Eip7825Constants" &&
            field.Name == "DefaultTxGasLimitCap" &&
            field.IsStatic &&
            field.Type.SpecialType == SpecialType.System_UInt64 ||
            _profile is ExtractionProfile.PrecompileGasPricing &&
            field.ContainingType.SpecialType == SpecialType.System_UInt64 &&
            field.Name == "MaxValue" &&
            field.IsStatic &&
            field.Type.SpecialType == SpecialType.System_UInt64;

        private bool IsAllowedProfileIntrinsic(IMethodSymbol method) =>
            _profile is ExtractionProfile.BlockGasInclusion &&
                method.ContainingType.ToDisplayString() == "Nethermind.Core.Extensions.UInt64Extensions" &&
                method.Name == "SaturatingSub" &&
                method.IsStatic &&
                method.IsExtensionMethod &&
                method.ReturnType.SpecialType == SpecialType.System_UInt64 &&
                method.Parameters.Length == 2 &&
                method.Parameters.All(static parameter => parameter.Type.SpecialType == SpecialType.System_UInt64) ||
            _profile is ExtractionProfile.TransactionSettlement &&
                method.ContainingType.ToDisplayString() == "Nethermind.Evm.GasPolicy.Eip8037BlockGasInclusionCheck" &&
                method.Name == "CalculateBlockExecutionGas" &&
                method.IsStatic &&
                method.ReturnType.SpecialType == SpecialType.System_UInt64 &&
                method.Parameters.Length == 3 &&
                method.Parameters.All(static parameter => parameter.Type.SpecialType == SpecialType.System_UInt64) ||
            _profile is ExtractionProfile.StateGasTransitionAdapter &&
                method.ContainingType.ToDisplayString() == TransitionTypeMetadataName &&
                method.IsStatic &&
                method.DeclaredAccessibility == Accessibility.Public &&
                method.ReturnType.ToDisplayString() == "Nethermind.Evm.GasPolicy.StateGasTransitionResult" ||
            _profile is ExtractionProfile.StateGasTransitionAdapter &&
                method.MethodKind == MethodKind.Constructor &&
                method.ContainingType.ToDisplayString() == "Nethermind.Evm.GasPolicy.StateGasTransitionResult" &&
                method.Parameters.Length == 6 &&
                method.Parameters[0].Type.SpecialType == SpecialType.System_UInt64 &&
                method.Parameters.Skip(1).All(static parameter => parameter.Type.SpecialType == SpecialType.System_Int64) ||
            _profile is ExtractionProfile.BlockReceiptGasAccounting &&
                method.ContainingType.ToDisplayString() == "Nethermind.Evm.GasPolicy.EthereumGasPolicy" &&
                method.Name == "CombineBlockGas" &&
                method.IsStatic &&
                method.ReturnType.SpecialType == SpecialType.System_UInt64 &&
                method.Parameters.Length == 2 &&
                method.Parameters.All(static parameter => parameter.Type.SpecialType == SpecialType.System_UInt64);

        private static bool IsAllowedMathIntrinsic(IMethodSymbol method) =>
            method.ContainingType.ToDisplayString() == typeof(Math).FullName &&
            method.Parameters.Length == 2 &&
            method.Parameters.All(parameter => parameter.Type.SpecialType == method.ReturnType.SpecialType) &&
            ((method.Name == nameof(Math.Min) &&
              method.ReturnType.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64) ||
             (method.Name == nameof(Math.Max) &&
              method.ReturnType.SpecialType == SpecialType.System_UInt64));

        private void ValidatePrimaryDataConstructor(IMethodSymbol constructor, StructDeclarationSyntax declaration)
        {
            string typeName = Display(constructor.ContainingType);
            if (_dataTypes.ContainsKey(typeName))
            {
                return;
            }

            if (!constructor.ContainingType.IsReadOnly ||
                declaration.AttributeLists.Count != 0 ||
                declaration.ParameterList is null ||
                declaration.ParameterList.Parameters.Count != constructor.Parameters.Length ||
                declaration.BaseList is not null ||
                declaration.Members.Any(static member => member is not FieldDeclarationSyntax))
            {
                throw new ExtractionException(
                    $"Data constructor '{Display(constructor)}' must declare only a primary parameter list and readonly fields.");
            }

            List<IrDataField> fields = [];
            foreach (FieldDeclarationSyntax fieldDeclaration in declaration.Members.Cast<FieldDeclarationSyntax>())
            {
                if (!fieldDeclaration.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) ||
                    fieldDeclaration.Declaration.Variables.Count != 1)
                {
                    throw new ExtractionException($"Data type '{typeName}' contains a field outside the restricted subset.");
                }

                VariableDeclaratorSyntax variable = fieldDeclaration.Declaration.Variables[0];
                if (variable.Initializer?.Value is not IdentifierNameSyntax initializer)
                {
                    throw new ExtractionException(
                        $"Field '{variable.Identifier.ValueText}' in '{typeName}' must be initialized directly from a primary-constructor parameter.");
                }

                IFieldSymbol field = _semanticModel.GetDeclaredSymbol(variable) as IFieldSymbol
                    ?? throw new ExtractionException($"Roslyn did not bind field '{variable.Identifier.ValueText}' in '{typeName}'.");
                IParameterSymbol parameter = _semanticModel.GetSymbolInfo(initializer).Symbol as IParameterSymbol
                    ?? throw new ExtractionException($"Roslyn did not bind '{initializer}' to a constructor parameter in '{typeName}'.");
                if (!SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, constructor) ||
                    !SymbolEqualityComparer.Default.Equals(field.Type, parameter.Type))
                {
                    throw new ExtractionException(
                        $"Field '{field.Name}' in '{typeName}' is not a type-preserving projection of '{Display(constructor)}'.");
                }

                fields.Add(new IrDataField(field.Name, Display(field.Type), parameter.Ordinal));
            }

            IFieldSymbol[] instanceFields = constructor.ContainingType.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(static field => !field.IsStatic && !field.IsImplicitlyDeclared)
                .ToArray();
            if (fields.Count != constructor.Parameters.Length || fields.Count != instanceFields.Length ||
                fields.Select(static field => field.ParameterOrdinal).Distinct().Count() != constructor.Parameters.Length)
            {
                throw new ExtractionException(
                    $"Data constructor '{Display(constructor)}' must project every parameter to exactly one field.");
            }

            _dataTypes.Add(typeName, new IrDataType(typeName, Display(constructor), fields));
        }

        private static string Location(IOperation operation)
        {
            FileLinePositionSpan span = operation.Syntax.GetLocation().GetLineSpan();
            return $"{NormalizePath(span.Path)}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}";
        }
    }

    private static class SyntaxSubsetValidator
    {
        private static readonly HashSet<SyntaxKind> AllowedKinds =
        [
            SyntaxKind.MethodDeclaration,
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.ArrowExpressionClause,
            SyntaxKind.AttributeList,
            SyntaxKind.Attribute,
            SyntaxKind.AttributeArgumentList,
            SyntaxKind.AttributeArgument,
            SyntaxKind.PredefinedType,
            SyntaxKind.IdentifierName,
            SyntaxKind.ParameterList,
            SyntaxKind.Parameter,
            SyntaxKind.Block,
            SyntaxKind.IfStatement,
            SyntaxKind.ElseClause,
            SyntaxKind.ReturnStatement,
            SyntaxKind.LocalDeclarationStatement,
            SyntaxKind.VariableDeclaration,
            SyntaxKind.VariableDeclarator,
            SyntaxKind.EqualsValueClause,
            SyntaxKind.InvocationExpression,
            SyntaxKind.ArgumentList,
            SyntaxKind.Argument,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression,
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxKind.ParenthesizedExpression,
            SyntaxKind.CastExpression,
            SyntaxKind.ConditionalExpression,
            SyntaxKind.ExpressionStatement,
            SyntaxKind.CheckedExpression,
            SyntaxKind.UncheckedExpression,
            SyntaxKind.NumericLiteralExpression,
            SyntaxKind.TrueLiteralExpression,
            SyntaxKind.FalseLiteralExpression,
            SyntaxKind.NullLiteralExpression,
            SyntaxKind.DefaultLiteralExpression,
            SyntaxKind.DefaultExpression,
            SyntaxKind.UnaryMinusExpression,
            SyntaxKind.UnaryPlusExpression,
            SyntaxKind.LogicalNotExpression,
            SyntaxKind.BitwiseNotExpression,
            SyntaxKind.AddExpression,
            SyntaxKind.SubtractExpression,
            SyntaxKind.MultiplyExpression,
            SyntaxKind.DivideExpression,
            SyntaxKind.ModuloExpression,
            SyntaxKind.LeftShiftExpression,
            SyntaxKind.RightShiftExpression,
            SyntaxKind.UnsignedRightShiftExpression,
            SyntaxKind.BitwiseAndExpression,
            SyntaxKind.BitwiseOrExpression,
            SyntaxKind.ExclusiveOrExpression,
            SyntaxKind.LogicalAndExpression,
            SyntaxKind.LogicalOrExpression,
            SyntaxKind.EqualsExpression,
            SyntaxKind.NotEqualsExpression,
            SyntaxKind.LessThanExpression,
            SyntaxKind.LessThanOrEqualExpression,
            SyntaxKind.GreaterThanExpression,
            SyntaxKind.GreaterThanOrEqualExpression,
        ];

        public static void Validate(SyntaxNode declaration)
        {
            foreach (SyntaxNode node in declaration.DescendantNodesAndSelf())
            {
                if (!AllowedKinds.Contains(node.Kind()))
                {
                    FileLinePositionSpan span = node.GetLocation().GetLineSpan();
                    throw new ExtractionException(
                        $"Syntax '{node.Kind()}' is outside the restricted subset at " +
                        $"{NormalizePath(span.Path)}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}.");
                }
            }

            if (declaration is BaseMethodDeclarationSyntax method &&
                (method.Modifiers.Any(SyntaxKind.AsyncKeyword) ||
                 method.Modifiers.Any(SyntaxKind.UnsafeKeyword) ||
                 method.Modifiers.Any(SyntaxKind.ExternKeyword)))
            {
                throw new ExtractionException($"Method modifiers on '{declaration}' are outside the restricted subset.");
            }
        }
    }

    private sealed record IrDocument(
        int SchemaVersion,
        string Root,
        string RootSignature,
        IReadOnlyList<IrDataType> DataTypes,
        IReadOnlyList<IrMethod> Methods);

    private sealed record TransitionIrDocument(
        int SchemaVersion,
        string Kernel,
        IReadOnlyList<IrRoot> Roots,
        TransitionKernelModel Program);

    private sealed record StateGasTransitionAdapterIrDocument(
        int SchemaVersion,
        string Kernel,
        IReadOnlyList<IrRoot> Roots,
        StateGasTransitionAdapterKernelModel Program);

    private sealed record TransactionGasInitializationIrDocument(
        int SchemaVersion,
        string Kernel,
        IReadOnlyList<IrRoot> Roots,
        TransactionGasInitializationKernelModel Program);

    private sealed record TransactionSettlementIrDocument(
        int SchemaVersion,
        string Kernel,
        IReadOnlyList<IrRoot> Roots,
        IReadOnlyList<IrDataType> DataTypes,
        IReadOnlyList<IrMethod> Methods,
        TransactionSettlementKernelModel Program);

    private sealed record BlockGasInclusionIrDocument(
        int SchemaVersion,
        string Kernel,
        IReadOnlyList<IrRoot> Roots,
        BlockGasInclusionKernelModel Program);

    private sealed record BlockReceiptGasAccountingIrDocument(
        int SchemaVersion,
        string Kernel,
        IReadOnlyList<IrRoot> Roots,
        BlockReceiptGasAccountingKernelModel Program);

    private sealed record SStorePricingIrDocument(
        int SchemaVersion,
        string Kernel,
        IReadOnlyList<IrRoot> Roots,
        IReadOnlyList<IrDataType> DataTypes,
        IReadOnlyList<IrMethod> Methods,
        SStorePricingKernelModel Program,
        SStorePricingAdapterShape Adapter);

    private sealed record AccountAccessPricingIrDocument(
        int SchemaVersion,
        string Kernel,
        IReadOnlyList<IrRoot> Roots,
        IReadOnlyList<IrDataType> DataTypes,
        IReadOnlyList<IrMethod> Methods,
        AccountAccessPricingKernelModel Program,
        AccountAccessPricingAdapterShape Adapter);

    private sealed record ExtendedStackDecoderIrDocument(
        int SchemaVersion,
        string Kernel,
        IReadOnlyList<IrRoot> Roots,
        IReadOnlyList<IrDataType> DataTypes,
        IReadOnlyList<IrMethod> Methods,
        ExtendedStackDecoderKernelModel Program,
        ExtendedStackDecoderAdapterShape Adapter);

    private sealed record IdentityPrecompileIrDocument(
        int SchemaVersion,
        string Kernel,
        IReadOnlyList<IrRoot> Roots,
        IReadOnlyList<IrDataType> DataTypes,
        IReadOnlyList<IrMethod> Methods,
        IdentityPrecompileKernelModel Program,
        IdentityPrecompileAdapterShape Adapter);

    private sealed record PrecompileGasPricingIrDocument(
        int SchemaVersion,
        string Kernel,
        IReadOnlyList<IrRoot> Roots,
        IReadOnlyList<IrDataType> DataTypes,
        IReadOnlyList<IrMethod> Methods,
        PrecompileGasPricingKernelModel Program,
        PrecompileGasPricingAdapterShape Adapter);

    private sealed record IrRoot(string Name, string Signature);

    private sealed record ExtractedGraph(
        IReadOnlyList<IrMethod> Methods,
        IReadOnlyList<IrDataType> DataTypes,
        IReadOnlyList<IMethodSymbol> Symbols);

    private sealed record IrDataType(
        string Type,
        string Constructor,
        IReadOnlyList<IrDataField> Fields);

    private sealed record IrDataField(string Name, string Type, int ParameterOrdinal);

    private sealed record IrMethod(
        string Signature,
        string ReturnType,
        IReadOnlyList<IrParameter> Parameters,
        IReadOnlyList<IrBlock> Blocks);

    private sealed record IrParameter(int Ordinal, string Name, string Type);

    private sealed record IrBlock(
        int Ordinal,
        string Kind,
        string ConditionKind,
        IReadOnlyList<IrPredecessor> Predecessors,
        IReadOnlyList<IrOperation> Operations,
        IrOperation? BranchValue,
        IrBranch? FallThrough,
        IrBranch? Conditional);

    private sealed record IrPredecessor(int SourceOrdinal, string Semantics);

    private sealed record IrBranch(string Semantics, int? DestinationOrdinal);

    private sealed record IrOperation(
        string Kind,
        string? Type,
        string? Constant,
        bool IsImplicit,
        IReadOnlyList<IrProperty> Properties,
        IReadOnlyList<IrOperation> Children);

    private sealed record IrProperty(string Name, string Value);

    private sealed record SourceManifest(
        int SchemaVersion,
        string ExtractorVersion,
        string CompilerVersion,
        string LanguageVersion,
        string Root,
        string MethodSignature,
        IReadOnlyList<string> RootSignatures,
        SourceIdentity Source,
        ArtifactIdentity Artifact,
        ArtifactIdentity LeanArtifact,
        IReadOnlyList<string> ExtractedMethods,
        IReadOnlyList<SourceIdentity>? SupportingSources);

    private sealed record SourceIdentity(string Path, string Sha256);

    private sealed record ArtifactIdentity(string Path, string Sha256);

    private sealed record KernelExtraction(
        string CanonicalRoot,
        string SourcePath,
        string SourceRelativePath,
        string SourceHash,
        IReadOnlyList<SourceIdentity> SupportingSources,
        SemanticModel SemanticModel,
        IReadOnlyList<IMethodSymbol> Roots,
        ExtractedGraph Extracted);

    private delegate IReadOnlyList<IMethodSymbol> RootFinder(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree);

    private delegate byte[] LeanEmitter(string compilerVersion, string sourceHash, string irHash);

    private enum ExtractionProfile
    {
        Standard,
        BlockGasInclusion,
        TransactionSettlement,
        StateGasTransitionAdapter,
        BlockReceiptGasAccounting,
        SStorePricing,
        AccountAccessPricing,
        ExtendedStackDecoder,
        IdentityPrecompile,
        PrecompileGasPricing,
    }
}
