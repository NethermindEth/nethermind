// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

public class DasKzgTests
{
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

    [Test]
    public void DasKzg_handle_is_the_same_native_pointer_as_the_execution_layers_handle()
    {
        nint handle = Nethermind.BeaconChain.DataAvailability.DasKzg.Handle;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(handle, Is.Not.EqualTo(nint.Zero));
        Assert.That(KzgPolynomialCommitments.IsInitialized, Is.True);
        Assert.That(Nethermind.BeaconChain.DataAvailability.DasKzg.IsSharedWithExecutionLayerHandle(), Is.True);
    }

    // Locate the source file, not merely a named directory: artifacts/bin contains matching assembly directories.
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
