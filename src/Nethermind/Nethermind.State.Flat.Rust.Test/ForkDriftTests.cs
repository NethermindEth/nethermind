// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using Nethermind.Logging;
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

    private const string ForkNamespace = "Nethermind.State.Flat.Rust.Fork";
    private const string ForkNamespaceLine = $"namespace {ForkNamespace};";
    private const string ScopeProvider = "Nethermind.State.Flat.ScopeProvider";

    /// <summary>The origin lines of the patched SnapshotBundle.cs that its copy keeps verbatim: the warmer and prewarm paths.</summary>
    /// <remarks>Line 243 is left out: its copy calls <see cref="ForkInternals.MarkWarmerOwned"/>.</remarks>
    private static readonly (int First, int Last)[] PatchedUnchangedLines = [(206, 242), (244, 288), (338, 384), (565, 586), (665, 668)];

    /// <summary>Types of Nethermind.State.Flat the fork replaces without sharing a name with a fork type.</summary>
    private static readonly string[] ReplacedOriginals = ["Nethermind.State.Flat.SnapshotContent", "Nethermind.State.Flat.FlatDbManager"];

    /// <summary>Methods of Nethermind.State.Flat's resource pool that rent the C# content the fork replaces.</summary>
    private static readonly string[] ReplacedPoolMethods = ["GetSnapshotContent", "CreateSnapshot"];

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

            if (file.Kind == ForkKind.Patched)
            {
                string[] originLines = origin.Split('\n');
                string fork = $"\n{ReadText(ForkPath(file))}\n";
                foreach ((int first, int last) in PatchedUnchangedLines)
                {
                    Assert.That(fork, Does.Contain($"\n{string.Join('\n', originLines[(first - 1)..last])}\n"),
                        $"origin lines {first}-{last} of {file.Origin} are no longer verbatim in {file.Fork}");
                }
            }
        }
    }

    /// <summary>
    /// A fork type that names an original Nethermind.State.Flat type the fork replaces, in its signatures, locals or IL,
    /// would silently run the C# code path: a simple name the fork lacks resolves to the original.
    /// </summary>
    [Test]
    public void Fork_binds_to_no_original_it_replaces()
    {
        Assembly flatAssembly = typeof(StateId).Assembly;
        Type[] forkTypes = [.. typeof(ForkInternals).Assembly.GetTypes().Where(static type => type.Namespace == ForkNamespace)];
        HashSet<string> forkNames = [.. forkTypes.Select(RelativeName)];

        bool IsReplaced(Type type) => type.Assembly == flatAssembly && !type.IsGenericParameter &&
            (forkNames.Contains(RelativeName(type)) || ReplacedOriginals.Contains(type.FullName));

        bool IsReplacedPoolMethod(MemberInfo member) => member is MethodBase method && ReplacedPoolMethods.Contains(method.Name) &&
            method.DeclaringType is { } pool && (pool == typeof(ResourcePool) || pool == typeof(IResourcePool));

        // The wrapper builds the original verifier and runs its core; nothing else may name the original, and only the
        // original's own members may bring in what their signatures name.
        static bool IsAllowed(Type user, string site, MemberInfo member, Type referenced) =>
            (referenced == typeof(Nethermind.State.Flat.FlatTrieVerifier) || member.DeclaringType == typeof(Nethermind.State.Flat.FlatTrieVerifier)) &&
            (Outermost(user) == typeof(Fork.FlatTrieVerifier) || (user == typeof(ForkInternals) && site.StartsWith($"{nameof(ForkInternals.VerifyCore)}(", StringComparison.Ordinal)));

        List<string> misbindings = [];
        foreach (Type user in forkTypes)
        {
            foreach ((string site, MemberInfo member) in References(user))
            {
                if (IsReplacedPoolMethod(member)) misbindings.Add($"{user.FullName} {site}: {member.DeclaringType!.FullName}.{member.Name}");

                foreach (Type referenced in TypesOf(member).SelectMany(Expand).Where(IsReplaced).Distinct())
                {
                    if (!IsAllowed(user, site, member, referenced)) misbindings.Add($"{user.FullName} {site}: {referenced.FullName}");
                }
            }
        }

        Assert.That(misbindings, Is.Empty);
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
            Assert.That(ILogManager.GetLoggerName(typeof(FlatDbManager)), Is.EqualTo(ForkInternals.FlatDbManagerLoggerName));
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

    /// <summary>What <paramref name="user"/> names, by the member naming it: base types, field types, signatures, locals and IL operands.</summary>
    internal static IEnumerable<(string Site, MemberInfo Member)> References(Type user)
    {
        const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        if (user.BaseType is { } baseType) yield return ("base type", baseType);
        foreach (Type implemented in user.GetInterfaces()) yield return ("interface", implemented);
        foreach (FieldInfo field in user.GetFields(declared)) yield return (field.Name, field.FieldType);
        foreach (MethodBase method in user.GetMethods(declared).Concat<MethodBase>(user.GetConstructors(declared)))
        {
            string site = $"{method.Name}({string.Join(", ", method.GetParameters().Select(static parameter => parameter.ParameterType.Name))})";
            yield return (site, method);
            if (method.GetMethodBody() is not { } body) continue;

            foreach (LocalVariableInfo local in body.LocalVariables) yield return (site, local.LocalType);
            foreach (MemberInfo operand in IlOperands(method, body.GetILAsByteArray()!)) yield return (site, operand);
        }
    }

    /// <summary>The fields, methods and types the IL of <paramref name="method"/> references.</summary>
    private static IEnumerable<MemberInfo> IlOperands(MethodBase method, byte[] il)
    {
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } declaringType ? declaringType.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (int position = 0; position < il.Length;)
        {
            OpCode opCode = il[position] == 0xFE ? OpCodeTables.TwoByte[il[position + 1]] : OpCodeTables.OneByte[il[position]];
            if (opCode.Size == 0) throw new InvalidOperationException($"Unknown opcode at IL offset {position} of {method.DeclaringType}.{method.Name}");

            position += opCode.Size;
            if (opCode.OperandType is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok)
            {
                yield return method.Module.ResolveMember(BitConverter.ToInt32(il, position), typeArguments, methodArguments)!;
            }

            position += opCode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
                _ => 4,
            };
        }
    }

    private static readonly (OpCode[] OneByte, OpCode[] TwoByte) OpCodeTables = BuildOpCodeTables();

    private static (OpCode[] OneByte, OpCode[] TwoByte) BuildOpCodeTables()
    {
        OpCode[] oneByte = new OpCode[0x100];
        OpCode[] twoByte = new OpCode[0x100];
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            OpCode opCode = (OpCode)field.GetValue(null)!;
            ushort value = (ushort)opCode.Value;
            if (opCode.Size == 1) oneByte[value] = opCode;
            else twoByte[value & 0xFF] = opCode;
        }

        return (oneByte, twoByte);
    }

    /// <summary>The types a referenced member names: itself, or its declaring type and signature.</summary>
    private static IEnumerable<Type> TypesOf(MemberInfo member)
    {
        IEnumerable<Type?> types = member switch
        {
            Type type => [type],
            FieldInfo field => [field.DeclaringType, field.FieldType],
            MethodInfo method => [method.DeclaringType, method.ReturnType, .. method.GetParameters().Select(static parameter => parameter.ParameterType),
                .. method.IsGenericMethod ? method.GetGenericArguments() : []],
            ConstructorInfo constructor => [constructor.DeclaringType, .. constructor.GetParameters().Select(static parameter => parameter.ParameterType)],
            _ => [],
        };
        return types.OfType<Type>();
    }

    /// <summary><paramref name="type"/> with the element types and generic arguments it is built from.</summary>
    private static IEnumerable<Type> Expand(Type type) =>
        [type,
            .. type.HasElementType ? Expand(type.GetElementType()!) : [],
            .. type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericArguments().SelectMany(Expand) : []];

    /// <summary>The name of <paramref name="type"/> with those of the types it is nested in, without the namespace.</summary>
    private static string RelativeName(Type type) => type.DeclaringType is null ? type.Name : $"{RelativeName(type.DeclaringType)}+{type.Name}";

    private static Type Outermost(Type type) => type.DeclaringType is null ? type : Outermost(type.DeclaringType);

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
