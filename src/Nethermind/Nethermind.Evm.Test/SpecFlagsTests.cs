// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Keeps <c>SpecFlags.zkevm.cs</c> honest against the fork graph it was generated from.
/// </summary>
/// <remarks>
/// The zkEVM guest is compiled ahead of time, so every reachable handler specialization is emitted
/// while one runs. A fork rule that holds a single value across the range of forks the guest serves
/// can be a constant, which folds the branch reading it and drops the specialization behind the
/// untaken side. This test recomputes which rules qualify and fails when the checked-in file
/// disagrees - which is what happens when a fork is added, or when the range below is moved.
/// </remarks>
public class SpecFlagsTests
{
    /// <summary>Oldest fork the guest serves. Raise it to drop older forks and fold more rules.</summary>
    private const string Floor = nameof(Osaka);

    /// <summary>Newest fork the guest serves, or null to serve every fork descended from the floor.</summary>
    private static readonly string? Max = nameof(Amsterdam);

    /// <summary>Each rule the opcode table branches on, and how it reads from a spec.</summary>
    private static readonly (string Rule, Func<IReleaseSpec, bool> Read)[] Rules =
    [
        ("Eip150", static s => s.Use63Over64Rule),
        ("Eip158", static s => s.ClearEmptyAccountWhenTouched),
        ("Eip2780", static s => s.IsEip2780Enabled),
        ("Eip2929", static s => s.UseHotAndColdStorage),
        ("Eip3860", static s => s.IsEip3860Enabled),
        ("Eip7708", static s => s.IsEip7708Enabled),
        ("Eip8037", static s => s.IsEip8037Enabled),
        ("Eip8038", static s => s.IsEip8038Enabled),
    ];

    [Test]
    public void Fork_range_is_bounded_by_its_floor_and_max()
    {
        List<string> names = ForkRange().Select(static f => f.GetType().Name).ToList();

        Assert.That(names, Does.Contain(Floor));
        Assert.That(names, Does.Not.Contain(nameof(Prague)), "Prague precedes the floor.");
        if (Max is null) return;

        Assert.That(names, Does.Contain(Max));
        IEnumerable<string> pastMax = AllForks()
            .Where(static f => f.GetType().Name != Max && AtOrAbove(f, Max))
            .Select(static f => f.GetType().Name);
        Assert.That(names.Intersect(pastMax), Is.Empty, "The max must exclude every fork descended from it.");
    }

    [Test]
    public void Generated_flags_match_the_fork_range()
    {
        List<NamedReleaseSpec> range = ForkRange();
        string source = File.ReadAllText(GeneratedFilePath());

        Dictionary<string, bool> constants = Regex
            .Matches(source, @"public const bool Const(?<rule>Eip\w+) = (?<value>true|false);")
            .ToDictionary(static m => m.Groups["rule"].Value, static m => m.Groups["value"].Value == "true");
        HashSet<string> folded = Regex
            .Matches(source, @"public static bool (?<rule>Eip\w+)\(IReleaseSpec spec\) => Const\k<rule>;")
            .Select(static m => m.Groups["rule"].Value).ToHashSet();
        HashSet<string> validated = Regex
            .Matches(source, @"Check\(spec\.\w+, Const(?<rule>Eip\w+),")
            .Select(static m => m.Groups["rule"].Value).ToHashSet();

        StringBuilder wrong = new();
        foreach ((string rule, Func<IReleaseSpec, bool> read) in Rules)
        {
            bool value = read(range[0]);
            bool invariant = range.TrueForAll(f => read(f) == value);

            if (invariant != constants.ContainsKey(rule))
            {
                wrong.AppendLine(invariant
                    ? $"{rule} holds {value} across {Describe(range)}; fold it to a constant."
                    : $"{rule} varies across {Describe(range)}; it must not be a constant.");
                continue;
            }

            if (!invariant) continue;

            if (constants[rule] != value)
                wrong.AppendLine($"{rule} is declared {constants[rule]} but is {value} across {Describe(range)}.");
            if (!folded.Contains(rule))
                wrong.AppendLine($"{rule} declares a constant but its body does not return it, so nothing folds.");
            if (!validated.Contains(rule))
                wrong.AppendLine($"{rule} is folded but Validate skips it, so an out-of-range block runs against a rule that does not describe it.");
        }

        Assert.That(wrong.ToString(), Is.Empty, "SpecFlags.zkevm.cs disagrees with the fork graph; move it and Floor/Max together.");
    }

    /// <summary>Forks at or descended from <see cref="Floor"/>, capped at <see cref="Max"/>.</summary>
    /// <remarks>
    /// Ancestry, not ordering: above Osaka the graph branches at BPO2 into BPO3-BPO5 and into
    /// Amsterdam, so there is no single newest fork to count back from.
    /// </remarks>
    private static List<NamedReleaseSpec> ForkRange()
    {
        List<NamedReleaseSpec> all = AllForks();

        NamedReleaseSpec? max = Max is null ? null : all.Find(static f => f.GetType().Name == Max);
        Assert.That(Max is null || max is not null, $"No fork named {Max}.");

        List<NamedReleaseSpec> range = all
            .Where(f => AtOrAbove(f, Floor) && (max is null || AtOrBelow(f, max)))
            .ToList();
        Assert.That(range, Is.Not.Empty, $"No fork lies between {Floor} and {Max ?? "the newest fork"}.");
        return range;
    }

    /// <summary>Every concrete mainnet fork, each replayed onto a fresh instance.</summary>
    private static List<NamedReleaseSpec> AllForks() =>
        typeof(Osaka).Assembly.GetTypes()
            .Where(static t => t.Namespace == typeof(Osaka).Namespace
                && !t.IsAbstract
                && t.IsSubclassOf(typeof(NamedReleaseSpec))
                && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(static t => (NamedReleaseSpec)Activator.CreateInstance(t)!)
            .ToList();

    private static bool AtOrAbove(NamedReleaseSpec fork, string floor)
    {
        for (NamedReleaseSpec? f = fork; f is not null; f = f.Parent)
        {
            if (f.GetType().Name == floor) return true;
        }
        return false;
    }

    private static bool AtOrBelow(NamedReleaseSpec fork, NamedReleaseSpec max)
    {
        for (NamedReleaseSpec? f = max; f is not null; f = f.Parent)
        {
            if (f.GetType() == fork.GetType()) return true;
        }
        return false;
    }

    private static string Describe(List<NamedReleaseSpec> range) =>
        string.Join(", ", range.Select(static f => f.GetType().Name).Order());

    private static string GeneratedFilePath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "Nethermind.Evm", "SpecFlags.zkevm.cs");
}
