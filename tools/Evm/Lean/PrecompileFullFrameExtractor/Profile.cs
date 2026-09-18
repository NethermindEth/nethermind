// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.PrecompileFullFrameExtractor;

internal static class Profile
{
    internal const string Package = "tools/Evm/Lean/PrecompileFullFrameExtractor/";
    internal const string Kernel = "standard-mainnet-amsterdam-precompile-full-frame-stage-c";
    internal const string IrName = "precompile-full-frame-stage-c.ir.json";
    internal const string ManifestName = "precompile-full-frame-stage-c.source-manifest.json";
    internal const string LeanName = "PrecompileFullFrame.lean";
    private const string StageA = "tools/Evm/Lean/PrecompileFrameExtractor/Generated/";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static Artifacts Build(string repositoryRoot)
    {
        string root = Path.GetFullPath(repositoryRoot);
        byte[] admissionBytes = ReadResource("Admission.json");
        RequireEqual(Resolve(root, Package + "Admission.json"), admissionBytes);
        PinDocument admission = Deserialize<PinDocument>(admissionBytes);
        if (admission.SchemaVersion != 1 || admission.Files.Length == 0)
            throw new ExtractionException("Stage C requires its reviewed nonempty admission closure.");

        HashSet<string> uniquePaths = new(StringComparer.OrdinalIgnoreCase);
        List<Pin> sources = [];
        List<Pin> dependencies = [];
        List<MemberIdentity> members = [];
        foreach (Pin pin in admission.Files)
        {
            if (!uniquePaths.Add(pin.Path) || pin.Role is not ("production" or "stageA" or "oracle" or "proof" or "target" or "artifact" or "local"))
                throw new ExtractionException($"Duplicate or unknown Stage C admission entry: {pin.Path}.");
            byte[] bytes = File.ReadAllBytes(Resolve(root, pin.Path));
            if (Hash(bytes) != pin.Sha256)
                throw new ExtractionException($"Stage C exact source/dependency profile rejected {pin.Path}.");
            string text = Utf8.GetString(bytes);
            if (pin.Role == "production")
            {
                sources.Add(pin);
                CollectMembers(pin, text, members);
            }
            else
            {
                dependencies.Add(pin);
                foreach (string symbol in pin.Symbols)
                {
                    if (Regex.Matches(text, @"(?m)^\s*(?:private\s+)?(?:theorem|def|structure|inductive|abbrev)\s+" + Regex.Escape(symbol) + @"(?=\s|\(|:|$)").Count != 1)
                        throw new ExtractionException($"Stage C requires exactly one Lean symbol {symbol} in {pin.Path}.");
                }
            }
        }

        if (sources.Count != 10 || members.Count != 48 || dependencies.Count != 53)
            throw new ExtractionException("Stage C requires exactly 10 production sources, 48 members and 53 dependencies.");

        OracleIdentity[] oracles = ReadOracles(root, admission);
        ValidateArchitecture(root);
        IrDocument ir = new(1, Kernel, "universal-refinement-under-explicit-leaf-and-adapter-premises",
            ["ExecuteTransaction", "ExecutePrecompile", "RunPrecompile<Eip158>", "ExecutePrecompileCall", "top-level substate or nested parent-resume"],
            "VmState.To = Env.CodeSource ?? Env.ExecutingAccount", "Env.ExecutingAccount",
            sources.ToArray(), members.ToArray(), dependencies.ToArray(), oracles, Branches(),
            [
                "A standard-mainnet Amsterdam precompile frame already exists; Eip158 is active and this is not contract creation.",
                "The selected code address, call data, leaf result and base/data costs match the exact admitted Stage A route and oracle.",
                "Front callbacks model normal action tracing, transfer logs and AddToBalanceAndCreateIfNotExists at the executing account; their world projection is an explicit adapter premise.",
                "Settlement primitives implement the separately theorem-pinned gas and FrameJournal transitions; this package proves their order, not concrete WorldJournal composition.",
                "Natural gas values represent UInt64 inputs; all other adapter arithmetic satisfies its existing signed/fixed-width bounds.",
                "An outer-exception entry supplies the exact machine at an EvmException/Overflow throw boundary while the admitted precompile remains current; no exception-reachability claim is made.",
                "Top-level substate and trace callbacks preserve the successful exit/control discriminants required by the target settlement relation.",
            ],
            ["inline STATICCALL and direct stack result", "cancellation", "TrySave and output-copy OOG", "missing-native process termination as an ordinary result",
                "cryptographic/native/CLR/JIT correctness", "concrete WorldJournal composition", "outer throws after parent restoration or frame pop",
                "full frame driver closure", "transaction and block reachability"]);
        byte[] irBytes = Serialize(ir);
        byte[] leanBytes = Emit(ir, Hash(irBytes));
        SourceManifest manifest = new(1, Kernel, typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
            Hash(admissionBytes), ir.Sources, ir.Members, ir.Dependencies, ir.Oracles,
            new(IrName, Hash(irBytes)), new(LeanName, Hash(leanBytes)));
        return new(ir, manifest, irBytes, Serialize(manifest), leanBytes);
    }

    private static void CollectMembers(Pin pin, string source, List<MemberIdentity> members)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14), pin.Path);
        foreach (Diagnostic diagnostic in tree.GetDiagnostics())
            if (diagnostic.Severity == DiagnosticSeverity.Error)
                throw new ExtractionException($"Stage C source {pin.Path} has Roslyn errors: {diagnostic.Id}.");
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        foreach (string required in pin.Symbols)
        {
            string[] parts = required.Split(':');
            string name = parts[0];
            int expected = parts.Length == 2 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1;
            int found = 0;
            foreach (SyntaxNode node in root.DescendantNodes())
            {
                string? memberName = node switch
                {
                    MethodDeclarationSyntax method => method.Identifier.ValueText,
                    PropertyDeclarationSyntax property => property.Identifier.ValueText,
                    _ => null,
                };
                if (memberName != name) continue;
                found++;
                string owner = node.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText;
                List<string> statements = [];
                if (node is MethodDeclarationSyntax { Body: { } body })
                    foreach (StatementSyntax statement in body.Statements) statements.Add(Hash(Utf8.GetBytes(Canonical(statement))));
                members.Add(new(pin.Path, owner, name, node.Kind().ToString(), Hash(Utf8.GetBytes(Canonical(node))), statements.ToArray()));
            }
            if (found != expected)
                throw new ExtractionException($"Stage C expected {expected} exact {name} member(s) in {pin.Path}, found {found}.");
        }
    }

    private static OracleIdentity[] ReadOracles(string root, PinDocument admission)
    {
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllBytes(Resolve(root, StageA + "precompile-frame-stage-a.ir.json")));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Resolve(root, StageA + "precompile-frame-stage-a.source-manifest.json")));
        foreach (string collection in new[] { "sources", "dependencies" })
            foreach (JsonElement dependency in manifest.RootElement.GetProperty(collection).EnumerateArray())
            {
                string path = dependency.GetProperty("path").GetString()!;
                if (Hash(File.ReadAllBytes(Resolve(root, path))) != dependency.GetProperty("sha256").GetString())
                    throw new ExtractionException($"Stage C accepted Stage A closure has drifted: {path}.");
            }
        if (ir.RootElement.GetProperty("routes").GetArrayLength() != 18)
            throw new ExtractionException("Stage C requires all 18 accepted Stage A routes.");
        List<OracleIdentity> result = [];
        HashSet<int> addresses = [];
        foreach (JsonElement binding in ir.RootElement.GetProperty("precompileBindings").EnumerateArray())
        {
            string name = binding.GetProperty("name").GetString()!;
            int address = binding.GetProperty("address").GetInt32();
            JsonElement? identity = null;
            foreach (JsonElement dependency in manifest.RootElement.GetProperty("dependencies").EnumerateArray())
                if (dependency.GetProperty("kind").GetString() == "handwrittenOracle" && dependency.GetProperty("name").GetString() == name)
                {
                    if (identity is not null) throw new ExtractionException($"Duplicate Stage A oracle {name}.");
                    identity = dependency;
                }
            if (identity is not { } entry || !addresses.Add(address))
                throw new ExtractionException($"Missing or duplicate Stage A oracle {name}.");
            string path = entry.GetProperty("path").GetString()!;
            string hash = entry.GetProperty("sha256").GetString()!;
            if (!admission.Files.Any(pin => pin.Role == "oracle" && pin.Path == path && pin.Sha256 == hash))
                throw new ExtractionException($"Stage A oracle {name} is not exact-hash pinned.");
            result.Add(new(name, address, path, hash, binding.GetProperty("oracleNamespace").GetString()!,
                binding.GetProperty("oracleEntrySymbol").GetString()!));
        }
        if (result.Count != 18) throw new ExtractionException("Stage C lost an oracle identity.");
        return result.ToArray();
    }

    private static void ValidateArchitecture(string root)
    {
        string template = Utf8.GetString(ReadResource("Templates.PrecompileFullFrame.lean"));
        string types = File.ReadAllText(Resolve(root, Package + "Specification/Types.lean"));
        string reference = File.ReadAllText(Resolve(root, Package + "Specification/Reference.lean"));
        string refinement = File.ReadAllText(Resolve(root, Package + "Refinement/PrecompileFullFrame.lean"));
        if (Regex.IsMatch(template, @"(?m)^\s*(theorem|axiom)\b") || template.Contains("sorry", StringComparison.Ordinal) ||
            template.Contains("Specification.Reference", StringComparison.Ordinal) || template.Contains("FrameMachineExecution", StringComparison.Ordinal) ||
            reference.Contains("PrecompileFullFrame.Generated", StringComparison.Ordinal) || reference.Contains("Generated.PrecompileFullFrame", StringComparison.Ordinal) ||
            !reference.Contains("def select ", StringComparison.Ordinal) || !reference.Contains("def project ", StringComparison.Ordinal) ||
            !types.Contains("def Entry.Admitted : Entry → Prop", StringComparison.Ordinal) ||
            !refinement.Contains("def FrontPreservesParents", StringComparison.Ordinal) ||
            !refinement.Contains("theorem generated_execute_is_raw_admitted", StringComparison.Ordinal) ||
            !refinement.Contains("(admitted : entry.Admitted)", StringComparison.Ordinal) ||
            !refinement.Contains("(frontParents : FrontPreservesParents front)", StringComparison.Ordinal) ||
            refinement.Contains("(domain : ∀ raw", StringComparison.Ordinal))
            throw new ExtractionException("Stage C generated/reference independence or theorem-free emitter contract changed.");
    }

    internal static Branch[] Branches()
    {
        Effect[] prefix = [Effect.ActionStart, Effect.TransferLog, Effect.TouchExecutingAccount, Effect.RipemdLatch, Effect.Pricing];
        Effect[] run = [.. prefix, Effect.InstallPricedGas, Effect.RunOracle];
        Effect[] failurePrefix = [Effect.RestoreSnapshot, Effect.RestoreRipemdTouch, Effect.OperationRemainingGasZero, Effect.OperationError, Effect.ActionError];
        Effect[] hardTop = [.. failurePrefix, Effect.TopFailureSubstate];
        Effect[] hardNested = [.. failurePrefix, Effect.ClearReturnData, Effect.RemoveAdvancedRefund, Effect.RestoreChildStateGasOnHalt,
            Effect.CreditNewAccountStateGas, Effect.ParentResume];
        return
        [
            new(BranchKind.PricingOverflow, "handleFailure/PrecompileOutOfGas", prefix, hardTop, hardNested, false, false, true),
            new(BranchKind.PricingOutOfGas, "handleFailure/PrecompileOutOfGas", prefix, hardTop, hardNested, false, false, true),
            new(BranchKind.ReturnedFailure, "handleFailure/PrecompileOutOfGas", run, hardTop, hardNested, true, true, true),
            new(BranchKind.ManagedTopFailure, "handleFailure/PrecompileExecutionFailure", run, hardTop, [], true, true, true),
            new(BranchKind.ManagedNestedFailure, "nestedPrecompileSoftFailure", run, [],
                [Effect.ClearExecutionGas, Effect.ClassifyShouldRevert, Effect.ReturnChildExecutionGas, Effect.RemoveAdvancedRefund,
                    Effect.RestoreChildStateGas, Effect.CreditNewAccountStateGas, Effect.HandleRevert, Effect.ParentResume], true, true, true),
            new(BranchKind.TopSuccess, "ordinary", run, [Effect.TraceTransactionActionEnd, Effect.PrepareTopLevelSubstate], [], true, true, true),
            new(BranchKind.NestedSuccess, "ordinary", run, [], [Effect.IncorporateAdvancedRefund, Effect.RefundChildGas,
                Effect.HandleRegularReturn, Effect.CommitToParent, Effect.RepayStateGasSpill, Effect.ParentResume], true, true, true),
            new(BranchKind.OuterEvmException, "handleFailure", [], hardTop, hardNested, false, false, true),
            new(BranchKind.OuterOverflow, "handleFailure/Other", [], hardTop, hardNested, false, false, true),
            new(BranchKind.MissingNativeDependency, "excluded", run, [Effect.ProcessExitExcluded], [Effect.ProcessExitExcluded], true, true, false),
        ];
    }

    private static byte[] Emit(IrDocument ir, string hash)
    {
        StringBuilder output = new();
        output.Append("-- Generated from the exact Stage C Roslyn source/member profile.\n-- IR SHA-256: ").Append(hash).Append('\n');
        output.Append(Utf8.GetString(ReadResource("Templates.PrecompileFullFrame.lean")).Replace("\r\n", "\n", StringComparison.Ordinal));
        output.Append("\nnamespace Eip803x.PrecompileFullFrame.Generated\n\ndef admittedBranches : List String := [\n");
        foreach (Branch branch in ir.Branches) output.Append("  \"").Append(branch.Kind).Append("\",\n");
        output.Append("]\n\ndef admittedOracleAddresses : List Nat := [");
        output.Append(string.Join(", ", ir.Oracles.Select(static oracle => oracle.Address)));
        output.Append("]\n\nend Eip803x.PrecompileFullFrame.Generated\n");
        return Utf8.GetBytes(output.ToString());
    }

    internal static void Write(string directory, Artifacts artifacts)
    {
        Directory.CreateDirectory(directory);
        foreach ((string name, byte[] bytes) in ArtifactFiles(artifacts))
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(path, bytes);
        }
    }

    internal static void Validate(string directory, Artifacts artifacts)
    {
        foreach ((string name, byte[] bytes) in ArtifactFiles(artifacts)) RequireEqual(Path.Combine(directory, name), bytes);
    }

    private static (string, byte[])[] ArtifactFiles(Artifacts artifacts) =>
        [(IrName, artifacts.IrBytes), (ManifestName, artifacts.ManifestBytes), (LeanName, artifacts.LeanBytes)];

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Serialize<T>(T value) => Utf8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n");
    private static T Deserialize<T>(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new ExtractionException("Empty Stage C admission."); }
        catch (JsonException exception) { throw new ExtractionException($"Invalid Stage C admission: {exception.Message}"); }
    }

    private static string Canonical(SyntaxNode node) => string.Join("\n", node.DescendantTokens().Select(static token => $"{token.RawKind}:{token.Text}"));

    private static byte[] ReadResource(string suffix)
    {
        using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Nethermind.Evm.Lean.PrecompileFullFrameExtractor." + suffix) ?? throw new ExtractionException($"Missing resource {suffix}.");
        using MemoryStream bytes = new();
        resource.CopyTo(bytes);
        return bytes.ToArray();
    }

    internal static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\')) throw new ExtractionException($"Noncanonical Stage C path {relative}.");
        string current = root;
        foreach (string segment in relative.Split('/'))
        {
            if (segment is "" or "." or "..") throw new ExtractionException($"Unsafe Stage C path {relative}.");
            string? match = null;
            foreach (string candidate in Directory.EnumerateFileSystemEntries(current))
            {
                if (!Path.GetFileName(candidate).Equals(segment, StringComparison.OrdinalIgnoreCase)) continue;
                if (match is not null || !Path.GetFileName(candidate).Equals(segment, StringComparison.Ordinal))
                    throw new ExtractionException($"Missing, noncanonical or ambiguous Stage C path {relative}.");
                match = candidate;
            }
            current = match ?? throw new ExtractionException($"Missing Stage C path {relative}.");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ExtractionException($"Stage C disallows reparse points in its source closure: {relative}.");
        }
        return current;
    }

    private static void RequireEqual(string path, byte[] expected)
    {
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
            throw new ExtractionException($"Stage C deterministic artifact mismatch: {path}.");
    }
}
