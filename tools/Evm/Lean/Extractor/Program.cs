// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.Extractor;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            ExtractorArguments arguments = ExtractorArguments.Parse(args);
            if (arguments.Profile == "transaction-settlement")
            {
                Print(StateGasChargeExtractor.ExtractTransactionSettlement(
                    arguments.RepoRoot,
                    arguments.OutputDirectory,
                    arguments.TransactionSettlementLeanOutputPath));
                return 0;
            }

            if (arguments.Profile == "state-gas-transition-adapter")
            {
                Print(StateGasChargeExtractor.ExtractStateGasTransitionAdapter(
                    arguments.RepoRoot,
                    arguments.OutputDirectory,
                    arguments.StateGasTransitionAdapterLeanOutputPath));
                return 0;
            }

            if (arguments.Profile == "block-receipt-gas-accounting")
            {
                Print(StateGasChargeExtractor.ExtractBlockReceiptGasAccounting(
                    arguments.RepoRoot,
                    arguments.OutputDirectory,
                    arguments.BlockReceiptGasAccountingLeanOutputPath));
                return 0;
            }

            if (arguments.Profile == "sstore-pricing")
            {
                Print(StateGasChargeExtractor.ExtractSStorePricing(
                    arguments.RepoRoot,
                    arguments.OutputDirectory,
                    arguments.SStorePricingLeanOutputPath));
                return 0;
            }

            if (arguments.Profile == "account-access-pricing")
            {
                Print(StateGasChargeExtractor.ExtractAccountAccessPricing(
                    arguments.RepoRoot,
                    arguments.OutputDirectory,
                    arguments.AccountAccessPricingLeanOutputPath));
                return 0;
            }

            if (arguments.Profile == "extended-stack-decoder")
            {
                Print(StateGasChargeExtractor.ExtractExtendedStackDecoder(
                    arguments.RepoRoot,
                    arguments.OutputDirectory,
                    arguments.ExtendedStackDecoderLeanOutputPath));
                return 0;
            }

            if (arguments.Profile == "identity-precompile")
            {
                Print(StateGasChargeExtractor.ExtractIdentityPrecompile(
                    arguments.RepoRoot,
                    arguments.OutputDirectory,
                    arguments.IdentityPrecompileLeanOutputPath));
                return 0;
            }

            if (arguments.Profile == "precompile-gas-pricing")
            {
                Print(StateGasChargeExtractor.ExtractPrecompileGasPricing(
                    arguments.RepoRoot,
                    arguments.OutputDirectory,
                    arguments.PrecompileGasPricingLeanOutputPath));
                return 0;
            }

            ExtractionResult charge = StateGasChargeExtractor.Extract(
                arguments.RepoRoot,
                arguments.OutputDirectory,
                arguments.LeanOutputPath);
            ExtractionResult transitions = StateGasChargeExtractor.ExtractTransitions(
                arguments.RepoRoot,
                arguments.OutputDirectory,
                arguments.TransitionLeanOutputPath);
            ExtractionResult transactionGasInitialization = StateGasChargeExtractor.ExtractTransactionGasInitialization(
                arguments.RepoRoot,
                arguments.OutputDirectory,
                arguments.TransactionGasInitializationLeanOutputPath);
            ExtractionResult blockGasInclusion = StateGasChargeExtractor.ExtractBlockGasInclusion(
                arguments.RepoRoot,
                arguments.OutputDirectory,
                arguments.BlockGasInclusionLeanOutputPath);
            Print(charge);
            Print(transitions);
            Print(transactionGasInitialization);
            Print(blockGasInclusion);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void Print(ExtractionResult result)
    {
        Console.WriteLine($"Extracted {result.MethodCount} method(s) from {result.SourcePath}.");
        Console.WriteLine($"IR: {result.IrPath}");
        Console.WriteLine($"Manifest: {result.ManifestPath}");
        Console.WriteLine($"Lean: {result.LeanPath}");
    }
}

internal sealed record ExtractorArguments(
    string RepoRoot,
    string OutputDirectory,
    string? LeanOutputPath,
    string? TransitionLeanOutputPath,
    string? StateGasTransitionAdapterLeanOutputPath,
    string? TransactionGasInitializationLeanOutputPath,
    string? TransactionSettlementLeanOutputPath,
    string? BlockGasInclusionLeanOutputPath,
    string? BlockReceiptGasAccountingLeanOutputPath,
    string? SStorePricingLeanOutputPath,
    string? AccountAccessPricingLeanOutputPath,
    string? ExtendedStackDecoderLeanOutputPath,
    string? IdentityPrecompileLeanOutputPath,
    string? PrecompileGasPricingLeanOutputPath,
    string? Profile)
{
    public static ExtractorArguments Parse(string[] args)
    {
        string? repoRoot = null;
        string? outputDirectory = null;
        string? leanOutputPath = null;
        string? transitionLeanOutputPath = null;
        string? stateGasTransitionAdapterLeanOutputPath = null;
        string? transactionGasInitializationLeanOutputPath = null;
        string? transactionSettlementLeanOutputPath = null;
        string? blockGasInclusionLeanOutputPath = null;
        string? blockReceiptGasAccountingLeanOutputPath = null;
        string? sStorePricingLeanOutputPath = null;
        string? accountAccessPricingLeanOutputPath = null;
        string? extendedStackDecoderLeanOutputPath = null;
        string? identityPrecompileLeanOutputPath = null;
        string? precompileGasPricingLeanOutputPath = null;
        string? profile = null;

        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for argument '{args[i]}'.");
            }

            string value = args[i + 1];
            switch (args[i])
            {
                case "--repo-root" when repoRoot is null:
                    repoRoot = value;
                    break;
                case "--output" when outputDirectory is null:
                    outputDirectory = value;
                    break;
                case "--lean-output" when leanOutputPath is null:
                    leanOutputPath = value;
                    break;
                case "--transition-lean-output" when transitionLeanOutputPath is null:
                    transitionLeanOutputPath = value;
                    break;
                case "--state-gas-transition-adapter-lean-output" when stateGasTransitionAdapterLeanOutputPath is null:
                    stateGasTransitionAdapterLeanOutputPath = value;
                    break;
                case "--transaction-gas-initialization-lean-output" when transactionGasInitializationLeanOutputPath is null:
                    transactionGasInitializationLeanOutputPath = value;
                    break;
                case "--transaction-settlement-lean-output" when transactionSettlementLeanOutputPath is null:
                    transactionSettlementLeanOutputPath = value;
                    break;
                case "--block-gas-inclusion-lean-output" when blockGasInclusionLeanOutputPath is null:
                    blockGasInclusionLeanOutputPath = value;
                    break;
                case "--block-receipt-gas-accounting-lean-output" when blockReceiptGasAccountingLeanOutputPath is null:
                    blockReceiptGasAccountingLeanOutputPath = value;
                    break;
                case "--sstore-pricing-lean-output" when sStorePricingLeanOutputPath is null:
                    sStorePricingLeanOutputPath = value;
                    break;
                case "--account-access-pricing-lean-output" when accountAccessPricingLeanOutputPath is null:
                    accountAccessPricingLeanOutputPath = value;
                    break;
                case "--extended-stack-decoder-lean-output" when extendedStackDecoderLeanOutputPath is null:
                    extendedStackDecoderLeanOutputPath = value;
                    break;
                case "--identity-precompile-lean-output" when identityPrecompileLeanOutputPath is null:
                    identityPrecompileLeanOutputPath = value;
                    break;
                case "--precompile-gas-pricing-lean-output" when precompileGasPricingLeanOutputPath is null:
                    precompileGasPricingLeanOutputPath = value;
                    break;
                case "--profile" when profile is null:
                    profile = value;
                    break;
                case "--repo-root":
                case "--output":
                case "--lean-output":
                case "--transition-lean-output":
                case "--state-gas-transition-adapter-lean-output":
                case "--transaction-gas-initialization-lean-output":
                case "--transaction-settlement-lean-output":
                case "--block-gas-inclusion-lean-output":
                case "--block-receipt-gas-accounting-lean-output":
                case "--sstore-pricing-lean-output":
                case "--account-access-pricing-lean-output":
                case "--extended-stack-decoder-lean-output":
                case "--identity-precompile-lean-output":
                case "--precompile-gas-pricing-lean-output":
                case "--profile":
                    throw new ArgumentException($"Argument '{args[i]}' was provided more than once.");
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            throw new ArgumentException("Missing required argument '--repo-root'.");
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Missing required argument '--output'.");
        }

        if (profile is not null &&
            profile is not ("transaction-settlement" or "state-gas-transition-adapter" or "block-receipt-gas-accounting" or
                "sstore-pricing" or "account-access-pricing" or "extended-stack-decoder" or "identity-precompile" or
                "precompile-gas-pricing"))
        {
            throw new ArgumentException($"Unknown extraction profile '{profile}'.");
        }

        return new ExtractorArguments(
            Path.GetFullPath(repoRoot),
            Path.GetFullPath(outputDirectory),
            leanOutputPath is null ? null : Path.GetFullPath(leanOutputPath),
            transitionLeanOutputPath is null ? null : Path.GetFullPath(transitionLeanOutputPath),
            stateGasTransitionAdapterLeanOutputPath is null
                ? null
                : Path.GetFullPath(stateGasTransitionAdapterLeanOutputPath),
            transactionGasInitializationLeanOutputPath is null
                ? null
                : Path.GetFullPath(transactionGasInitializationLeanOutputPath),
            transactionSettlementLeanOutputPath is null
                ? null
                : Path.GetFullPath(transactionSettlementLeanOutputPath),
            blockGasInclusionLeanOutputPath is null
                ? null
                : Path.GetFullPath(blockGasInclusionLeanOutputPath),
            blockReceiptGasAccountingLeanOutputPath is null
                ? null
                : Path.GetFullPath(blockReceiptGasAccountingLeanOutputPath),
            sStorePricingLeanOutputPath is null
                ? null
                : Path.GetFullPath(sStorePricingLeanOutputPath),
            accountAccessPricingLeanOutputPath is null
                ? null
                : Path.GetFullPath(accountAccessPricingLeanOutputPath),
            extendedStackDecoderLeanOutputPath is null
                ? null
                : Path.GetFullPath(extendedStackDecoderLeanOutputPath),
            identityPrecompileLeanOutputPath is null
                ? null
                : Path.GetFullPath(identityPrecompileLeanOutputPath),
            precompileGasPricingLeanOutputPath is null
                ? null
                : Path.GetFullPath(precompileGasPricingLeanOutputPath),
            profile);
    }
}
