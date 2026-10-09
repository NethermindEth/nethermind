// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;

namespace Nethermind.DocGen;

internal static class NethermindAssemblies
{
    /// <summary>
    /// Gets the types exported by the Nethermind assemblies the app depends on at runtime.
    /// </summary>
    /// <remarks>
    /// The assemblies are taken from the trusted platform assemblies, which the host resolves from the app's
    /// <c>deps.json</c>, rather than from the app directory. The latter may also contain build-only assemblies,
    /// such as <c>Nethermind.Evm.Fody</c> when built with <c>--output</c>, whose dependencies are not deployed.
    /// </remarks>
    internal static IEnumerable<Type> GetExportedTypes() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("Nethermind.", StringComparison.Ordinal))
            .SelectMany(p => Assembly.LoadFrom(p).GetExportedTypes());
}
