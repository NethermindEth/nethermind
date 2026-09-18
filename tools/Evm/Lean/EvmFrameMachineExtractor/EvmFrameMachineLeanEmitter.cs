// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.EvmFrameMachineExtractor;

/// <summary>Emits only the admitted Stage A routing and dependency lookup layer.</summary>
internal static class EvmFrameMachineLeanEmitter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Emit(IrDocument document, string irSha256, string sourceSha256)
    {
        EvmFrameMachineProfile.ValidateStageAReady(document);
        ValidateSha256(irSha256, "Canonical IR");
        ValidateSha256(sourceSha256, "Source closure");
        StringBuilder text = new();
        text.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        text.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        text.AppendLine();
        foreach (OpcodePackageDescriptor package in document.OpcodePackages)
            text.Append("import ").AppendLine(package.LeanModule);
        text.AppendLine();
        text.AppendLine("/-!");
        text.AppendLine("Source-extracted standard-mainnet Amsterdam Stage A routing metadata.");
        text.AppendLine("This generated module is theorem-free and deliberately contains no frame transition,");
        text.AppendLine("settlement, world-state adapter, precompile body, or transaction installation.");
        text.AppendLine($"Canonical IR SHA-256: {irSha256}");
        text.AppendLine($"Combined source/dependency SHA-256: {sourceSha256}");
        text.AppendLine("-/");
        text.AppendLine();
        foreach (OpcodePackageDescriptor package in document.OpcodePackages)
            foreach (ProofTheoremIdentity theorem in package.RequiredTheorems)
                text.Append("#check @").AppendLine(theorem.FullyQualifiedName);
        text.AppendLine();
        text.AppendLine("namespace EvmFrameMachineExtractor.Generated.EvmFrameMachineRouting");
        text.AppendLine();
        text.AppendLine("inductive DispatchTable where");
        text.AppendLine("  | noTrace | noTraceCancelable | traced | tracedCancelable");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("def tableIndex : DispatchTable -> Nat");
        text.AppendLine("  | .noTrace => 0");
        text.AppendLine("  | .noTraceCancelable => 1");
        text.AppendLine("  | .traced => 2");
        text.AppendLine("  | .tracedCancelable => 3");
        text.AppendLine();
        text.AppendLine("inductive RouteKind where");
        text.AppendLine("  | enabled | disabled | badInstruction");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("structure OpcodeRoute where");
        text.AppendLine("  table : DispatchTable");
        text.AppendLine("  byte : Nat");
        text.AppendLine("  instruction : String");
        text.AppendLine("  kind : RouteKind");
        text.AppendLine("  activation : String");
        text.AppendLine("  owner : String");
        text.AppendLine("  closedRoot : String");
        text.AppendLine("  admitted : Bool");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("def opcodeRoutes : Array OpcodeRoute := #[");
        for (int index = 0; index < document.OpcodeRoutes.Length; index++)
        {
            OpcodeRouteDescriptor route = document.OpcodeRoutes[index];
            text.Append("  { table := ").Append(LeanTable(route.DispatchTable))
                .Append(", byte := ").Append(route.Byte)
                .Append(", instruction := \"").Append(Escape(route.Instruction))
                .Append("\", kind := ").Append(LeanRouteKind(route.RouteKind))
                .Append(", activation := \"").Append(Escape(route.ActivationRule))
                .Append("\", owner := \"").Append(Escape(route.Package))
                .Append("\", closedRoot := \"").Append(Escape(route.ClosedHandlerRoot))
                .Append("\", admitted := ").Append(route.Admitted ? "true" : "false").Append(" }");
            text.AppendLine(index + 1 == document.OpcodeRoutes.Length ? "" : ",");
        }
        text.AppendLine("]");
        text.AppendLine();
        text.AppendLine("def routeAt (table : DispatchTable) (byte : Nat) : Option OpcodeRoute :=");
        text.AppendLine("  if byte < 256 then opcodeRoutes[(tableIndex table * 256) + byte]? else none");
        text.AppendLine();
        text.AppendLine("structure PrecompileRoute where");
        text.AppendLine("  name : String");
        text.AppendLine("  address : Nat");
        text.AppendLine("  activation : String");
        text.AppendLine("  providerRoot : String");
        text.AppendLine("  wrapperIdentityPath : String");
        text.AppendLine("  wrapperIdentitySha256 : String");
        text.AppendLine("  admitted : Bool");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("def precompileRoutes : List PrecompileRoute := [");
        for (int index = 0; index < document.PrecompileRoutes.Length; index++)
        {
            PrecompileRouteDescriptor route = document.PrecompileRoutes[index];
            text.Append("  { name := \"").Append(Escape(route.Name))
                .Append("\", address := ").Append(route.Address)
                .Append(", activation := \"").Append(Escape(route.ActivationRule))
                .Append("\", providerRoot := \"").Append(Escape(route.ProviderRoot))
                .Append("\", wrapperIdentityPath := \"").Append(Escape(route.WrapperManifestPath))
                .Append("\", wrapperIdentitySha256 := \"").Append(route.WrapperManifestSha256)
                .Append("\", admitted := ").Append(route.Admitted ? "true" : "false").Append(" }");
            text.AppendLine(index + 1 == document.PrecompileRoutes.Length ? "" : ",");
        }
        text.AppendLine("]");
        text.AppendLine();
        text.AppendLine("def precompileAt : Nat -> Option PrecompileRoute");
        text.AppendLine("  | address => precompileRoutes.find? (fun route => route.address == address)");
        text.AppendLine();
        text.AppendLine("structure ProofTheoremIdentity where");
        text.AppendLine("  fullyQualifiedName : String");
        text.AppendLine("  signatureSha256 : String");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("structure DependencyIdentity where");
        text.AppendLine("  kind : String");
        text.AppendLine("  name : String");
        text.AppendLine("  path : String");
        text.AppendLine("  sha256 : String");
        text.AppendLine("  proofModule : Option String");
        text.AppendLine("  proofModulePath : Option String");
        text.AppendLine("  proofModuleSha256 : Option String");
        text.AppendLine("  requiredTheorems : List ProofTheoremIdentity");
        text.AppendLine("  admitted : Bool");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("def dependencies : List DependencyIdentity := [");
        IEnumerable<DependencyIdentity> dependencies =
            document.OpcodePackages.Select(static package =>
                new DependencyIdentity("opcodePackage", package.Name, package.ManifestPath, package.ManifestSha256!,
                    package.LeanModule, package.ProofModulePath, package.ProofModuleSha256,
                    package.RequiredTheorems, package.Admitted))
            .Concat(document.PrecompileRoutes.Select(static route =>
                new DependencyIdentity("precompileWrapper", route.Name, route.WrapperManifestPath,
                    route.WrapperManifestSha256!, null, null, null, [], false)));
        DependencyIdentity[] values = [.. dependencies];
        for (int index = 0; index < values.Length; index++)
        {
            DependencyIdentity dependency = values[index];
            text.Append("  { kind := \"").Append(dependency.Kind)
                .Append("\", name := \"").Append(Escape(dependency.Name))
                .Append("\", path := \"").Append(Escape(dependency.Path))
                .Append("\", sha256 := \"").Append(dependency.Sha256)
                .Append("\", proofModule := ").Append(LeanOptional(dependency.ProofModule))
                .Append(", proofModulePath := ").Append(LeanOptional(dependency.ProofModulePath))
                .Append(", proofModuleSha256 := ").Append(LeanOptional(dependency.ProofModuleSha256))
                .Append(", requiredTheorems := ").Append(LeanTheoremIdentities(dependency.RequiredTheorems))
                .Append(", admitted := ").Append(dependency.Admitted ? "true" : "false").Append(" }");
            text.AppendLine(index + 1 == values.Length ? "" : ",");
        }
        text.AppendLine("]");
        text.AppendLine();
        text.AppendLine("end EvmFrameMachineExtractor.Generated.EvmFrameMachineRouting");
        string generated = text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (ContainsProofDeclaration(generated) || generated.Contains("FrameMachineExecution", StringComparison.Ordinal) ||
            generated.Contains("TransactionAdapter", StringComparison.Ordinal))
            throw new ExtractionException("Stage A emitter introduced a proof or executable frame-transition dependency.");
        return StrictUtf8.GetBytes(generated);
    }

    private static bool ContainsProofDeclaration(string source) =>
        source.Split('\n').Any(static line => line.TrimStart().StartsWith("theorem ", StringComparison.Ordinal) ||
            line.TrimStart().StartsWith("lemma ", StringComparison.Ordinal) ||
            line.Contains(":= by", StringComparison.Ordinal));

    private static string LeanTable(string value) => value switch
    {
        "NoTrace" => ".noTrace",
        "NoTraceCancelable" => ".noTraceCancelable",
        "Traced" => ".traced",
        "TracedCancelable" => ".tracedCancelable",
        _ => throw new ExtractionException($"Unknown dispatch table {value}."),
    };

    private static string LeanRouteKind(string value) => value switch
    {
        "enabled" => ".enabled",
        "disabled" => ".disabled",
        "badInstruction" => ".badInstruction",
        _ => throw new ExtractionException($"Unknown route kind {value}."),
    };

    internal static void ValidateSha256(string? value, string description)
    {
        if (value is not { Length: 64 } || value.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ExtractionException($"{description} SHA-256 must be lowercase hexadecimal.");
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal);

    private static string LeanOptional(string? value) => value is null ? "none" : $"some \"{Escape(value)}\"";

    private static string LeanTheoremIdentities(ProofTheoremIdentity[] theorems) => "[" + string.Join(", ",
        theorems.Select(static theorem =>
            $"{{ fullyQualifiedName := \"{Escape(theorem.FullyQualifiedName)}\", signatureSha256 := \"{theorem.SignatureSha256}\" }}")) + "]";
}
