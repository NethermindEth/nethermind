// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Evm.Lean.OrdinaryEvmCompletionExtractor;

internal static class SemanticBindings
{
    internal const string Path = SourceAdmission.PackagePath + "/Admission/REVIEWED_BINDINGS.json";
    internal const string Sha256 = "71794b8c220e0b645f7a373d6a35c9b8ea2434b192cb1bac6f36769226d2c4db";
    internal sealed record MethodBinding(string Symbol, ParameterIdentity[] Parameters);
    internal sealed record SiteBinding(string Role, string Owner, string OperationSha256);
    internal sealed record BindingInventory(int SchemaVersion, MethodBinding[] Methods, SiteBinding[] Sites, string[] OperationKinds);

    internal static BindingInventory Describe(SourceModel model) => new(1,
        model.Compiler.Members.Select(static member => new MethodBinding(member.Identity.Symbol, member.Identity.Parameters)).ToArray(),
        model.Plan.Expressions.Select(static site => new SiteBinding(site.Role, site.Owner,
            CompilerReferences.Hash(JsonSerializer.SerializeToUtf8Bytes(site.Operation, CompilerReferences.JsonOptions)))).ToArray(),
        model.Compiler.Members.SelectMany(static member => Descendants(member.Body).Concat(member.Blocks.SelectMany(static block =>
            block.Operations.SelectMany(Descendants).Concat(block.BranchValue is null ? [] : Descendants(block.BranchValue))))).Select(static operation => operation.Kind)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    internal static void Require(string root, SourceModel model)
    {
        byte[] bytes = File.ReadAllBytes(CompilerReferences.Within(root, Path));
        CompilerReferences.RequireHash(bytes, Sha256, Path);
        BindingInventory expected = JsonSerializer.Deserialize<BindingInventory>(bytes, CompilerReferences.JsonOptions)
            ?? throw new AdmissionException("binding.empty");
        BindingInventory actual = Describe(model);
        if (expected.SchemaVersion != 1 || expected.Methods.Length != 10 || expected.Sites.Length != 43 || actual.Sites.Length != expected.Sites.Length ||
            !actual.Methods.Select(static method => method.Symbol).SequenceEqual(expected.Methods.Select(static method => method.Symbol), StringComparer.Ordinal))
            throw new AdmissionException("binding.method-signatures");
        if (!actual.OperationKinds.SequenceEqual(expected.OperationKinds, StringComparer.Ordinal))
            throw new AdmissionException("binding.operation-kinds");
        for (int index = 0; index < expected.Sites.Length; index++)
            if (actual.Sites[index] != expected.Sites[index])
                throw new AdmissionException("binding.site." + expected.Sites[index].Role);
    }

    private static IEnumerable<OperationTerm> Descendants(OperationTerm operation)
    {
        yield return operation;
        foreach (OperationTerm child in operation.Children)
            foreach (OperationTerm descendant in Descendants(child)) yield return descendant;
    }
}
