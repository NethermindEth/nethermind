// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// untaken side. Rules that vary but take the same value on every fork in the range move together,
/// so one reads the spec and the others follow its flag, which drops the pairings the range never
/// produces. This test recomputes which rules qualify for each and fails when the checked-in file
/// disagrees - which is what happens when a fork is added, or when the range below is moved.
/// </remarks>
public class SpecFlagsTests
{
    private const string ResourceName = "SpecFlags.zkevm.cs";

    /// <summary>Oldest fork the guest serves. Raise it to drop older forks and fold more rules.</summary>
    private const string Floor = nameof(Osaka);

    /// <summary>Newest fork the guest serves, or null to serve every fork descended from the floor.</summary>
    private static readonly string? Max = nameof(Amsterdam);

    /// <summary>
    /// Each rule the opcode table branches on: the spec property the generated file must read it
    /// from, and the same read as a delegate for evaluating it on a fork.
    /// </summary>
    /// <remarks>Extension properties are spelled out because <c>nameof</c> rejects extension members (CS9316).</remarks>
    private static readonly (string Rule, string Property, Func<IReleaseSpec, bool> Read)[] Rules =
    [
        ("Eip150", "Use63Over64Rule", static s => s.Use63Over64Rule),
        ("Eip158", "ClearEmptyAccountWhenTouched", static s => s.ClearEmptyAccountWhenTouched),
        ("Eip2200", "UseNetGasMeteringWithAStipendFix", static s => s.UseNetGasMeteringWithAStipendFix),
        ("Eip2780", nameof(IReleaseSpec.IsEip2780Enabled), static s => s.IsEip2780Enabled),
        ("Eip2929", "UseHotAndColdStorage", static s => s.UseHotAndColdStorage),
        ("Eip3860", nameof(IReleaseSpec.IsEip3860Enabled), static s => s.IsEip3860Enabled),
        ("Eip6780", "SelfdestructOnlyOnSameTransaction", static s => s.SelfdestructOnlyOnSameTransaction),
        ("Eip7708", nameof(IReleaseSpec.IsEip7708Enabled), static s => s.IsEip7708Enabled),
        ("Eip8037", nameof(IReleaseSpec.IsEip8037Enabled), static s => s.IsEip8037Enabled),
        ("Eip8038", nameof(IReleaseSpec.IsEip8038Enabled), static s => s.IsEip8038Enabled),
        ("Eip8246", "RemoveSelfdestructBurn", static s => s.RemoveSelfdestructBurn),
        ("NetGasMetering", "UseNetGasMetering", static s => s.UseNetGasMetering),
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
        string source = GeneratedSource();

        HashSet<string> declared = Regex
            .Matches(source, @"public static bool (?<rule>\w+)(?:<T\w+>)?\(IReleaseSpec spec\)")
            .Select(static m => m.Groups["rule"].Value).ToHashSet();
        Dictionary<string, bool> constants = Regex
            .Matches(source, @"public const bool Const(?<rule>\w+) = (?<value>true|false);")
            .ToDictionary(static m => m.Groups["rule"].Value, static m => m.Groups["value"].Value == "true");
        HashSet<string> folded = Regex
            .Matches(source, @"public static bool (?<rule>\w+)(?:<T\w+>)?\(IReleaseSpec spec\)(?: where T\w+ : struct, IFlag)? => Const\k<rule>;")
            .Select(static m => m.Groups["rule"].Value).ToHashSet();
        Dictionary<string, string> reads = Regex
            .Matches(source, @"public static bool (?<rule>\w+)(?:<T\w+>)?\(IReleaseSpec spec\)(?: where T\w+ : struct, IFlag)? => spec\.(?<property>\w+);")
            .ToDictionary(static m => m.Groups["rule"].Value, static m => m.Groups["property"].Value);
        Dictionary<string, string> derived = Regex
            .Matches(source, @"public static bool (?<rule>\w+)<T(?<anchor>\w+)>\(IReleaseSpec spec\) where T\k<anchor> : struct, IFlag => T\k<anchor>\.IsActive;")
            .ToDictionary(static m => m.Groups["rule"].Value, static m => m.Groups["anchor"].Value);
        Dictionary<string, string> checkedProperties = Regex
            .Matches(source, @"Check\(spec\.(?<property>\w+), Const(?<rule>\w+),")
            .ToDictionary(static m => m.Groups["rule"].Value, static m => m.Groups["property"].Value);
        Dictionary<string, (string Property, string AnchorProperty, string Anchor)> followed = Regex
            .Matches(source, @"Follows\(spec\.(?<property>\w+), spec\.(?<anchorProperty>\w+), ""(?<rule>[^""]+)"", ""(?<anchor>[^""]+)""\)")
            .ToDictionary(
                static m => m.Groups["rule"].Value,
                static m => (m.Groups["property"].Value, m.Groups["anchorProperty"].Value, m.Groups["anchor"].Value));

        StringBuilder wrong = new();

        // Everything below walks Rules, so a rule the file declares, or names as an anchor, but Rules
        // lacks would go unexamined.
        Assert.That(declared.Concat(constants.Keys).Concat(derived.Values).Except(Rules.Select(static r => r.Rule)), Is.Empty,
            "A rule in the generated file with no entry in Rules is never checked against the fork graph.");

        Dictionary<string, string> properties = Rules.ToDictionary(static r => r.Rule, static r => r.Property);
        Dictionary<string, string> values = Rules.ToDictionary(
            static r => r.Rule,
            r => string.Concat(range.Select(f => r.Read(f) ? '1' : '0')));

        foreach ((string rule, string property, Func<IReleaseSpec, bool> read) in Rules)
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

            if (invariant)
            {
                if (constants[rule] != value)
                    wrong.AppendLine($"{rule} is declared {constants[rule]} but is {value} across {Describe(range)}.");
                if (!folded.Contains(rule))
                    wrong.AppendLine($"{rule} declares a constant but its body does not return it, so nothing folds.");
                if (!checkedProperties.TryGetValue(rule, out string? checkedProperty))
                    wrong.AppendLine($"{rule} is folded but Validate skips it, so an out-of-range block runs against a rule that does not describe it.");
                else if (checkedProperty != property)
                    wrong.AppendLine($"Validate checks {rule} against spec.{checkedProperty}, but the rule reads spec.{property}.");
                continue;
            }

            List<string> peers = Rules
                .Select(static r => r.Rule)
                .Where(r => r != rule && values[r] == values[rule])
                .ToList();

            if (derived.TryGetValue(rule, out string? anchor))
            {
                if (!peers.Contains(anchor))
                    wrong.AppendLine($"{rule} follows {anchor} but does not move with it across {Describe(range)}; read it from the spec.");
                else if (derived.ContainsKey(anchor))
                    wrong.AppendLine($"{rule} follows {anchor}, which follows another rule itself; follow the one that reads the spec.");
                if (!followed.TryGetValue(Display(rule), out (string Property, string AnchorProperty, string Anchor) pairing))
                    wrong.AppendLine($"{rule} follows {anchor} but Validate does not check that they agree, so an out-of-range block runs against a pairing that was never compiled.");
                else if (pairing.Anchor != Display(anchor) || pairing.Property != property || pairing.AnchorProperty != properties[anchor])
                    wrong.AppendLine($"Validate checks {rule} as spec.{pairing.Property} following {pairing.Anchor} as spec.{pairing.AnchorProperty}; the rule reads spec.{property} and follows {anchor}, spec.{properties[anchor]}.");
                continue;
            }

            if (!reads.TryGetValue(rule, out string? readProperty))
                wrong.AppendLine($"{rule} varies across {Describe(range)} but neither reads the spec nor follows another rule.");
            else if (readProperty != property)
                wrong.AppendLine($"{rule} reads spec.{readProperty}; the opcode table's rule is spec.{property}.");

            // This rule reads the spec, so a peer that also reads it compiles pairings the range never produces.
            foreach (string peer in peers)
            {
                if (!derived.ContainsKey(peer) && string.CompareOrdinal(rule, peer) < 0)
                    wrong.AppendLine($"{rule} and {peer} move together across {Describe(range)}; make one follow the other.");
            }
        }

        Assert.That(wrong.ToString(), Is.Empty, "SpecFlags.zkevm.cs disagrees with the fork graph; move it and Floor/Max together.");
    }

    /// <summary>The name Validate reports a rule under: <c>EIP-150</c> for <c>Eip150</c>.</summary>
    private static string Display(string rule) =>
        rule.StartsWith("Eip", StringComparison.Ordinal) ? $"EIP-{rule.AsSpan(3)}" : rule;

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

    /// <summary>Reads the generated file from this assembly rather than from disk.</summary>
    /// <remarks>
    /// A deterministic build rewrites source paths to a normalized root, so a caller-file path does
    /// not name a file that exists at run time. The csproj embeds the file instead.
    /// </remarks>
    private static string GeneratedSource()
    {
        using Stream stream = typeof(SpecFlagsTests).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded in the test assembly.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
