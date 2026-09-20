// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

/// <summary>
/// Proves the plugin loads no KZG trusted setup of its own: it forwards to the execution layer's
/// single handle rather than owning a second one.
/// </summary>
public class DasKzgTests
{
    /// <summary>
    /// Static proof: exactly one production call site in the whole repository calls
    /// <c>Ckzg.LoadTrustedSetup</c>. This does not depend on process load order or on which tests ran
    /// first (both a concern for any runtime check here), and it fails loud - by name and file - the
    /// moment a second loader is (re)introduced anywhere, including outside this project.
    /// </summary>
    [Test]
    public void Exactly_one_production_call_site_loads_the_kzg_trusted_setup()
    {
        string sourceRoot = FindSourceRoot();

        string[] callSites = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains(".Test") && !path.Contains(".Benchmark") && !path.Contains("Ethereum.")
                && File.ReadAllText(path).Contains("Ckzg.LoadTrustedSetup"))
            .ToArray();

        Assert.That(callSites, Has.Length.EqualTo(1),
            $"expected exactly one production Ckzg.LoadTrustedSetup call site, found: {string.Join(", ", callSites)}");
        Assert.That(callSites[0], Does.Match(@"Nethermind\.Crypto[\\/]KzgPolynomialCommitments\.cs$"));
    }

    /// <summary>
    /// Runtime proof: the plugin's handle is not merely non-zero, it is bit-for-bit the same native
    /// pointer as the execution layer's own <see cref="KzgPolynomialCommitments"/> handle - i.e. this
    /// really is the one shared setup, not a second copy that happens to also work.
    /// </summary>
    [Test]
    public void DasKzg_handle_is_the_same_native_pointer_as_the_execution_layers_handle()
    {
        nint handle = Nethermind.BeaconChain.DataAvailability.DasKzg.Handle;

        Assert.Multiple(() =>
        {
            Assert.That(handle, Is.Not.EqualTo(nint.Zero));
            Assert.That(KzgPolynomialCommitments.IsInitialized, Is.True);
            Assert.That(Nethermind.BeaconChain.DataAvailability.DasKzg.IsSharedWithExecutionLayerHandle(), Is.True);
        });
    }

    /// <summary>
    /// Walks up from the test binary's own output folder to the 'src/Nethermind' source tree. Checks
    /// for the source file itself, not just a same-named folder: 'artifacts/bin' also has
    /// 'Nethermind.Crypto'/'Nethermind.BeaconChain' subfolders (build output), and matching on the
    /// folder name alone would stop there and silently scan zero source files.
    /// </summary>
    private static string FindSourceRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 15 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Nethermind.Crypto", "KzgPolynomialCommitments.cs")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not find the 'src/Nethermind' source root walking up from '{AppContext.BaseDirectory}'.");
    }
}
