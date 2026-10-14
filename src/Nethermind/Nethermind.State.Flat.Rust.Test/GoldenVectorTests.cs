// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading.Tasks;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>The golden vectors in flatdb-core/tests/golden, which the cargo tests replay against the Rust persistence.</summary>
[Parallelizable(ParallelScope.All)]
public class GoldenVectorTests
{
    private static readonly FlatLayout[] Layouts = GoldenVectorGenerator.Layouts;

    [Test, Explicit("Rewrites the checked-in golden vectors")]
    public async Task Generate_golden_vectors([ValueSource(nameof(Layouts))] FlatLayout layout, [Values] bool rlpSlots, [Values] bool carryForward) =>
        await File.WriteAllTextAsync(GoldenFile(layout, rlpSlots, carryForward), await GoldenVectorGenerator.Generate(layout, rlpSlots, carryForward));

    [Test]
    public async Task Checked_in_golden_vectors_match_the_csharp_persistence([ValueSource(nameof(Layouts))] FlatLayout layout, [Values] bool rlpSlots, [Values] bool carryForward) =>
        Assert.That(
            await GoldenVectorGenerator.Generate(layout, rlpSlots, carryForward),
            Is.EqualTo(await File.ReadAllTextAsync(GoldenFile(layout, rlpSlots, carryForward))),
            $"The C# persistence drifted from the golden vector; regenerate with --filter \"FullyQualifiedName~{nameof(Generate_golden_vectors)}\" and port the change to Rust.");

    private static string GoldenFile(FlatLayout layout, bool rlpSlots, bool carryForward) =>
        Path.Combine(GoldenDirectory(), GoldenVectorGenerator.FileName(layout, rlpSlots, carryForward));

    /// <summary>Finds the checked-in directory by walking up from the test binaries, which build under src/Nethermind/artifacts.</summary>
    internal static string GoldenDirectory()
    {
        string relative = Path.Combine("Nethermind.State.Flat.Rust", "native", "crates", "flatdb-core", "tests", "golden");
        for (DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException($"{relative} not found above {TestContext.CurrentContext.TestDirectory}");
    }
}
