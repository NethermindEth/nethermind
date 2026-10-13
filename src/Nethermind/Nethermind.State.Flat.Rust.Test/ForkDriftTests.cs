// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Nethermind.State.Flat.Rust.Fork;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// Keeps the fork in Nethermind.State.Flat.Rust/Fork in step with the Nethermind.State.Flat files it copies.
/// </summary>
/// <remarks>
/// Each manifest row records the SHA-256 of its origin at f4c7c182dd. A merge that changes an origin fails here: a
/// verbatim copy is rewritten with <see cref="Regenerate_verbatim_copies"/>, a patched or wrapped one is re-ported by
/// hand and its SHA updated in the same commit.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class ForkDriftTests
{
    public enum ForkKind
    {
        Verbatim,
        Patched,
        Wrapper,
    }

    /// <param name="Origin">The origin's path under Nethermind.State.Flat; the fork file has its file name.</param>
    /// <param name="AddedUsing">The one using a verbatim copy adds for the origin's unforked namespace siblings.</param>
    /// <param name="DroppedUsing">The one origin using a verbatim copy drops because every type it brought in is forked.</param>
    public sealed record ForkFile(string Origin, ForkKind Kind, string OriginSha256, string? AddedUsing, string? DroppedUsing)
    {
        public string Fork => Path.GetFileName(Origin);

        public string OriginNamespaceLine =>
            $"namespace Nethermind.State.Flat{string.Concat(Path.GetDirectoryName(Origin)!.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Select(static part => $".{part}"))};";
    }

    private const string ForkNamespaceLine = "namespace Nethermind.State.Flat.Rust.Fork;";
    private const string ScopeProvider = "Nethermind.State.Flat.ScopeProvider";

    private static readonly ForkFile[] Manifest =
    [
        new("IFlatDbManager.cs", ForkKind.Verbatim, "e884b05a0acecfab647732521a9c4f4ce92d16aba3c0da34c7a98d25f2fb24cb", null, null),
        new("SnapshotPooledList.cs", ForkKind.Verbatim, "7c55b4016a9d2d4b54faa4091922143ac99cadfb30fa6d8ac554ef8ec1ee352f", null, null),
        new("FlatStateReader.cs", ForkKind.Verbatim, "449998ef98974e1988120c386e6ab2ba85f7b57378c6738a969fe4258f0a5357", null, ScopeProvider),
        new("Sync/Snap/SnapFlatStateServer.cs", ForkKind.Verbatim, "e0137f1860d0240625ef47f352c6535dac882180fa55bec201aef17701ec9253", "Nethermind.State.Flat.Sync.Snap", ScopeProvider),
        new("ScopeProvider/FlatScopeProvider.cs", ForkKind.Verbatim, "9462c91af7873bb90beb2362d6123192eeade343bbad8882409b82b359e3e882", ScopeProvider, null),
        new("ScopeProvider/FlatWorldStateManager.cs", ForkKind.Verbatim, "c2c021363a4fc3f1ae4cf0af36708ca813aa1c3d3724e5763391e67ba8ddf4bc", ScopeProvider, null),
        new("ScopeProvider/FlatWorldStateScope.cs", ForkKind.Verbatim, "0432d76be2df16330d8e9fd5858b37ed574a25020bfa605aea6f2353b599c4c9", ScopeProvider, null),
        new("ScopeProvider/FlatStorageTree.cs", ForkKind.Verbatim, "ce5f142aaa8efd8964dff632ee86c719fe6e74df230fcb8f2ed4ff885befddde", ScopeProvider, null),
        new("ScopeProvider/IdleStorageApplier.cs", ForkKind.Verbatim, "a336a032147e038266f66d56f5d517e5fa07c9f293e10f00938b50299923686c", null, null),
        new("ScopeProvider/StateTrieStoreAdapter.cs", ForkKind.Verbatim, "fcb98605abb5dc4e7035fda904a82e117e3a0b79cf5863aad6633c26654e098c", ScopeProvider, null),
        new("ScopeProvider/ReadOnlyStateTrieStoreAdapter.cs", ForkKind.Verbatim, "7a46e32b471ff9bb70aceb5913f61c51ddc5855b4aacc62da05e16f32a5880af", ScopeProvider, null),
        new("ScopeProvider/FlatReadOnlyTrieStore.cs", ForkKind.Verbatim, "06f743d0d8c5628428342149b578b8c8d6112967f6f3e24ab7984e0e9f29e52f", null, null),
        new("ScopeProvider/FlatOverridableWorldScope.cs", ForkKind.Verbatim, "e407ab03d6634e243f765b1c66dcd31a6c1366793b051057dc3e961111e9ab16", ScopeProvider, null),
        new("ScopeProvider/TrieWarmer.cs", ForkKind.Verbatim, "584eac7687d60d95dc657d801b974cf232fa9023fd23c0a36fb9d089e44f86f6", ScopeProvider, null),
        new("SnapshotBundle.cs", ForkKind.Patched, "ccfcb9b847536f161fb2acf4a8d240a5be03310362d851943828ab7ca1a5d750", null, null),
        new("FlatTrieVerifier.cs", ForkKind.Wrapper, "98d048a0af47c3e06b7bbc70b395f030df5ddc0d838debd248af82fe757ba232", null, null),
    ];

    /// <summary>The origin text and the text a verbatim copy has in its place.</summary>
    /// <remarks>
    /// The <see cref="ForkInternals"/> rows reach internals the fork assembly cannot see. The <c>IdleStorageApplier</c>
    /// rows exist because the fork assembly disables runtime marshalling, under which a <c>DllImport</c> with
    /// <c>SetLastError</c> or a by-ref parameter does not run; the generated <c>LibraryImport</c> stub keeps both.
    /// </remarks>
    private static readonly (string Origin, string Fork)[] Substitutions =
    [
        ("KeyHashBatch.MinimumBatchSize", "ForkInternals.KeyHashMinimumBatchSize"),
        ("scope.StateTree.SetAccounts(_dirtyAccounts)", "ForkInternals.SetAccounts(scope.StateTree, _dirtyAccounts)"),
        ("StorageTree.CreateBulkSetEntry(", "ForkInternals.CreateBulkSetEntry(null, "),
        ("internal sealed class IdleStorageApplier", "internal sealed partial class IdleStorageApplier"),
        ("[DllImport(\"libc\", SetLastError = true)]", "[LibraryImport(\"libc\", SetLastError = true)]"),
        ("private static extern int sched_setscheduler(", "private static partial int sched_setscheduler("),
    ];

    private static IEnumerable<TestCaseData> ManifestRows() =>
        Manifest.Select(static file => new TestCaseData(file).SetArgDisplayNames(file.Fork));

    [TestCaseSource(nameof(ManifestRows))]
    public void Fork_copy_tracks_its_origin(ForkFile file)
    {
        string origin = ReadText(OriginPath(file));
        string originSha256 = Sha256(origin);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(originSha256, Is.EqualTo(file.OriginSha256), () =>
                $"re-port {file.Fork}: {file.Origin} changed since f4c7c182dd (SHA-256 now {originSha256})." +
                (file.Kind == ForkKind.Wrapper ? "" : $"{Environment.NewLine}{Diff(origin, ToOrigin(file))}"));

            if (file.Kind == ForkKind.Verbatim)
            {
                Assert.That(Diff(origin, ToOrigin(file)), Is.Empty,
                    $"{file.Fork} differs from {file.Origin} beyond the namespace, the added or dropped using and the substitutions; rerun {nameof(Regenerate_verbatim_copies)}.");
            }
        }
    }

    [Test]
    public void Fork_does_not_shadow_metrics_and_pins_the_constants_it_duplicates()
    {
        Type keyHashBatch = typeof(StateTree).Assembly.GetType("Nethermind.State.KeyHashBatch", throwOnError: true)!;
        object? minimumBatchSize = keyHashBatch.GetField("MinimumBatchSize", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                typeof(ForkInternals).Assembly.GetTypes().Where(static type => !type.IsNested && type.Name == "Metrics" &&
                    type.Namespace is "Nethermind.State.Flat.Rust" or "Nethermind.State.Flat.Rust.Fork"),
                Is.Empty,
                "A Metrics type here would shadow Nethermind.State.Flat.Metrics in the fork.");
            Assert.That(minimumBatchSize, Is.EqualTo(ForkInternals.KeyHashMinimumBatchSize));
        }
    }

    [Test, Explicit("Rewrites the verbatim fork copies and their recorded origin SHA-256")]
    public void Regenerate_verbatim_copies()
    {
        string manifestPath = Path.Combine(SourceRoot(), "Nethermind.State.Flat.Rust.Test", $"{nameof(ForkDriftTests)}.cs");
        string manifestSource = ReadText(manifestPath);
        foreach (ForkFile file in Manifest.Where(static file => file.Kind == ForkKind.Verbatim))
        {
            string origin = ReadText(OriginPath(file));
            File.WriteAllText(ForkPath(file), ToFork(file, origin));
            manifestSource = manifestSource.Replace(file.OriginSha256, Sha256(origin));
        }

        File.WriteAllText(manifestPath, manifestSource);
    }

    private static string ToFork(ForkFile file, string origin)
    {
        List<string> lines = [.. origin.Split('\n').Select(line => line == file.OriginNamespaceLine ? ForkNamespaceLine : Substitute(line, toFork: true))];
        if (file.DroppedUsing is not null && !lines.Remove($"using {file.DroppedUsing};"))
            throw new InvalidOperationException($"{file.Origin} no longer has using {file.DroppedUsing}; update its manifest row");
        if (file.AddedUsing is not null && lines.Contains($"using {file.AddedUsing};"))
            throw new InvalidOperationException($"{file.Origin} already has using {file.AddedUsing}; update its manifest row");
        if (file.AddedUsing is not null) InsertUsing(lines, file.AddedUsing);
        return string.Join('\n', lines);
    }

    /// <summary>The fork copy with its namespace line, usings and substitutions turned back into the origin's.</summary>
    private static string ToOrigin(ForkFile file)
    {
        List<string> lines = [.. ReadText(ForkPath(file)).Split('\n').Select(line => line == ForkNamespaceLine ? file.OriginNamespaceLine : Substitute(line, toFork: false))];
        if (file.AddedUsing is not null) lines.Remove($"using {file.AddedUsing};");
        if (file.DroppedUsing is not null) InsertUsing(lines, file.DroppedUsing);
        return string.Join('\n', lines);
    }

    /// <summary>Inserts <c>using <paramref name="usingNamespace"/>;</c> at its ordinal place among the Nethermind usings.</summary>
    private static void InsertUsing(List<string> lines, string usingNamespace)
    {
        string usingLine = $"using {usingNamespace};";
        int lastUsing = lines.FindLastIndex(static line => line.StartsWith("using ", StringComparison.Ordinal));
        int firstLater = lines.FindIndex(line => line.StartsWith("using Nethermind.", StringComparison.Ordinal) &&
            string.CompareOrdinal(line[..^1], usingLine[..^1]) > 0);
        lines.Insert(firstLater >= 0 ? firstLater : lastUsing + 1, usingLine);
    }

    private static string Substitute(string line, bool toFork) =>
        Substitutions.Aggregate(line, (text, substitution) => toFork
            ? text.Replace(substitution.Origin, substitution.Fork, StringComparison.Ordinal)
            : text.Replace(substitution.Fork, substitution.Origin, StringComparison.Ordinal));

    /// <summary>The lines only in <paramref name="expected"/> (-) or only in <paramref name="actual"/> (+), by line number; empty when equal.</summary>
    private static string Diff(string expected, string actual)
    {
        string[] expectedLines = expected.Split('\n');
        string[] actualLines = actual.Split('\n');
        int[,] commonSuffix = new int[expectedLines.Length + 1, actualLines.Length + 1];
        for (int expectedIndex = expectedLines.Length - 1; expectedIndex >= 0; expectedIndex--)
        {
            for (int actualIndex = actualLines.Length - 1; actualIndex >= 0; actualIndex--)
            {
                commonSuffix[expectedIndex, actualIndex] = expectedLines[expectedIndex] == actualLines[actualIndex]
                    ? commonSuffix[expectedIndex + 1, actualIndex + 1] + 1
                    : Math.Max(commonSuffix[expectedIndex + 1, actualIndex], commonSuffix[expectedIndex, actualIndex + 1]);
            }
        }

        StringBuilder diff = new();
        int expectedPosition = 0, actualPosition = 0;
        while (expectedPosition < expectedLines.Length || actualPosition < actualLines.Length)
        {
            if (expectedPosition < expectedLines.Length && actualPosition < actualLines.Length && expectedLines[expectedPosition] == actualLines[actualPosition])
            {
                expectedPosition++;
                actualPosition++;
            }
            else if (expectedPosition < expectedLines.Length &&
                (actualPosition == actualLines.Length || commonSuffix[expectedPosition + 1, actualPosition] >= commonSuffix[expectedPosition, actualPosition + 1]))
            {
                diff.Append($"-{expectedPosition + 1}: {expectedLines[expectedPosition++]}\n");
            }
            else
            {
                diff.Append($"+{actualPosition + 1}: {actualLines[actualPosition++]}\n");
            }
        }

        return diff.ToString();
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string ReadText(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    private static string OriginPath(ForkFile file) => Path.Combine(SourceRoot(), "Nethermind.State.Flat", file.Origin);

    private static string ForkPath(ForkFile file) => Path.Combine(SourceRoot(), "Nethermind.State.Flat.Rust", "Fork", file.Fork);

    /// <summary>Finds src/Nethermind by walking up from the test binaries, which build under src/Nethermind/artifacts.</summary>
    private static string SourceRoot()
    {
        string relative = Path.Combine("Nethermind.State.Flat.Rust", "Fork");
        for (DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, relative))) return directory.FullName;
        }

        throw new DirectoryNotFoundException($"{relative} not found above {TestContext.CurrentContext.TestDirectory}");
    }
}
