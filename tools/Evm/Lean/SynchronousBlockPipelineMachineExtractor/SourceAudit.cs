// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.SynchronousBlockPipelineMachineExtractor;

internal sealed class ExtractionException(string message) : Exception(message);

/// <summary>
/// Audits the selected production source route without assigning it operational semantics.
/// </summary>
internal static class SourceAudit
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    internal static AuditResult Run(string repositoryRoot)
    {
        string root = Path.GetFullPath(repositoryRoot);
        AuditProfile profile = ReadProfile(root);
        ValidateProfile(profile);
        MetadataMirror mirror = ReadMirror(root);
        ValidateRouteMetadata(mirror);
        ValidateConfigurationPins(root, profile);
        ValidateCompositionFiles(root, profile.CompositionPins);
        ValidateSpecificationFiles(root, profile.SpecificationPins);
        ValidateLeanMirror(root, mirror);

        Dictionary<string, SourceFile> sources = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        string rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        foreach (SourcePin pin in profile.Pins)
        {
            string fullPath = Path.GetFullPath(Path.Combine(root, pin.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !paths.Add(fullPath))
                throw new ExtractionException($"Duplicate or out-of-root audit source: {pin.Path}.");
            if (pin.Kind != "csharp" || pin.CompositionAdmitted || pin.Sha256.Length != 64)
                throw new ExtractionException($"Audit source is not a non-admitted SHA-256 pin: {pin.Path}.");

            byte[] bytes = File.ReadAllBytes(fullPath);
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ExtractionException($"Audit source pin changed: {pin.Path}.");
            sources.Add(pin.Path, Parse(pin.Path, fullPath, bytes));
        }

        ValidateDiRoute(sources);
        ValidateSpecRoute(sources);
        ValidateSymbols(sources);
        ValidateHeaderProjection(sources);
        ValidateOrder(sources);

        return new(
            ProductionRoute.RouteName,
            sources.Count,
            ProductionRoute.SourceSymbols.Length,
            CountSourceAnchorsAndEdges(),
            ProductionRoute.HookDependencies.Length,
            ProductionRoute.PhasePlan.Length,
            [.. ProductionRoute.OpenBoundaries],
            ExtractionClosed: true);
    }

    internal static void RefuseExtraction(string repositoryRoot, string outputDirectory)
    {
        _ = outputDirectory;
        _ = Run(repositoryRoot);
        throw new ExtractionException(
            "Operational extraction is intentionally disabled: the source route is an audit scaffold, not an admitted semantic kernel.");
    }

    private static AuditProfile ReadProfile(string root)
    {
        byte[] embeddedBytes = ReadEmbeddedResourceBytes(
            "Nethermind.Evm.Lean.SynchronousBlockPipelineMachineExtractor.SOURCE_PINS.json",
            "Missing embedded audit source pins.");
        string path = Path.Combine(root, "tools", "Evm", "Lean", "SynchronousBlockPipelineMachineExtractor",
            "SOURCE_PINS.json");
        if (!File.Exists(path))
            throw new ExtractionException("Missing checked audit source pins.");
        byte[] checkedBytes = File.ReadAllBytes(path);
        if (!checkedBytes.AsSpan().SequenceEqual(embeddedBytes))
            throw new ExtractionException("Checked audit source pins differ from the embedded resource.");
        return JsonSerializer.Deserialize<AuditProfile>(checkedBytes, JsonOptions)
            ?? throw new ExtractionException("Empty audit source pins.");
    }

    private static MetadataMirror ReadMirror(string root)
    {
        byte[] embeddedBytes = ReadEmbeddedResourceBytes(
            "Nethermind.Evm.Lean.SynchronousBlockPipelineMachineExtractor.METADATA_MIRROR.json",
            "Missing embedded route metadata mirror.");
        string path = Path.Combine(root, "tools", "Evm", "Lean", "SynchronousBlockPipelineMachineExtractor",
            "METADATA_MIRROR.json");
        if (!File.Exists(path))
            throw new ExtractionException("Missing checked route metadata mirror.");
        byte[] checkedBytes = File.ReadAllBytes(path);
        if (!checkedBytes.AsSpan().SequenceEqual(embeddedBytes))
            throw new ExtractionException("Checked route metadata mirror differs from the embedded resource.");
        return JsonSerializer.Deserialize<MetadataMirror>(checkedBytes, JsonOptions)
            ?? throw new ExtractionException("Empty route metadata mirror.");
    }

    private static byte[] ReadEmbeddedResourceBytes(string resourceName, string missingMessage)
    {
        using Stream stream = typeof(SourceAudit).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new ExtractionException(missingMessage);
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void ValidateLeanMirror(string root, MetadataMirror mirror)
    {
        string path = Path.Combine(root, "tools", "Evm", "Lean", "SynchronousBlockPipelineMachineExtractor",
            "Specification", "MetadataMirror.lean");
        if (!File.Exists(path))
            throw new ExtractionException("Missing checked Lean metadata mirror.");

        byte[] jsonBytes = ReadEmbeddedResourceBytes(
            "Nethermind.Evm.Lean.SynchronousBlockPipelineMachineExtractor.METADATA_MIRROR.json",
            "Missing embedded route metadata mirror.");

        string expectedHash = Convert.ToHexString(SHA256.HashData(jsonBytes)).ToLowerInvariant();
        string lean = File.ReadAllText(path, Encoding.UTF8);
        Match hash = Regex.Match(lean,
            "(?m)^\\s*def\\s+metadataMirrorSha256\\s*:\\s*String\\s*:=\\s*\\\"(?<hash>[0-9a-fA-F]{64})\\\"\\s*$");
        if (!hash.Success || !string.Equals(hash.Groups["hash"].Value, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException("Lean metadata mirror hash does not identify the embedded JSON mirror.");

        RequireLeanList(lean, "phaseKeys", mirror.PhaseLabels);
        RequireLeanList(lean, "hookKeys", mirror.Hooks.Select(MirrorHookKey).ToArray());
    }

    private static void RequireLeanList(string source, string definition, IReadOnlyList<string> expected)
    {
        Match match = Regex.Match(source,
            $@"def\s+{Regex.Escape(definition)}\s*:\s*List\s+String\s*:=\s*\[(?<body>.*?)\]\s*(?=\r?\n\s*(?:def|end)\b)",
            RegexOptions.Singleline);
        if (!match.Success)
            throw new ExtractionException($"Missing Lean metadata list {definition}.");
        string[] actual = Regex.Matches(match.Groups["body"].Value, "\\\"(?<value>(?:[^\\\"\\\\]|\\\\.)*)\\\"")
            .Select(item => item.Groups["value"].Value)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new ExtractionException($"Lean metadata list {definition} does not match the JSON mirror.");
    }

    private static void ValidateProfile(AuditProfile profile)
    {
        if (profile.SchemaVersion != 1 || profile.Status != "audit-scaffold-unchecked"
            || profile.Pins.Length != ProductionRoute.AuditedSourcePaths.Length
            || profile.ConfigurationPins.Length != ProductionRoute.ConfigurationRequirements.Length
            || profile.SpecificationPins.Length != ProductionRoute.SpecificationRequirements.Length)
            throw new ExtractionException("Unsupported audit profile.");
        if (!profile.Pins.Select(pin => pin.Path).ToHashSet(StringComparer.Ordinal)
                .SetEquals(ProductionRoute.AuditedSourcePaths))
            throw new ExtractionException("The audit profile does not cover the exact source roster.");
        if (profile.Boundary.GeneratedKernelPresent)
            throw new ExtractionException("An audit profile cannot declare a generated kernel.");
        if (!string.Equals(profile.Boundary.Processor, "Nethermind.Consensus.Processing.BlockchainProcessor.Process", StringComparison.Ordinal)
            || !string.Equals(profile.Boundary.Implementation, "Nethermind.Consensus.Processing.BlockProcessor.ProcessOne", StringComparison.Ordinal)
            || !string.Equals(profile.Boundary.BranchProcessor, "Nethermind.Consensus.Processing.BranchProcessor.Process", StringComparison.Ordinal)
            || !string.Equals(profile.Boundary.BlockchainProcessor, "Nethermind.Consensus.Processing.BlockchainProcessor.Process", StringComparison.Ordinal)
            || !string.Equals(profile.Boundary.SpecProvider, "Nethermind.Specs.ChainSpecStyle.ChainSpecBasedSpecProvider", StringComparison.Ordinal)
            || !string.Equals(profile.Boundary.SpecDerivation, "ApiBuilder.LoadChainSpec -> ChainSpecFileLoader.LoadEmbeddedOrFromFile -> AutoDetectingChainSpecLoader -> ChainSpecLoader -> ChainSpecBasedSpecProvider", StringComparison.Ordinal)
            || !string.Equals(profile.Boundary.MainnetConfigPath, "src/Nethermind/Nethermind.Runner/configs/mainnet.json", StringComparison.Ordinal)
            || !string.Equals(profile.Boundary.MainnetChainSpecPath, "src/Nethermind/Chains/foundation.json", StringComparison.Ordinal)
            || !string.Equals(profile.Boundary.Fork, "Nethermind.Specs.Forks.Amsterdam", StringComparison.Ordinal)
            || profile.Boundary.ForkActivationConfigured)
            throw new ExtractionException("Audit profile does not identify the standard synchronous route.");
    }

    private static void ValidateRouteMetadata(MetadataMirror mirror)
    {
        if (ProductionRoute.AuditedSourcePaths.Length != 50
            || ProductionRoute.AuditedSourcePaths.Distinct(StringComparer.Ordinal).Count() != 50
            || ProductionRoute.SourceSymbols.Length != 80
            || ProductionRoute.SourceSymbols.Select(symbol =>
                    (symbol.Path, symbol.Namespace, symbol.Type, symbol.Member, symbol.ParameterCount))
                .Distinct().Count() != 80
            || CountSourceAnchorsAndEdges() != 182)
            throw new ExtractionException("The exact source, symbol, and anchor/edge inventories changed.");

        if (ProductionRoute.PhasePlan.Length != 17
            || ProductionRoute.PhasePlan.Select(contract => contract.Phase).Distinct().Count() != Enum.GetValues<PipelinePhase>().Length
            || ProductionRoute.PhasePlan.Select(contract => contract.Label).Distinct(StringComparer.Ordinal).Count() != ProductionRoute.PhasePlan.Length)
            throw new ExtractionException("The static route plan must contain each of the 17 phases exactly once.");

        HookDependency[] hooks = ProductionRoute.HookDependencies;
        if (hooks.Length != 57
            || hooks.Select(contract => contract.Hook).Distinct().Count() != Enum.GetValues<HookId>().Length
            || hooks.Select(contract => contract.Hook).Distinct().Count() != hooks.Length)
            throw new ExtractionException("The revised hook catalog must cover exactly 57 unique hook IDs.");

        HookId[] plannedHookOccurrences = ProductionRoute.PhasePlan
            .SelectMany(contract => contract.Hooks)
            .ToArray();
        HashSet<HookId> plannedHooks = plannedHookOccurrences.ToHashSet();
        if (plannedHookOccurrences.Length != hooks.Length
            || plannedHooks.Count != Enum.GetValues<HookId>().Length
            || hooks.Any(contract => !plannedHooks.Contains(contract.Hook)
                || ProductionRoute.PhasePlan.Single(item => item.Phase == contract.Phase).Hooks.Count(hook => hook == contract.Hook) != 1))
            throw new ExtractionException("Phase membership and hook dependency phases are not a one-to-one mirror.");

        MirrorHook[] expectedMirrorHooks = hooks
            .Select(contract => new MirrorHook(
                contract.Hook.ToString(),
                ProductionRoute.PhasePlan.Single(item => item.Phase == contract.Phase).Label,
                contract.ProductionPath,
                contract.ProductionSymbol,
                contract.InputShape,
                contract.OutputShape,
                contract.FailureDisposition.ToString(),
                contract.MayMutateLogicalState,
                contract.ReceiptDependent,
                contract.RequiresScope))
            .ToArray();
        if (mirror.SchemaVersion != 1 || mirror.Route != ProductionRoute.RouteName
            || mirror.PhaseLabels.Length != ProductionRoute.PhasePlan.Length
            || !mirror.PhaseLabels.SequenceEqual(ProductionRoute.PhasePlan.Select(contract => contract.Label), StringComparer.Ordinal)
            || mirror.Hooks.Length != hooks.Length
            || !mirror.Hooks.SequenceEqual(expectedMirrorHooks))
            throw new ExtractionException("The JSON route mirror does not exactly match the C# phase/hook catalog.");

        OrderEdge[] orderEdges =
        [
            .. ProductionRoute.ProcessBlockOrderEdges,
            .. ProductionRoute.SynchronousReceiptOrderEdges,
            .. ProductionRoute.BackgroundReceiptOrderEdges,
            .. ProductionRoute.BranchOrderEdges,
            .. ProductionRoute.ChainOrderEdges,
            .. ProductionRoute.PreparationOrderEdges,
            .. ProductionRoute.RecoveryOrderEdges,
            .. ProductionRoute.TransactionFoldOrderEdges,
            .. ProductionRoute.AdapterOrderEdges,
            .. ProductionRoute.TransactionReceiptOrderEdges,
        ];
        if (orderEdges.Length != 66
            || orderEdges.Select(edge => edge.Id).Distinct(StringComparer.Ordinal).Count() != orderEdges.Length
            || orderEdges.Any(edge => !ProductionRoute.AuditedSourcePaths.Contains(edge.Path, StringComparer.Ordinal)))
            throw new ExtractionException("The source-order edge catalog must contain 66 unique in-roster edges.");
    }

    private static int CountSourceAnchorsAndEdges() =>
        ProductionRoute.ProcessOneAnchors.Length
        + ProductionRoute.ProcessBlockAnchors.Length
        + ProductionRoute.BranchAnchors.Length
        + ProductionRoute.ProcessBranchAnchors.Length
        + ProductionRoute.ValidateProcessedAnchors.Length
        + ProductionRoute.ChainAnchors.Length
        + ProductionRoute.PreparationAnchors.Length
        + ProductionRoute.TransactionAnchors.Length
        + ProductionRoute.SystemTransactionAnchors.Length
        + ProductionRoute.ReceiptAnchors.Length
        + ProductionRoute.SynchronousReceiptAnchors.Length
        + ProductionRoute.BackgroundReceiptAnchors.Length
        + ProductionRoute.TransactionGasMarkers.Length
        + ProductionRoute.ReceiptGasMarkers.Length
        + ProductionRoute.AdapterAnchors.Length
        + ProductionRoute.AdapterRegistrationAnchors.Length
        + ProductionRoute.ProcessBlockOrderEdges.Length
        + ProductionRoute.SynchronousReceiptOrderEdges.Length
        + ProductionRoute.BackgroundReceiptOrderEdges.Length
        + ProductionRoute.BranchOrderEdges.Length
        + ProductionRoute.ChainOrderEdges.Length
        + ProductionRoute.PreparationOrderEdges.Length
        + ProductionRoute.RecoveryOrderEdges.Length
        + ProductionRoute.TransactionFoldOrderEdges.Length
        + ProductionRoute.AdapterOrderEdges.Length
        + ProductionRoute.TransactionReceiptOrderEdges.Length;

    private static string MirrorHookKey(MirrorHook hook) => string.Join("|",
        new[]
        {
            hook.Name,
            hook.Phase,
            hook.ProductionPath,
            hook.ProductionSymbol,
            hook.InputShape,
            hook.OutputShape,
            hook.FailureDisposition,
            hook.MayMutateLogicalState ? "true" : "false",
            hook.ReceiptDependent ? "true" : "false",
            hook.RequiresScope ? "true" : "false",
        });

    private static void ValidateConfigurationPins(string root, AuditProfile profile)
    {
        FileRequirement[] requirements = ProductionRoute.ConfigurationRequirements;
        if (profile.ConfigurationPins.Length != requirements.Length
            || requirements.Any(requirement => profile.ConfigurationPins.Count(pin =>
                pin.Path == requirement.Path && pin.Kind == requirement.Kind) != 1)
            || profile.ConfigurationPins.Any(pin => !requirements.Any(requirement =>
                pin.Path == requirement.Path && pin.Kind == requirement.Kind)))
            throw new ExtractionException("Configuration pins do not cover the exact path/kind roster.");

        string rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ConfigurationPin pin in profile.ConfigurationPins)
        {
            string fullPath = Path.GetFullPath(Path.Combine(root, pin.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !paths.Add(fullPath))
                throw new ExtractionException($"Duplicate or out-of-root configuration pin: {pin.Path}.");
            if (pin.Sha256.Length != 64 || !File.Exists(fullPath))
                throw new ExtractionException($"Missing or malformed configuration pin: {pin.Path}.");
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant();
            if (!string.Equals(actual, pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ExtractionException($"Configuration pin changed: {pin.Path}.");
        }

        ConfigurationPin mainnetConfig = profile.ConfigurationPins.SingleOrDefault(pin =>
            string.Equals(pin.Path, profile.Boundary.MainnetConfigPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new ExtractionException("The configured mainnet JSON is not pinned.");
        ConfigurationPin chainSpec = profile.ConfigurationPins.SingleOrDefault(pin =>
            string.Equals(pin.Path, profile.Boundary.MainnetChainSpecPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new ExtractionException("The configured mainnet chain spec is not pinned.");
        ConfigurationPin configProject = profile.ConfigurationPins.Single(pin =>
            pin.Path == "src/Nethermind/Nethermind.Config/Nethermind.Config.csproj");

        using JsonDocument config = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, mainnetConfig.Path.Replace('/', Path.DirectorySeparatorChar))));
        if (!config.RootElement.TryGetProperty("Init", out JsonElement init)
            || !init.TryGetProperty("ChainSpecPath", out JsonElement chainSpecPath)
            || chainSpecPath.GetString() != ProductionRoute.MainnetConfigChainSpecPath)
            throw new ExtractionException("The pinned mainnet config does not select chainspec/foundation.json.");

        using JsonDocument chainSpecDocument = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(root, chainSpec.Path.Replace('/', Path.DirectorySeparatorChar))));
        if (!chainSpecDocument.RootElement.TryGetProperty("engine", out _)
            || chainSpecDocument.RootElement.TryGetProperty("config", out _)
            || !chainSpecDocument.RootElement.TryGetProperty("params", out JsonElement parameters)
            || ProductionRoute.TargetForkTransitionProperties.Any(property =>
                parameters.TryGetProperty(property, out _)))
            throw new ExtractionException(
                "The pinned mainnet chain spec is not the expected pre-Amsterdam Parity-format foundation config.");

        string normalizedProject = Normalize(File.ReadAllText(
            Path.Combine(root, configProject.Path.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8));
        foreach (string anchor in ProductionRoute.ChainSpecEmbeddingAnchors)
            RequireContains(normalizedProject, Normalize(anchor), configProject.Path, "embedded foundation chain-spec mapping");
        XDocument project = XDocument.Load(
            Path.Combine(root, configProject.Path.Replace('/', Path.DirectorySeparatorChar)));
        XElement[] embeddedChainSpecs = project.Descendants().Where(element =>
                element.Name.LocalName == "EmbeddedResource"
                && (string?)element.Attribute("Include") == @"..\Chains\**\*.*")
            .ToArray();
        if (embeddedChainSpecs.Length != 1
            || embeddedChainSpecs[0].Elements().Count(element => element.Name.LocalName == "Link") != 1
            || embeddedChainSpecs[0].Elements().Single(element => element.Name.LocalName == "Link").Value !=
                @"chainspec\%(RecursiveDir)%(Filename)%(Extension)"
            || embeddedChainSpecs[0].Parent is not XElement itemGroup
            || itemGroup.Name.LocalName != "ItemGroup"
            || Normalize((string?)itemGroup.Attribute("Condition") ?? "") != "'$(EnableZkEvm)'!='true'")
            throw new ExtractionException("The embedded chain-spec resource mapping is not structurally bound.");
    }

    private static void ValidateSpecificationFiles(string root, IReadOnlyList<SpecificationPin> pins)
    {
        FileRequirement[] requirements = ProductionRoute.SpecificationRequirements;
        if (pins.Count != requirements.Length
            || requirements.Any(requirement => pins.Count(pin =>
                pin.Path == requirement.Path && pin.Kind == requirement.Kind) != 1)
            || pins.Any(pin => !requirements.Any(requirement =>
                pin.Path == requirement.Path && pin.Kind == requirement.Kind)))
            throw new ExtractionException("Lean specification pins do not cover the exact handwritten roster.");

        string rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        string packageRoot = Path.Combine(root, "tools", "Evm", "Lean", "SynchronousBlockPipelineMachineExtractor");
        string specificationRoot = Path.Combine(packageRoot, "Specification");
        if (Directory.EnumerateDirectories(packageRoot, "*", SearchOption.AllDirectories).Any(path =>
                !IsIgnoredToolOutput(packageRoot, path)
                && string.Equals(Path.GetFileName(path), "Generated", StringComparison.OrdinalIgnoreCase)))
            throw new ExtractionException("Generated artifacts are forbidden in the audit scaffold.");
        string[] actualLeanPaths = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".lean", StringComparison.OrdinalIgnoreCase)
                && !IsIgnoredToolOutput(packageRoot, path))
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedLeanPaths = requirements.Select(requirement => requirement.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actualLeanPaths.SequenceEqual(expectedLeanPaths, StringComparer.Ordinal))
            throw new ExtractionException("The handwritten Lean specification file roster changed.");

        foreach (SpecificationPin pin in pins)
        {
            string fullPath = Path.GetFullPath(Path.Combine(root, pin.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || pin.Sha256.Length != 64 || !File.Exists(fullPath))
                throw new ExtractionException($"Missing or malformed Lean specification pin: {pin.Path}.");
            byte[] bytes = File.ReadAllBytes(fullPath);
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ExtractionException($"Lean specification byte identity changed: {pin.Path}.");

            string source = Encoding.UTF8.GetString(bytes);
            if (Regex.IsMatch(source,
                    @"(?m)^\s*(?:@\[[^\]\r\n]*\]\s*)*(?:(?:private|protected|noncomputable|unsafe)\s+)*(?:theorem|lemma|example|axiom|opaque|instance)\b|\b(?:sorry|admit)\b|:=\s*(?:by|rfl)\b"))
                throw new ExtractionException($"Operational proof content is forbidden in the audit scaffold: {pin.Path}.");
        }

        string state = File.ReadAllText(Path.Combine(specificationRoot, "BlockPipelineState.lean"), Encoding.UTF8);
        ValidateTypedLeanState(state);
        string plan = File.ReadAllText(Path.Combine(specificationRoot, "BlockPipelinePlan.lean"), Encoding.UTF8);
        ValidateTypedLeanPlan(plan);
    }

    private static void ValidateTypedLeanState(string source)
    {
        RequireLeanCases(source, "Phase", Enum.GetValues<PipelinePhase>().Select(LeanName).ToArray());
        RequireLeanCases(source, "HookId", Enum.GetValues<HookId>().Select(LeanName).ToArray());
        RequireLeanCases(source, "Outcome", Enum.GetValues<LogicalOutcome>().Select(LeanName).ToArray());
        RequireLeanCases(source, "FailureDisposition", Enum.GetValues<FailureDisposition>().Select(LeanName).ToArray());
        RequireLeanCases(source, "ReceiptArtifactMode", Enum.GetValues<ReceiptArtifactMode>().Select(LeanName).ToArray());
        RequireLeanCases(source, "ScopeOperation", Enum.GetValues<ScopeOperation>().Select(LeanName).ToArray());
        RequireLeanCases(source, "InvalidBlockReason",
            ["transactionFailure", "blockGasLimit", "processedHeader", "receiptRoot", "stateRoot", "blockAccessList"]);
        RequireLeanCases(source, "ChainPipelineResult", ["preBranch", "branched"]);
        RequireContains(Normalize(source), "inclusionListSatisfied:OptionBool", "BlockPipelineState.lean", "optional inclusion-list signal");
    }

    private static void ValidateTypedLeanPlan(string source)
    {
        string phaseBody = LeanDefinitionBody(source, "phasePlan", "synchronousReceiptPlan");
        Match[] phaseEntries = Regex.Matches(phaseBody,
                @"\.mk\s+\.(?<phase>[A-Za-z][A-Za-z0-9]*)\s+\(Phase\.label\s+\.(?<labelPhase>[A-Za-z][A-Za-z0-9]*)\)\s*\[(?<hooks>.*?)\]",
                RegexOptions.Singleline)
            .Cast<Match>()
            .ToArray();
        if (phaseEntries.Length != ProductionRoute.PhasePlan.Length
            || Regex.Matches(phaseBody, @"\.mk\b").Count != phaseEntries.Length)
            throw new ExtractionException("Typed Lean phase plan does not contain the exact 17-phase roster.");
        for (int index = 0; index < phaseEntries.Length; index++)
        {
            PhaseContract expected = ProductionRoute.PhasePlan[index];
            Match actual = phaseEntries[index];
            string expectedPhase = LeanName(expected.Phase);
            string[] actualHooks = Regex.Matches(actual.Groups["hooks"].Value, @"\.(?<hook>[a-z][A-Za-z0-9]*)")
                .Select(match => match.Groups["hook"].Value)
                .ToArray();
            if (actual.Groups["phase"].Value != expectedPhase
                || actual.Groups["labelPhase"].Value != expectedPhase
                || !actualHooks.SequenceEqual(expected.Hooks.Select(LeanName), StringComparer.Ordinal))
                throw new ExtractionException($"Typed Lean phase membership changed at index {index}.");
        }

        string dependencyBody = LeanDefinitionBody(source, "allHookDependencies", "ordinaryTransactionBoundary");
        Match[] dependencyEntries = Regex.Matches(dependencyBody,
                @"\.mk\s+\.(?<hook>[A-Za-z][A-Za-z0-9]*)\s+\.(?<phase>[A-Za-z][A-Za-z0-9]*)\s+" +
                "\"(?<path>[^\"]*)\"\\s+\"(?<symbol>[^\"]*)\"\\s+" +
                "\"(?<input>[^\"]*)\"\\s+\"(?<output>[^\"]*)\"\\s+" +
                @"\.(?<failure>[A-Za-z][A-Za-z0-9]*)\s+(?<mutates>true|false)\s+(?<receipt>true|false)\s+(?<scope>true|false)")
            .Cast<Match>()
            .ToArray();
        if (dependencyEntries.Length != ProductionRoute.HookDependencies.Length
            || Regex.Matches(dependencyBody, @"\.mk\b").Count != dependencyEntries.Length)
            throw new ExtractionException("Typed Lean hook dependency roster is incomplete.");
        for (int index = 0; index < dependencyEntries.Length; index++)
        {
            HookDependency expected = ProductionRoute.HookDependencies[index];
            Match actual = dependencyEntries[index];
            if (actual.Groups["hook"].Value != LeanName(expected.Hook)
                || actual.Groups["phase"].Value != LeanName(expected.Phase)
                || actual.Groups["path"].Value != expected.ProductionPath
                || actual.Groups["symbol"].Value != expected.ProductionSymbol
                || actual.Groups["input"].Value != expected.InputShape
                || actual.Groups["output"].Value != expected.OutputShape
                || actual.Groups["failure"].Value != LeanName(expected.FailureDisposition)
                || bool.Parse(actual.Groups["mutates"].Value) != expected.MayMutateLogicalState
                || bool.Parse(actual.Groups["receipt"].Value) != expected.ReceiptDependent
                || bool.Parse(actual.Groups["scope"].Value) != expected.RequiresScope)
                throw new ExtractionException($"Typed Lean hook contract changed at index {index}.");
        }
    }

    private static void RequireLeanCases(string source, string inductive, IReadOnlyList<string> expected)
    {
        Match definition = Regex.Match(source,
            $@"inductive\s+{Regex.Escape(inductive)}\s+where(?<body>.*?)(?=\r?\n\s*deriving\b)",
            RegexOptions.Singleline);
        string[] actual = definition.Success
            ? Regex.Matches(definition.Groups["body"].Value, @"(?m)^\s*\|\s*(?<case>[A-Za-z][A-Za-z0-9]*)")
                .Select(match => match.Groups["case"].Value)
                .ToArray()
            : [];
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new ExtractionException($"Typed Lean {inductive} cases do not match the C# boundary.");
    }

    private static string LeanDefinitionBody(string source, string definition, string nextDefinition)
    {
        Match match = Regex.Match(source,
            $@"def\s+{Regex.Escape(definition)}\b.*?:=\s*(?<body>.*?)(?=\r?\n\s*def\s+{Regex.Escape(nextDefinition)}\b)",
            RegexOptions.Singleline);
        if (!match.Success)
            throw new ExtractionException($"Missing typed Lean definition {definition}.");
        return match.Groups["body"].Value;
    }

    private static string LeanName<T>(T value) where T : struct, Enum
    {
        string name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static void ValidateCompositionFiles(string root, IReadOnlyList<CompositionPin> pins)
    {
        CompositionRequirement[] requirements = ProductionRoute.CompositionRequirements;
        if (pins.Count != requirements.Length
            || requirements.Any(requirement => pins.Count(pin =>
                pin.Artifact == requirement.Artifact && pin.Path == requirement.Path) != 1)
            || pins.Any(pin => !requirements.Any(requirement =>
                pin.Artifact == requirement.Artifact && pin.Path == requirement.Path)))
            throw new ExtractionException("Composition byte pins do not cover the exact dependency roster.");

        HashSet<string> artifacts = requirements.Select(requirement => requirement.Artifact).ToHashSet(StringComparer.Ordinal);
        if (!pins.Select(pin => pin.Artifact).ToHashSet(StringComparer.Ordinal).SetEquals(artifacts))
            throw new ExtractionException("Composition byte pins must name both accepted composition artifacts.");
        HashSet<string> identities = new(StringComparer.OrdinalIgnoreCase);
        string rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        foreach (CompositionPin pin in pins)
        {
            if (!artifacts.Contains(pin.Artifact) || !pin.AcceptedByteIdentity || pin.Sha256.Length != 64
                || !identities.Add($"{pin.Artifact}:{pin.Path}"))
                throw new ExtractionException($"Invalid or duplicate composition byte identity: {pin.Artifact}/{pin.Path}.");
            string fullPath = Path.GetFullPath(Path.Combine(root, pin.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
                throw new ExtractionException($"Missing composition input {pin.Path}.");
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant();
            if (!string.Equals(actual, pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ExtractionException($"Composition byte identity changed: {pin.Artifact}/{pin.Path}.");
        }
    }

    private static SourceFile Parse(string relativePath, string fullPath, byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, relativePath);
        foreach (Diagnostic diagnostic in tree.GetDiagnostics())
            if (diagnostic.Severity == DiagnosticSeverity.Error)
                throw new ExtractionException($"C# parse error in {relativePath}: {diagnostic}");
        return new(relativePath, fullPath, text, tree.GetCompilationUnitRoot());
    }

    private static void ValidateDiRoute(IReadOnlyDictionary<string, SourceFile> sources)
    {
        SourceFile module = Get(sources, "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs");
        string normalizedModule = Normalize(module.Text);
        foreach (string registration in ProductionRoute.RequiredRegistrations)
            RequireContains(normalizedModule, Normalize(registration), module.RelativePath, "DI registration");
        string blockProcessingLoad = FindMethodText(module, "BlockProcessingModule", "Load", 1);
        foreach (string registration in ProductionRoute.RequiredBlockProcessingRegistrations)
            RequireContains(blockProcessingLoad, Normalize(registration), module.RelativePath,
                "BlockProcessingModule.Load registration");
        string standardValidationLoad = FindMethodText(module, "StandardBlockValidationModule", "Load", 1);
        foreach (string registration in ProductionRoute.RequiredStandardValidationRegistrations)
            RequireContains(standardValidationLoad, Normalize(registration), module.RelativePath,
                "StandardBlockValidationModule.Load registration");

        SourceFile rootModule = Get(sources, "src/Nethermind/Nethermind.Init/Modules/NethermindModule.cs");
        string normalizedRootModule = Normalize(rootModule.Text);
        foreach (string registration in ProductionRoute.RequiredRootRegistrations)
            RequireContains(normalizedRootModule, Normalize(registration), rootModule.RelativePath, "root DI registration");
        string rootLoad = FindMethodText(rootModule, "NethermindModule", "Load", 1);
        RequireContainsAll(rootLoad,
            [ProductionRoute.RequiredRootRegistrations[0], ProductionRoute.RequiredRootRegistrations[2]],
            rootModule.RelativePath, "NethermindModule.Load registration");
        string appInputLoad = FindMethodText(rootModule, "AppInputModule", "Load", 1);
        RequireContainsAll(appInputLoad,
            [ProductionRoute.RequiredRootRegistrations[1], ProductionRoute.RequiredRootRegistrations[2]],
            rootModule.RelativePath, "AppInputModule.Load registration");

        SourceFile context = Get(sources, "src/Nethermind/Nethermind.Init/Modules/MainProcessingContext.cs");
        string mainProcessingConstructor = Normalize(
            FindConstructorSyntax(context, "MainProcessingContext", 10).ToFullString());
        RequireContains(mainProcessingConstructor, "rootLifetimeScope.BeginLifetimeScope((builder)=>",
            context.RelativePath, "main processing lifetime scope");
        RequireOrdered(mainProcessingConstructor,
            ["AddSingleton<IWorldStateScopeProvider>(worldState)", "AddModule(blockValidationModules)",
             "AddModule(mainProcessingModules)", "newBlockchainProcessor(", "blockPreprocessorSteps",
             "IsMainProcessor=true", "AddScoped<IBlockchainProcessor>(ctx=>ctx.Resolve<BlockchainProcessor>())",
             "AddScoped<IBlockProcessingQueue>(ctx=>ctx.Resolve<BlockchainProcessor>())"]);
        foreach (string registration in ProductionRoute.RequiredMainProcessingRegistrations)
            RequireContains(mainProcessingConstructor, Normalize(registration), context.RelativePath,
                "main processing DI registration");
    }

    private static void ValidateSpecRoute(IReadOnlyDictionary<string, SourceFile> sources)
    {
        SourceFile rootModule = Get(sources, "src/Nethermind/Nethermind.Init/Modules/NethermindModule.cs");
        RequireContains(Normalize(rootModule.Text), Normalize("AddSingleton<ISpecProvider,ChainSpecBasedSpecProvider>()"), rootModule.RelativePath, "runtime ChainSpecBasedSpecProvider registration");

        SourceFile api = Get(sources, "src/Nethermind/Nethermind.Runner/Ethereum/Api/ApiBuilder.cs");
        ConstructorDeclarationSyntax apiConstructor = FindConstructorSyntax(api, "ApiBuilder", 3);
        RequireContains(Normalize(apiConstructor.ToFullString()), Normalize("ChainSpec=LoadChainSpec(_jsonSerializer)"),
            api.RelativePath, "chain-spec construction");
        string apiLoadChainSpec = FindMethodText(api, "ApiBuilder", "LoadChainSpec", 1);
        RequireContains(apiLoadChainSpec, Normalize("new(ethereumJsonSerializer,_logManager)"), api.RelativePath,
            "chain-spec loader construction");
        RequireContains(apiLoadChainSpec, Normalize("LoadEmbeddedOrFromFile(_initConfig.ChainSpecPath)"), api.RelativePath,
            "configured chain-spec load");

        SourceFile loader = Get(sources, "src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecFileLoader.cs");
        string normalizedLoader = Normalize(loader.Text);
        RequireContains(normalizedLoader, Normalize("LoadEmbeddedOrFromFile(stringfileName)"), loader.RelativePath, "chain-spec loader entry");
        RequireContains(normalizedLoader, Normalize("fileName=NormalizeFileName(fileName)"), loader.RelativePath, "chain-spec filename normalization");
        RequireContainsAll(normalizedLoader, ProductionRoute.ChainSpecLoaderAnchors,
            loader.RelativePath, "embedded chain-spec resource derivation");
        RequireOrdered(FindMethodText(loader, "ChainSpecFileLoader", "LoadEmbeddedOrFromFile", 1),
            ["fileName=NormalizeFileName(fileName)", "stringextension=Path.GetExtension(fileName)",
             "stringresourceName=FileNameToResource(fileName)", "Assemblyassembly=typeof(IConfig).Assembly",
             "assembly.GetManifestResourceStream(resourceName)",
             "return_chainSpecLoaders[extension].Load(stream)", "returnLoadFromFile(fileName)"]);
        RequireOrdered(FindMethodText(loader, "ChainSpecFileLoader", "FileNameToResource", 1),
            ["sb.Append(\"Nethermind.Config.\")", "sb.Append(fileName)", "sb.Replace('/','.')",
             "returnsb.ToString()"]);
        RequireUniqueDirectReturn(FindMethodSyntax(loader, "ChainSpecFileLoader", "NormalizeFileName", 1),
            "extension==\"\"?$\"{fileName}.json\":fileName", loader.RelativePath,
            "chain-spec filename normalization");

        SourceFile autoLoader = Get(sources,
            "src/Nethermind/Nethermind.Specs/ChainSpecStyle/AutoDetectingChainSpecLoader.cs");
        RequireContainsAll(Normalize(autoLoader.Text), ProductionRoute.AutoDetectingChainSpecAnchors,
            autoLoader.RelativePath, "Parity chain-spec format selection");
        MethodDeclarationSyntax autoLoad = FindMethodSyntax(autoLoader, "AutoDetectingChainSpecLoader", "Load", 1);
        IfStatementSyntax nonSeekable = FindUniqueIf(autoLoad, "!streamData.CanSeek", autoLoader.RelativePath);
        RequireContains(Normalize(nonSeekable.Statement.ToFullString()),
            "returnLoadDetected(format,replayStream)", autoLoader.RelativePath,
            "non-seekable detected format forwarding");
        RequireUniqueDirectReturn(autoLoad, "LoadSeekable(streamData)", autoLoader.RelativePath,
            "seekable loader dispatch");
        MethodDeclarationSyntax loadSeekable = FindMethodSyntax(
            autoLoader, "AutoDetectingChainSpecLoader", "LoadSeekable", 1);
        RequireDirectStatementSequence(loadSeekable,
            ["longstartPosition=streamData.Position;", "GenesisFormatformat=DetectFormat(streamData);",
             "streamData.Position=startPosition;", "returnLoadDetected(format,streamData);"],
            autoLoader.RelativePath, "seekable format detection");
        RequireExpressionBody(FindMethodSyntax(autoLoader, "AutoDetectingChainSpecLoader", "LoadDetected", 2),
            "formatswitch{GenesisFormat.Geth=>_gethLoader.Load(streamData),_=>_parityLoader.Load(streamData),}",
            autoLoader.RelativePath, "detected format loader selection");
        RequireOrdered(FindMethodText(autoLoader, "AutoDetectingChainSpecLoader", "DetectFormat", 1),
            ["reader.TokenTypeisJsonTokenType.EndObject&&reader.CurrentDepth==0",
             "returnhasGethConfig?GenesisFormat.Geth:GenesisFormat.Parity",
             "reader.ValueTextEquals(\"engine\"u8)||reader.ValueTextEquals(\"params\"u8)||reader.ValueTextEquals(\"genesis\"u8)||reader.ValueTextEquals(\"accounts\"u8)",
             "returnGenesisFormat.Parity", "hasGethConfig|=reader.ValueTextEquals(\"config\"u8)",
             "returnGenesisFormat.Unknown"]);

        SourceFile chainSpecLoader = Get(sources,
            "src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs");
        string loadChainSpec = FindMethodText(chainSpecLoader, "ChainSpecLoader", "Load", 1);
        RequireOrdered(loadChainSpec,
            ["serializer.Deserialize<ChainSpecJson>(streamData)", "returnInitChainSpecFrom(chainSpecJson)"]);
        RequireOrdered(FindMethodText(chainSpecLoader, "ChainSpecLoader", "InitChainSpecFrom", 1),
            ProductionRoute.ChainSpecConstructionAnchors);
        RequireContainsAll(FindMethodText(chainSpecLoader, "ChainSpecLoader", "LoadParameters", 3),
            ProductionRoute.ChainSpecTargetForkAnchors, chainSpecLoader.RelativePath,
            "target-fork parameter mapping");
        RequireContains(FindMethodText(chainSpecLoader, "ChainSpecLoader", "LoadTransitions", 2),
            "chainSpec.AmsterdamTimestamp=chainSpec.Parameters.Eip7928TransitionTimestamp",
            chainSpecLoader.RelativePath, "Amsterdam timestamp derivation");

        SourceFile provider = Get(sources, "src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecBasedSpecProvider.cs");
        string normalizedProvider = Normalize(provider.Text);
        RequireContains(normalizedProvider, Normalize("publicChainSpecBasedSpecProvider(ChainSpecchainSpec,ILogManager?logManager=null)"), provider.RelativePath, "runtime chain-spec provider constructor");
        RequireContains(normalizedProvider, Normalize("_chainSpec=chainSpec??thrownewArgumentNullException(nameof(chainSpec))"), provider.RelativePath, "chain-spec provider input");
        RequireContains(normalizedProvider, Normalize("BuildTransitions()"), provider.RelativePath, "chain-spec fork derivation");
        RequireOrdered(FindMethodText(provider, "ChainSpecBasedSpecProvider", "BuildTransitions", 0),
            ["AddTransitions(transitionTimestamps,_chainSpec.Parameters,staticn=>n.EndsWith(\"TransitionTimestamp\"),_chainSpec.Genesis?.Timestamp??0)",
             "CreateTransitions(_chainSpec,transitionBlockNumbers,transitionTimestamps)",
             "LoadTransitions(allTransitions)"]);
        RequireContainsAll(FindMethodText(provider, "ChainSpecBasedSpecProvider", "CreateReleaseSpec", 3),
            ProductionRoute.ChainSpecProviderTargetForkAnchors, provider.RelativePath,
            "runtime target-fork activation mapping");

        SourceFile mainnet = Get(sources, "src/Nethermind/Nethermind.Specs/MainnetSpecProvider.cs");
        string normalizedMainnet = Normalize(mainnet.Text);
        RequireContains(normalizedMainnet, "AmsterdamBlockTimestamp=ulong.MaxValue-1", mainnet.RelativePath, "Amsterdam activation timestamp");
        RequireContains(normalizedMainnet, "Amsterdam.Instance", mainnet.RelativePath, "Amsterdam schedule binding");

        SourceFile fork = Get(sources, "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs");
        string normalizedFork = Normalize(fork.Text);
        foreach (string marker in ProductionRoute.RequiredSpecMarkers)
            if (marker != "AmsterdamBlockTimestamp=ulong.MaxValue-1" && marker != "Amsterdam.Instance"
                && marker != "AddSingleton<ISpecProvider,ChainSpecBasedSpecProvider>()")
                RequireContains(normalizedFork, Normalize(marker), fork.RelativePath, "Amsterdam EIP schedule");
        RequireContains(normalizedFork, "BPO2.Instance", fork.RelativePath, "Amsterdam base schedule");
    }

    private static void ValidateSymbols(IReadOnlyDictionary<string, SourceFile> sources)
    {
        foreach (SourceSymbol expected in ProductionRoute.SourceSymbols)
        {
            SourceFile source = Get(sources, expected.Path);
            TypeDeclarationSyntax[] types = FindTypes(source.Root, expected.Namespace, expected.Type).ToArray();
            if (types.Length == 0)
                throw new ExtractionException($"Missing source type {expected.Namespace}.{expected.Type} in {expected.Path}.");
            if (expected.Member is null)
            {
                if (types.Length != 1)
                    throw new ExtractionException($"Source type {expected.Namespace}.{expected.Type} is ambiguous in {expected.Path}.");
                continue;
            }

            int matches = expected.Member == ".ctor"
                ? types.SelectMany(type => type.Members.OfType<ConstructorDeclarationSyntax>()).Count(constructor =>
                    expected.ParameterCount is null || constructor.ParameterList.Parameters.Count == expected.ParameterCount)
                : types.SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>()).Count(method =>
                    method.Identifier.ValueText == expected.Member
                    && (expected.ParameterCount is null || method.ParameterList.Parameters.Count == expected.ParameterCount));
            if (matches != 1)
                throw new ExtractionException($"Source method {expected.Type}.{expected.Member}/{expected.ParameterCount} is missing or ambiguous in {expected.Path}.");
        }
    }

    private static void ValidateHeaderProjection(IReadOnlyDictionary<string, SourceFile> sources)
    {
        SourceFile header = Get(sources, "src/Nethermind/Nethermind.Core/BlockHeader.cs");
        TypeDeclarationSyntax type = FindType(header.Root, "Nethermind.Core", "BlockHeader")
            ?? throw new ExtractionException("Missing BlockHeader declaration.");
        MethodDeclarationSyntax clone = FindMethod(type, "CloneForProcessing", 0)
            ?? throw new ExtractionException("Missing BlockHeader.CloneForProcessing.");
        MethodDeclarationSyntax copy = FindMethod(type, "CopyProcessingFields", 1)
            ?? throw new ExtractionException("Missing BlockHeader.CopyProcessingFields.");
        ConstructorDeclarationSyntax constructor = FindConstructorSyntax(header, "BlockHeader", 13);

        string cloneText = Normalize(clone.ToFullString());
        RequireContains(cloneText, "new(ParentHash!,UnclesHash!,Beneficiary!,Difficulty,Number,GasLimit,Timestamp,ExtraData)", header.RelativePath, "processing-header constructor projection");
        if (AssignsMember(constructor, "StateRoot"))
            throw new ExtractionException($"Unexpected processing-header state-root assignment in {header.RelativePath}.");
        if (AssignsMember(constructor, "GasUsed"))
            throw new ExtractionException($"Unexpected processing-header gas-used assignment in {header.RelativePath}.");
        if (AssignsMember(copy, "StateRoot"))
            throw new ExtractionException($"Unexpected processing-header state-root copy in {header.RelativePath}.");
        if (AssignsMember(copy, "GasUsed"))
            throw new ExtractionException($"Unexpected processing-header gas-used copy in {header.RelativePath}.");
        string copyText = Normalize(copy.ToFullString());
        foreach (string marker in ProductionRoute.HeaderProjectionMarkers)
            RequireContains(copyText, Normalize(marker), header.RelativePath, "processing-header field projection");
        RequireContains(copyText, "dst.Bloom=Core.Bloom.Empty", header.RelativePath, "processing-header bloom reset");
    }

    private static void ValidateOrder(IReadOnlyDictionary<string, SourceFile> sources)
    {
        SourceFile block = Get(sources, "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs");
        RequireOrdered(FindMethodText(block, "BlockProcessor", "ProcessOne", 5), ProductionRoute.ProcessOneAnchors);
        string processBlock = FindMethodText(block, "BlockProcessor", "ProcessBlock", 5);
        RequireContainsAll(processBlock, ProductionRoute.ProcessBlockAnchors, block.RelativePath, "ProcessBlock hook membership");
        RequireOrdered(FindMethodText(block, "BlockProcessor", "ValidateProcessedBlock", 4), ProductionRoute.ValidateProcessedAnchors);

        SourceFile branch = Get(sources, "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs");
        string branchProcess = FindMethodText(branch, "BranchProcessor", "Process", 5);
        RequireContainsAll(branchProcess, ProductionRoute.BranchAnchors, branch.RelativePath, "BranchProcessor hook membership");

        SourceFile chain = Get(sources, "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs");
        string chainProcess = FindMethodText(chain, "BlockchainProcessor", "Process", 5);
        RequireContainsAll(chainProcess, ProductionRoute.ChainAnchors, chain.RelativePath, "BlockchainProcessor hook membership");
        RequireOrdered(FindMethodText(chain, "BlockchainProcessor", "ProcessBranch", 5), ProductionRoute.ProcessBranchAnchors);
        string prepareBlocks = FindMethodText(chain, "BlockchainProcessor", "PrepareBlocksToProcess", 3);
        RequireContainsAll(prepareBlocks, ProductionRoute.PreparationAnchors, chain.RelativePath, "selected-block preprocessing");

        ValidateOrderEdges(sources, ProductionRoute.ProcessBlockOrderEdges);
        ValidateOrderEdges(sources, ProductionRoute.SynchronousReceiptOrderEdges);
        ValidateOrderEdges(sources, ProductionRoute.BackgroundReceiptOrderEdges);
        ValidateOrderEdges(sources, ProductionRoute.BranchOrderEdges);
        ValidateOrderEdges(sources, ProductionRoute.ChainOrderEdges);
        ValidateOrderEdges(sources, ProductionRoute.PreparationOrderEdges);
        ValidateOrderEdges(sources, ProductionRoute.RecoveryOrderEdges);
        ValidateOrderEdges(sources, ProductionRoute.TransactionFoldOrderEdges);
        ValidateOrderEdges(sources, ProductionRoute.AdapterOrderEdges);
        ValidateOrderEdges(sources, ProductionRoute.TransactionReceiptOrderEdges);
        ValidateNestedRoutes(sources);

        SourceFile std = Get(sources, "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs");
        string normalizedStd = Normalize(std.Text);
        RequireContains(normalizedStd, "ShouldCalculateReceiptsInBackground", std.RelativePath, "receipt scheduling implementation");
        RequireContains(normalizedStd, "BackgroundReceiptCountThreshold=16", std.RelativePath, "receipt background threshold");
        RequireContains(normalizedStd, "BackgroundLogCountThreshold=64", std.RelativePath, "log background threshold");
        RequireExpressionBody(FindMethodSyntax(std, "BlockProcessor", "ShouldCalculateReceiptsInBackground", 1),
            "receipts.Length>=BackgroundReceiptCountThreshold||CountLogs(receipts)>=BackgroundLogCountThreshold",
            std.RelativePath, "standard receipt scheduling predicate");

        SourceFile transaction = Get(sources, "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs");
        RequireOrdered(Normalize(transaction.Text), ProductionRoute.TransactionAnchors);
        foreach (string marker in ProductionRoute.TransactionGasMarkers)
            RequireContains(Normalize(transaction.Text), Normalize(marker), transaction.RelativePath, "execution/state block gas accounting");
        SourceFile systemTransaction = Get(sources, "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs");
        RequireOrdered(Normalize(systemTransaction.Text), ProductionRoute.SystemTransactionAnchors);
        SourceFile receipts = Get(sources, "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs");
        RequireOrdered(Normalize(receipts.Text), ProductionRoute.ReceiptAnchors[..2]);
        foreach (string marker in ProductionRoute.ReceiptGasMarkers)
            RequireContains(Normalize(receipts.Text), Normalize(marker), receipts.RelativePath, "receipt gas/cumulative accounting");
        SourceFile receiptRoot = Get(sources, "src/Nethermind/Nethermind.Blockchain/Receipts/ReceiptsRootCalculator.cs");
        RequireContains(Normalize(receiptRoot.Text), Normalize(ProductionRoute.ReceiptAnchors[2]), receiptRoot.RelativePath, "receipt root implementation");
        string normalizedBlock = Normalize(block.Text);
        foreach (string anchor in ProductionRoute.SynchronousReceiptAnchors)
            RequireContains(normalizedBlock, Normalize(anchor), block.RelativePath, "synchronous receipt artifact order");
        RequireContainsAll(processBlock, ProductionRoute.BackgroundReceiptAnchors, block.RelativePath, "background receipt task membership");
        SourceFile adapter = Get(sources, "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs");
        RequireOrdered(Normalize(adapter.Text), ProductionRoute.AdapterAnchors);
        SourceFile di = Get(sources, "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs");
        RequireContainsAll(Normalize(di.Text), ProductionRoute.AdapterRegistrationAnchors, di.RelativePath, "standard transaction adapter factory");
    }

    private static void ValidateNestedRoutes(IReadOnlyDictionary<string, SourceFile> sources)
    {
        SourceFile chainSource = Get(sources,
            "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs");
        MethodDeclarationSyntax chainProcess = FindMethodSyntax(
            chainSource, "BlockchainProcessor", "Process", 5);
        RequireContains(Normalize(FindUniqueIf(chainProcess,
                "!RunSimpleChecksAheadOfProcessing(suggestedBlock,options)", chainSource.RelativePath)
            .Statement.ToFullString()), "returnnull", chainSource.RelativePath, "simple-check skip nesting");
        RequireContains(Normalize(FindUniqueIf(chainProcess, "!shouldProcess", chainSource.RelativePath)
            .Statement.ToFullString()), "returnnull", chainSource.RelativePath, "eligibility skip nesting");
        RequireContains(Normalize(FindUniqueIf(chainProcess, "processedBlocksisnull", chainSource.RelativePath)
            .Statement.ToFullString()), "returnnull", chainSource.RelativePath, "invalid-branch result nesting");
        RequireContains(Normalize(FindUniqueIf(chainProcess, "processedBlocks.Length>0", chainSource.RelativePath)
            .Statement.ToFullString()),
            "lastProcessed.Header.TotalDifficulty=suggestedBlock.TotalDifficulty",
            chainSource.RelativePath, "processed-prefix total-difficulty nesting");
        RequireContains(Normalize(FindUniqueIf(chainProcess, "updateHead", chainSource.RelativePath)
            .Statement.ToFullString()),
            "_blockTree.TryUpdateMainChain(suggestedBlock.Header,wereProcessed:true,preloadedBlocks:processingBranch.Blocks.AsSpan())",
            chainSource.RelativePath, "conditional head-update nesting");
        RequireContains(Normalize(FindUniqueIf(chainProcess,
                "(options&ProcessingOptions.MarkAsProcessed)==ProcessingOptions.MarkAsProcessed", chainSource.RelativePath)
            .Statement.ToFullString()), "_blockTree.MarkChainAsProcessed(processingBranch.Blocks)",
            chainSource.RelativePath, "conditional processed-chain nesting");

        MethodDeclarationSyntax processBranch = FindMethodSyntax(
            chainSource, "BlockchainProcessor", "ProcessBranch", 5);
        TryStatementSyntax[] classificationTries = processBranch.DescendantNodes().OfType<TryStatementSyntax>()
            .Where(statement => Normalize(statement.Block.ToFullString()).Contains(
                "processedBlocks=_branchProcessor.Process(", StringComparison.Ordinal))
            .ToArray();
        if (classificationTries.Length != 1 || classificationTries[0].Catches.Count != 1
            || classificationTries[0].Finally is null
            || classificationTries[0].Catches[0].Declaration?.Type.ToString() != "InvalidBlockException")
            throw new ExtractionException("The synchronous invalid-block classification boundary is not uniquely identified.");
        RequireContains(Normalize(classificationTries[0].Catches[0].Block.ToFullString()),
            "processedBlocks=null", chainSource.RelativePath, "caught invalid-block result classification");
        RequireContainsAll(Normalize(classificationTries[0].Finally!.Block.ToFullString()),
            ["invalidBlockHashisnotnull", "!options.ContainsFlag(ProcessingOptions.ReadOnlyChain)",
             "DeleteInvalidBlocks(inprocessingBranch,invalidBlockHash)"],
            chainSource.RelativePath, "invalid-block cleanup nesting");

        SourceFile blockSource = Get(sources,
            "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs");
        MethodDeclarationSyntax processOne = FindMethodSyntax(blockSource, "BlockProcessor", "ProcessOne", 5);
        TryStatementSyntax[] processingTries = processOne.DescendantNodes().OfType<TryStatementSyntax>()
            .Where(statement => statement.Finally is not null
                && Normalize(statement.Block.ToFullString()).Contains(
                    "receipts=ProcessBlock(block,blockTracer,options,spec,token)", StringComparison.Ordinal))
            .ToArray();
        if (processingTries.Length != 1)
            throw new ExtractionException("The ProcessOne failure rollback boundary is not uniquely identified.");
        RequirePreparationOutsideRetry(processOne, processingTries[0], blockSource.RelativePath);
        RequireContains(Normalize(processingTries[0].Finally!.Block.ToFullString()),
            "if(!processed)block.DisposeAccountChanges()", blockSource.RelativePath,
            "failed ProcessOne account-change rollback nesting");
        RequireContains(Normalize(FindUniqueIf(processOne,
                "options.ContainsFlag(ProcessingOptions.StoreReceipts)", blockSource.RelativePath)
            .Statement.ToFullString()), "StoreTxReceipts(block,receipts,spec)", blockSource.RelativePath,
            "conditional receipt persistence nesting");
        MethodDeclarationSyntax validateProcessed = FindMethodSyntax(
            blockSource, "BlockProcessor", "ValidateProcessedBlock", 4);
        IfStatementSyntax invalidProcessed = FindUniqueIf(validateProcessed,
            "!options.ContainsFlag(ProcessingOptions.NoValidation)&&!blockValidator.ValidateProcessedBlock(block,receipts,suggestedBlock,outstring?error)",
            blockSource.RelativePath);
        RequireOrdered(Normalize(invalidProcessed.Statement.ToFullString()),
            ["block.DisposeAccountChanges()", "thrownewInvalidBlockException(suggestedBlock,error)"]);

        SourceFile executorSource = Get(sources,
            "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs");
        MethodDeclarationSyntax processTransactions = FindMethodSyntax(
            executorSource, "BlockValidationTransactionsExecutor", "ProcessTransactions", 4);
        ForStatementSyntax[] transactionLoops = processTransactions.DescendantNodes().OfType<ForStatementSyntax>()
            .Where(loop => Normalize(loop.Condition?.ToString() ?? "") == "i<block.Transactions.Length")
            .ToArray();
        if (transactionLoops.Length != 1)
            throw new ExtractionException("The sequential transaction loop is not uniquely identified.");
        string transactionLoop = Normalize(transactionLoops[0].Statement.ToFullString());
        RequireContains(transactionLoop,
            "ProcessTransaction(block,currentTx,i,receiptsTracer,processingOptions)",
            executorSource.RelativePath, "per-transaction adapter nesting");
        RequireContains(transactionLoop,
            "if(shouldValidate&&block.Header.GasUsed>block.Header.GasLimit)",
            executorSource.RelativePath, "per-transaction gas-limit validation nesting");

        SourceFile adapterBoundarySource = Get(sources,
            "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs");
        MethodDeclarationSyntax adapterBoundary = FindMethodSyntax(
            adapterBoundarySource, "TransactionProcessorAdapterExtensions", "ProcessTransaction", 5);
        RequireDirectStatementSequence(adapterBoundary,
            ["usingITxTracertracer=receiptsTracer.StartNewTxTrace(currentTx);",
             "TransactionResultresult=transactionProcessor.Execute(currentTx,receiptsTracer);",
             "receiptsTracer.EndTxTrace();", "returnresult;"],
            adapterBoundarySource.RelativePath, "per-transaction trace/execute lifecycle");

        SourceFile concreteAdapterSource = Get(sources,
            "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecuteTransactionProcessorAdapter.cs");
        RequireExpressionBody(FindMethodSyntax(
                concreteAdapterSource, "ExecuteTransactionProcessorAdapter", "Execute", 2),
            "transactionProcessor.Execute(transaction,txTracer)", concreteAdapterSource.RelativePath,
            "concrete Execute adapter forwarding");

        SourceFile transactionInterfaceSource = Get(sources,
            "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs");
        TypeDeclarationSyntax extensionType = FindType(transactionInterfaceSource.Root,
            "Nethermind.Evm.TransactionProcessing", "ITransactionProcessorExtensions")
            ?? throw new ExtractionException("Missing ITransactionProcessorExtensions declaration.");
        MethodDeclarationSyntax[] executeExtensions = extensionType.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "Execute"
                && method.ParameterList.Parameters.Count == 2)
            .ToArray();
        if (executeExtensions.Length != 1)
            throw new ExtractionException("The ITransactionProcessor Execute/2 extension boundary is missing or ambiguous.");
        RequireExpressionBody(executeExtensions[0],
            "transactionProcessor.Process(transaction,txTracer,ExecutionOptions.Commit)",
            transactionInterfaceSource.RelativePath, "Execute-to-Process extension forwarding");

        SourceFile transactionSource = Get(sources,
            "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs");
        MethodDeclarationSyntax transactionProcess = FindMethodSyntax(
            transactionSource, "TransactionProcessorBase", "Process", 3);
        RequireUniqueDirectReturn(transactionProcess, "ExecuteCore(transaction,txTracer,options)",
            transactionSource.RelativePath, "transaction Process-to-ExecuteCore forwarding");
        MethodDeclarationSyntax executeCore = FindMethodSyntax(
            transactionSource, "TransactionProcessorBase", "ExecuteCore", 3);
        RequireDirectStatementSequence(executeCore,
            ["TransactionResultresult=Execute(tx,tracer,opts);", "returnresult;"],
            transactionSource.RelativePath, "ordinary ExecuteCore dispatch");
        MethodDeclarationSyntax prepareExecute = FindMethodSyntax(
            transactionSource, "TransactionProcessorBase", "Execute", 3);
        RequireUniqueDirectReturn(prepareExecute, "Execute(tx,tracer,opts,header,spec,inintrinsicGas)",
            transactionSource.RelativePath, "validated ordinary transaction dispatch");
        MethodDeclarationSyntax validatedExecute = FindMethodSyntax(
            transactionSource, "TransactionProcessorBase", "Execute", 6);
        IfStatementSyntax simpleTransferDispatch = FindUniqueIf(
            validatedExecute, "simpleTransferRecipientisnotnull", transactionSource.RelativePath);
        RequireUniqueDirectReturn(simpleTransferDispatch.Statement,
            "ExecuteSimpleTransfer(tx,header,spec,tracer,opts,restore,commit,deleteCallerAccount,simpleTransferRecipient,inintrinsicGas,gasAvailable,inopcodeGasPrice,inpremiumPerGas,insenderReservedGasPayment,inblobBaseFee)",
            transactionSource.RelativePath, "simple-transfer dispatch");
        RequireUniqueDirectReturn(validatedExecute,
            "ExecuteEvmTransaction(tx,header,spec,tracer,opts,restore,commit,deleteCallerAccount,inintrinsicGas,gasAvailable,inopcodeGasPrice,inpremiumPerGas,insenderReservedGasPayment,inblobBaseFee,preloadedCodeInfo,preloadedDelegationAddress)",
            transactionSource.RelativePath, "ordinary EVM dispatch");
        RequireUniqueDirectReturn(FindMethodSyntax(
                transactionSource, "TransactionProcessorBase", "ExecuteEvmTransaction", 16),
            "FinalizeTransaction(tx,spec,tracer,opts,restore,commit,deleteCallerAccount,insenderReservedGasPayment,env.ExecutingAccount,insubstate,spentGas,statusCode)",
            transactionSource.RelativePath, "ordinary EVM receipt finalization");
        RequireUniqueDirectReturn(FindMethodSyntax(
                transactionSource, "TransactionProcessorBase", "ExecuteSimpleTransfer", 15),
            "FinalizeTransaction(tx,spec,tracer,opts,restore,commit,deleteCallerAccount,insenderReservedGasPayment,recipient,insubstate,spentGas,statusCode)",
            transactionSource.RelativePath, "simple-transfer receipt finalization");
        MethodDeclarationSyntax finalizeTransaction = FindMethodSyntax(
            transactionSource, "TransactionProcessorBase", "FinalizeTransaction", 12);
        IfStatementSyntax receiptBranch = FindUniqueIf(
            finalizeTransaction, "tracer.IsTracingReceipt", transactionSource.RelativePath);
        string receiptBranchText = Normalize(receiptBranch.Statement.ToFullString());
        RequireContains(receiptBranchText,
            "tracer.MarkAsFailed(executingAccount,spentGas,output,error,stateRoot)",
            transactionSource.RelativePath, "failed receipt nesting");
        RequireContains(receiptBranchText,
            "tracer.MarkAsSuccess(executingAccount,spentGas,substate.Output.AsReadOnlyArray(),logs,stateRoot)",
            transactionSource.RelativePath, "successful receipt nesting");

        SourceFile receiptSource = Get(sources,
            "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs");
        MethodDeclarationSyntax markSuccess = FindMethodSyntax(receiptSource, "BlockReceiptsTracer", "MarkAsSuccess", 5);
        MethodDeclarationSyntax markFailed = FindMethodSyntax(receiptSource, "BlockReceiptsTracer", "MarkAsFailed", 5);
        MethodDeclarationSyntax buildFailure = FindMethodSyntax(receiptSource, "BlockReceiptsTracer", "BuildFailedReceipt", 4);
        RequireDirectStatementSequence(markSuccess,
            ["_txReceipts.Add(BuildReceipt(recipient,gasSpent,StatusCode.Success,logs,stateRoot));"],
            receiptSource.RelativePath, "successful concrete receipt construction");
        RequireDirectStatementSequence(markFailed,
            ["_txReceipts.Add(BuildFailedReceipt(recipient,gasSpent,error,stateRoot));"],
            receiptSource.RelativePath, "failed receipt finalization");
        RequireContains(Normalize(buildFailure.ToFullString()),
            "BuildReceipt(recipient,gasSpent,StatusCode.Failure,[],stateRoot)",
            receiptSource.RelativePath, "failed concrete receipt construction");

        MethodDeclarationSyntax processBlock = FindMethodSyntax(blockSource, "BlockProcessor", "ProcessBlock", 5);
        if (processBlock.Body is null)
            throw new ExtractionException("ProcessBlock must have a block body.");
        StatementSyntax[] directProcessBlockStatements = processBlock.Body.Statements.ToArray();
        int[] transactionSignalIndexes = directProcessBlockStatements
            .Select((statement, index) => (statement, index))
            .Where(item => Normalize(item.statement.ToString()) == "TransactionsExecuted?.Invoke();")
            .Select(item => item.index)
            .ToArray();
        if (transactionSignalIndexes.Length != 1
            || transactionSignalIndexes[0] + 1 >= directProcessBlockStatements.Length
            || Normalize(directProcessBlockStatements[transactionSignalIndexes[0] + 1].ToString()) !=
                "CommitState(spec);")
            throw new ExtractionException(
                "The post-transaction CommitState must directly follow the transaction-completion signal at ProcessBlock scope.");
        IfStatementSyntax receiptMode = FindUniqueIf(
            processBlock, "ShouldCalculateReceiptsInBackground(receipts)", blockSource.RelativePath);
        string backgroundArm = Normalize(receiptMode.Statement.ToFullString());
        RequireContainsAll(backgroundArm,
            ["Task.Run(()=>", "CalculateBlooms(receipts)", "AccumulateBlockBloom(receipts)", "CalculateReceiptsRoot(receipts,spec,block)"],
            blockSource.RelativePath, "background receipt arm nesting");
        InvocationExpressionSyntax[] receiptTasks = receiptMode.Statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Normalize(invocation.Expression.ToString()) == "Task.Run")
            .ToArray();
        if (receiptTasks.Length != 1 || receiptTasks[0].ArgumentList.Arguments.Count != 1
            || receiptTasks[0].ArgumentList.Arguments[0].Expression is not ParenthesizedLambdaExpressionSyntax
                { Block: not null } receiptTask)
            throw new ExtractionException("The background receipt task lambda is missing or ambiguous.");
        RequireOrdered(Normalize(receiptTask.Block.ToFullString()),
            ["CalculateBlooms(receipts)",
             "return(AccumulateBlockBloom(receipts),CalculateReceiptsRoot(receipts,spec,block))"]);
        if (receiptMode.Else?.Statement is not BlockSyntax synchronousArm)
            throw new ExtractionException("The synchronous receipt arm must have a block body.");
        RequireDirectStatementSequence(synchronousArm,
            ["CalculateBlooms(receipts);",
             "header.ReceiptsRoot=CalculateReceiptsRoot(receipts,spec,block);"],
            blockSource.RelativePath, "synchronous receipt arm");
        IfStatementSyntax receiptJoin = FindUniqueIf(
            processBlock, "bloomsAndReceiptsRootTaskisnotnull", blockSource.RelativePath);
        RequireContains(Normalize(receiptJoin.Statement.ToFullString()),
            "(header.Bloom,header.ReceiptsRoot)=bloomsAndReceiptsRootTask.GetAwaiter().GetResult()",
            blockSource.RelativePath, "background receipt success join nesting");
        TryStatementSyntax[] postTransactionTries = processBlock.DescendantNodes().OfType<TryStatementSyntax>()
            .Where(statement => Normalize(statement.Block.ToFullString()).Contains(
                "ApplyMinerRewards(block,blockTracer,spec)", StringComparison.Ordinal))
            .ToArray();
        if (postTransactionTries.Length != 1 || postTransactionTries[0].Finally is null)
            throw new ExtractionException("The post-transaction cleanup boundary is not uniquely identified.");
        RequireDirectStatementSequence(postTransactionTries[0].Block,
            ["ApplyMinerRewards(block,blockTracer,spec);", "_systemContractHandler.ProcessWithdrawals(block,spec);",
             "CommitState(spec);", "_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec);"],
            blockSource.RelativePath, "post-system commit ordering");
        string cleanup = Normalize(postTransactionTries[0].Finally!.Block.ToFullString());
        RequireContainsAll(cleanup,
            ["bloomsAndReceiptsRootTaskis{IsCompletedSuccessfully:false}",
             "bloomsAndReceiptsRootTask.GetAwaiter().GetResult()", "catch"],
            blockSource.RelativePath, "background receipt cleanup join nesting");

        SourceFile branchSource = Get(sources, "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs");
        MethodDeclarationSyntax branchProcess = FindMethodSyntax(branchSource, "BranchProcessor", "Process", 5);
        IfStatementSyntax inScope = FindUniqueIf(branchProcess, "stateProvider.IsInScope", branchSource.RelativePath);
        RequireContainsAll(Normalize(inScope.Statement.ToFullString()),
            ["baseBlockisnull&&suggestedBlock.IsGenesis", "thrownewInvalidOperationException("],
            branchSource.RelativePath, "externally owned genesis scope nesting");
        RequireContains(Normalize(inScope.Else?.Statement.ToFullString() ?? ""),
            "worldStateCloser=stateProvider.BeginScope(baseBlock)", branchSource.RelativePath,
            "owned branch scope nesting");

        ForStatementSyntax[] blockLoops = branchProcess.DescendantNodes().OfType<ForStatementSyntax>()
            .Where(loop => Normalize(loop.Condition?.ToString() ?? "") == "i<blocksCount")
            .ToArray();
        if (blockLoops.Length != 1)
            throw new ExtractionException("The branch success-prefix loop is not uniquely identified.");
        string blockLoop = Normalize(blockLoops[0].Statement.ToFullString());
        RequireOrdered(blockLoop,
            ["blockProcessor.ProcessOne(suggestedBlock,blockOptions,blockTracer,spec,token)",
             "inclusionListSatisfactionChecker.IsSatisfied(processedBlock,suggestedBlock,stateProvider)",
             "WaitAndClear(refpreWarmTask)", "PreCommitBlock(suggestedBlock.Header)",
             "processedBlocksCount=i+1", "if(isCommitPoint&&notReadOnly)", "stateProvider.Reset()"]);

        IfStatementSyntax checkpoint = FindUniqueIf(blockLoops[0],
            "isCommitPoint&&notReadOnly", branchSource.RelativePath);
        if (checkpoint.Statement is not BlockSyntax checkpointBody
            || checkpointBody.Statements.Count < 2
            || Normalize(checkpointBody.Statements[^2].ToString()) != "worldStateCloser?.Dispose();"
            || Normalize(checkpointBody.Statements[^1].ToString()) !=
                "worldStateCloser=stateProvider.BeginScope(previousBranchStateRoot);")
            throw new ExtractionException(
                "The checkpoint scope must end with direct Dispose then reopen statements.");
        if (blockLoops[0].Statement is not BlockSyntax blockLoopBody)
            throw new ExtractionException("The branch success-prefix loop must have a block body.");
        int checkpointIndex = blockLoopBody.Statements.IndexOf(checkpoint);
        int[] directResetIndexes = blockLoopBody.Statements
            .Select((statement, index) => (statement, index))
            .Where(item => Normalize(item.statement.ToString()) == "stateProvider.Reset();")
            .Select(item => item.index)
            .ToArray();
        if (checkpointIndex < 0 || directResetIndexes.Length != 1 || directResetIndexes[0] <= checkpointIndex
            || checkpoint.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation =>
                Normalize(invocation.Expression.ToString()) == "stateProvider.Reset"))
            throw new ExtractionException(
                "The successful-block Reset must be a unique direct loop statement after the checkpoint scope.");
        CatchClauseSyntax[] retryCatches = branchProcess.DescendantNodes().OfType<CatchClauseSyntax>()
            .Where(clause => clause.Declaration?.Type.ToString().EndsWith(
                "BlockAccessListSequentialRetryException", StringComparison.Ordinal) == true)
            .ToArray();
        if (retryCatches.Length != 1)
            throw new ExtractionException("The BAL sequential retry boundary is not uniquely identified.");
        RequireOrdered(Normalize(retryCatches[0].Block.ToFullString()),
            ["worldStateCloser.Dispose()", "worldStateCloser=stateProvider.BeginScope(preBlockBaseBlock)",
             "ProcessingOptionsretryOptions=blockOptions|ProcessingOptions.ForceSequentialBlockAccessList",
             "blockProcessor.ProcessOne(suggestedBlock,retryOptions,blockTracer,spec,token)"]);

        TryStatementSyntax[] branchLifecycleTries = branchProcess.DescendantNodes().OfType<TryStatementSyntax>()
            .Where(statement => statement.Finally is not null
                && statement.Catches.Any(clause => clause.Declaration?.Type.ToString() == "Exception"))
            .ToArray();
        if (branchLifecycleTries.Length != 1)
            throw new ExtractionException("The branch catch/finalization boundary is not uniquely identified.");
        CatchClauseSyntax branchCatch = branchLifecycleTries[0].Catches.Single(clause =>
            clause.Declaration?.Type.ToString() == "Exception");
        RequireOrdered(Normalize(branchCatch.Block.ToFullString()), ["processingException=ex", "throw;"]);
        RequireOrdered(Normalize(branchLifecycleTries[0].Finally!.Block.ToFullString()),
            ["blockProcessor.TransactionsExecuted-=CancelBackgroundWork", "worldStateCloser?.Dispose()",
             "BranchProcessingCompleted?.Invoke(",
             "newBranchProcessingCompletedEventArgs(blocksProcessingEventArgs.Blocks,processedBlocksCount,processingException)"]);
    }

    private static void RequirePreparationOutsideRetry(
        MethodDeclarationSyntax processOne, TryStatementSyntax processingTry, string path)
    {
        InvocationExpressionSyntax[] preparations = processOne.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Normalize(invocation.ToString()) ==
                "_balManager.PrepareForProcessing(suggestedBlock,spec,options)")
            .ToArray();
        if (processOne.Body is not BlockSyntax body || preparations.Length != 1
            || preparations[0].Parent is not ExpressionStatementSyntax preparation
            || body.Statements.IndexOf(preparation) < 0
            || body.Statements.IndexOf(processingTry) <= body.Statements.IndexOf(preparation))
            throw new ExtractionException($"BAL preparation must be a direct statement before the processing retry boundary in {path}.");
        if (ProductionRoute.HookDependencies.Single(hook => hook.Hook == HookId.PrepareBal).FailureDisposition
            != FailureDisposition.Escape)
            throw new ExtractionException("BAL preparation failures escape the processing retry boundary.");
    }

    private static void RequireDirectStatementSequence(
        SyntaxNode scope,
        IReadOnlyList<string> expected,
        string path,
        string obligation)
    {
        SyntaxList<StatementSyntax> statements = scope switch
        {
            MethodDeclarationSyntax { Body: not null } method => method.Body.Statements,
            BlockSyntax block => block.Statements,
            _ => default,
        };
        if (statements.Count == 0)
            throw new ExtractionException($"Missing block body for {obligation} in {path}.");
        string[] actual = statements.Select(statement => Normalize(statement.ToString())).ToArray();
        int cursor = 0;
        foreach (string statement in expected)
        {
            string normalized = Normalize(statement);
            int[] matches = actual.Select((item, index) => (item, index))
                .Where(item => item.item == normalized)
                .Select(item => item.index)
                .ToArray();
            if (matches.Length != 1 || matches[0] < cursor)
                throw new ExtractionException($"Missing, duplicated, or reordered {obligation} statement in {path}: {statement}.");
            cursor = matches[0] + 1;
        }
    }

    private static void RequireExpressionBody(
        MethodDeclarationSyntax method,
        string expected,
        string path,
        string obligation)
    {
        if (method.ExpressionBody is null
            || Normalize(method.ExpressionBody.Expression.ToString()) != Normalize(expected))
            throw new ExtractionException($"Missing or changed {obligation} in {path}.");
    }

    private static void RequireUniqueDirectReturn(
        SyntaxNode scope,
        string expected,
        string path,
        string obligation)
    {
        SyntaxList<StatementSyntax> statements = scope switch
        {
            MethodDeclarationSyntax { Body: not null } method => method.Body.Statements,
            BlockSyntax block => block.Statements,
            _ => default,
        };
        ReturnStatementSyntax[] returns = statements.OfType<ReturnStatementSyntax>().ToArray();
        if (returns.Length != 1
            || Normalize(returns[0].Expression?.ToString() ?? "") != Normalize(expected))
            throw new ExtractionException($"Missing or changed {obligation} direct return in {path}.");
    }

    private static IfStatementSyntax FindUniqueIf(
        SyntaxNode node, string condition, string path)
    {
        IfStatementSyntax[] matches = node.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(statement => Normalize(statement.Condition.ToString()) == condition)
            .ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"The {condition} branch is not uniquely identified in {path}.");
        return matches[0];
    }

    private static TypeDeclarationSyntax? FindType(CompilationUnitSyntax root, string expectedNamespace, string expectedType) =>
        FindTypes(root, expectedNamespace, expectedType).FirstOrDefault();

    private static IEnumerable<TypeDeclarationSyntax> FindTypes(
        CompilationUnitSyntax root, string expectedNamespace, string expectedType) =>
        root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(type =>
            type.Identifier.ValueText == expectedType && NamespaceOf(type) == expectedNamespace);

    private static MethodDeclarationSyntax? FindMethod(TypeDeclarationSyntax type, string name, int parameterCount) =>
        type.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(method =>
            method.Identifier.ValueText == name && method.ParameterList.Parameters.Count == parameterCount);

    private static string FindMethodText(SourceFile source, string typeName, string methodName, int parameterCount)
        => Normalize(FindMethodSyntax(source, typeName, methodName, parameterCount).ToFullString());

    private static MethodDeclarationSyntax FindMethodSyntax(
        SourceFile source, string typeName, string methodName, int parameterCount)
    {
        string expectedNamespace = source.Root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().First().Name.ToString();
        MethodDeclarationSyntax[] methods = FindTypes(source.Root, expectedNamespace, typeName)
            .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>())
            .Where(method => method.Identifier.ValueText == methodName
                && method.ParameterList.Parameters.Count == parameterCount)
            .ToArray();
        if (methods.Length != 1)
            throw new ExtractionException(
                $"Method {typeName}.{methodName}/{parameterCount} is missing or ambiguous in {source.RelativePath}.");
        return methods[0];
    }

    private static ConstructorDeclarationSyntax FindConstructorSyntax(
        SourceFile source, string typeName, int parameterCount)
    {
        string expectedNamespace = source.Root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().First().Name.ToString();
        ConstructorDeclarationSyntax[] constructors = FindTypes(source.Root, expectedNamespace, typeName)
            .SelectMany(type => type.Members.OfType<ConstructorDeclarationSyntax>())
            .Where(constructor => constructor.ParameterList.Parameters.Count == parameterCount)
            .ToArray();
        if (constructors.Length != 1)
            throw new ExtractionException(
                $"Constructor {typeName}/.ctor/{parameterCount} is not unique in {source.RelativePath}.");
        return constructors[0];
    }

    private static void ValidateOrderEdges(IReadOnlyDictionary<string, SourceFile> sources, IReadOnlyList<OrderEdge> edges)
    {
        foreach (OrderEdge edge in edges)
        {
            SourceFile source = Get(sources, edge.Path);
            string method = FindMethodText(source, edge.Type, edge.Method, edge.ParameterCount);
            string before = Normalize(edge.Before);
            string after = Normalize(edge.After);
            int beforeLocation = method.IndexOf(before, StringComparison.Ordinal);
            int afterLocation = beforeLocation < 0 ? -1 : method.IndexOf(after, beforeLocation + before.Length, StringComparison.Ordinal);
            if (beforeLocation < 0 || afterLocation < 0)
                throw new ExtractionException($"Missing or reordered {edge.Id} edge in {edge.Path}.");
        }
    }

    private static void RequireContainsAll(string source, IReadOnlyList<string> anchors, string path, string obligation)
    {
        foreach (string anchor in anchors)
            RequireContains(source, Normalize(anchor), path, obligation);
    }

    private static void RequireOrdered(string source, IReadOnlyList<OrderedAnchor> anchors)
    {
        int cursor = 0;
        foreach (OrderedAnchor anchor in anchors)
        {
            string expected = Normalize(anchor.CanonicalText);
            int location = source.IndexOf(expected, cursor, StringComparison.Ordinal);
            if (location < 0)
                throw new ExtractionException($"Missing or reordered {anchor.Id} anchor in {anchor.Path}.");
            cursor = location + expected.Length;
        }
    }

    private static void RequireOrdered(string source, IReadOnlyList<string> anchors)
    {
        int cursor = 0;
        foreach (string anchor in anchors)
        {
            string expected = Normalize(anchor);
            int location = source.IndexOf(expected, cursor, StringComparison.Ordinal);
            if (location < 0)
                throw new ExtractionException($"Missing or reordered source anchor: {anchor}.");
            cursor = location + expected.Length;
        }
    }

    private static SourceFile Get(IReadOnlyDictionary<string, SourceFile> sources, string path)
    {
        if (sources.TryGetValue(path, out SourceFile? source)) return source;
        throw new ExtractionException($"Audit profile did not pin required source {path}.");
    }

    private static void RequireContains(string source, string expected, string path, string obligation)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
            throw new ExtractionException($"Missing {obligation} anchor in {path}: {expected}.");
    }

    private static void RequireNotContains(string source, string unexpected, string path, string obligation)
    {
        if (source.Contains(unexpected, StringComparison.Ordinal))
            throw new ExtractionException($"Unexpected {obligation} assignment in {path}: {unexpected}.");
    }

    private static bool AssignsMember(SyntaxNode node, string member) =>
        node.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment => assignment.Left switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText == member,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText == member,
            _ => false,
        });

    private static string NamespaceOf(SyntaxNode node)
    {
        List<string> namespaces = [];
        for (SyntaxNode? parent = node.Parent; parent is not null; parent = parent.Parent)
            if (parent is BaseNamespaceDeclarationSyntax declaration)
                namespaces.Add(declaration.Name.ToString());
        namespaces.Reverse();
        return string.Join('.', namespaces);
    }

    private static bool IsIgnoredToolOutput(string packageRoot, string path) =>
        path.StartsWith(Path.Combine(packageRoot, ".lake") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.Combine(packageRoot, "bin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.Combine(packageRoot, "obj") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
            if (!char.IsWhiteSpace(character)) builder.Append(character);
        return builder.ToString();
    }

    private sealed record SourceFile(
        string RelativePath,
        string FullPath,
        string Text,
        CompilationUnitSyntax Root);
}
