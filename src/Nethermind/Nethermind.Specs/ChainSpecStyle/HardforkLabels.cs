// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Specs.ChainSpecStyle;

/// <summary>
/// Shorthand hardfork labels for the Parity-style chainspec JSON and the EIP-7949 Geth-style
/// genesis. Each label is a single activation value — a block number for pre-Shanghai forks
/// (<c>long?</c>) and a timestamp for post-merge forks (<c>ulong?</c>) — that fans out at load
/// time to the full set of per-EIP transition fields that make up that fork.
/// </summary>
/// <remarks>
/// Labels are sourced from <see cref="IHasNamedForks.NamedForks"/> (populated by
/// <c>[JsonExtensionData]</c> on the Parity side and synthesized from typed <c>&lt;fork&gt;Time</c>
/// properties on the Geth side) and applied to an <see cref="IEipTransitionFields"/> target.
/// A label and an explicit per-EIP field may coexist with matching values (redundant);
/// conflicting values are rejected at load.
/// </remarks>
public static partial class HardforkLabels
{
    /// <summary>All labels in canonical fork order. Exposed for tests and tooling.</summary>
    /// <remarks>
    /// The contents are produced by <c>Nethermind.Analyzers.HardforkLabelsGenerator</c> from the
    /// <c>Nethermind.Specs.Forks.*</c> <see cref="Nethermind.Specs.Forks.NamedReleaseSpec"/>
    /// subclasses — Forks/*.cs is the single source of truth for the per-fork EIP set.
    /// </remarks>
    public static IReadOnlyList<IHardforkLabel> All { get; } = BuildAll();

    /// <summary>Implemented by the source generator; emits the explicit <c>Block</c>/<c>Time</c> registrations.</summary>
    private static partial IReadOnlyList<IHardforkLabel> BuildAll();

    /// <summary>
    /// Expands every hardfork label present in <paramref name="source"/>'s
    /// <see cref="IHasNamedForks.NamedForks"/> into the constituent per-EIP transition fields on
    /// <paramref name="target"/>. Throws <see cref="InvalidConfigurationException"/> when a label
    /// disagrees with an explicit per-EIP value already on the target.
    /// </summary>
    /// <remarks>
    /// Parity-style chainspecs pass the same instance for both parameters (it implements both
    /// interfaces). Geth-style genesis loaders pass the destination <see cref="ChainParameters"/>
    /// as target and the parsed config as source.
    /// </remarks>
    public static void ExpandAll(IEipTransitionFields target, IHasNamedForks source)
    {
        if (source.NamedForks is null or { Count: 0 }) return;
        foreach (IHardforkLabel label in All) label.Apply(target, source.NamedForks);
    }

    /// <param name="name">Fork class name (e.g. <c>Cancun</c>); the JSON wire key is its camelCase form.</param>
    internal static HardforkLabel<long> Block(
        string name,
        params Expression<Func<IEipTransitionFields, long?>>[] eips) =>
        new(name, static e => e.Deserialize<long>(EthereumJsonSerializer.JsonOptions), eips);

    /// <inheritdoc cref="Block"/>
    internal static HardforkLabel<ulong> Time(
        string name,
        params Expression<Func<IEipTransitionFields, ulong?>>[] eips) =>
        new(name, static e => e.Deserialize<ulong>(EthereumJsonSerializer.JsonOptions), eips);
}

/// <summary>
/// Wire-format object that exposes the chainspec's hardfork shorthand keys as a JSON-element-keyed
/// dictionary. <see cref="HardforkLabels.ExpandAll"/> consumes recognized entries; unknown entries
/// stay in the dictionary for upstream typo detection.
/// </summary>
public interface IHasNamedForks
{
    /// <summary>
    /// Dictionary of fork shorthand entries keyed by canonical fork name (case-insensitive).
    /// Values are <see cref="JsonElement"/>; numeric tokens and hex strings are both accepted.
    /// </summary>
    IReadOnlyDictionary<string, JsonElement>? NamedForks { get; }
}

public interface IHardforkLabel
{
    /// <summary>Fork class name in PascalCase, e.g. <c>Shanghai</c>. The JSON wire key is its camelCase form.</summary>
    string LabelName { get; }

    /// <summary>Property names of every per-EIP transition field this label fans out to.</summary>
    IReadOnlyList<string> EipPropertyNames { get; }

    /// <summary>
    /// Canonical EIP numbers activated (or, for <c>Eip&lt;N&gt;DisableTransition</c>, revoked) by
    /// this label, parsed from <see cref="EipPropertyNames"/> and deduplicated.
    /// </summary>
    /// <remarks>
    /// Parity-split fields (<c>Eip161abcTransition</c>, <c>Eip161dTransition</c>) both fold to
    /// EIP-158 — the canonical EIP number that the runtime <c>IsEip158Enabled</c> flag tracks.
    /// </remarks>
    IReadOnlyList<int> Eips { get; }

    /// <summary>
    /// If <paramref name="namedForks"/> carries this label's key, copies the value into each
    /// constituent EIP field on <paramref name="target"/> that is still unset. Throws
    /// <see cref="InvalidConfigurationException"/> when an EIP field is already set to a
    /// different value.
    /// </summary>
    void Apply(IEipTransitionFields target, IReadOnlyDictionary<string, JsonElement> namedForks);
}

internal sealed class HardforkLabel<T> : IHardforkLabel
    where T : struct, IEquatable<T>, IFormattable
{
    // Eip{N}[abc|d][Disable]Transition[Timestamp] — captures the EIP number and the Parity-split
    // marker so EipPropertyNames can be normalized to canonical EIPs in `Eips`.
    private static readonly Regex EipPropertyPattern =
        new(@"^Eip(?<eip>\d+)(?<split>abc|d)?(?:Disable)?Transition(?:Timestamp)?$", RegexOptions.Compiled);

    private readonly Func<JsonElement, T> _parse;
    private readonly string _jsonKey;
    private readonly EipAccessor[] _eips;

    public string LabelName { get; }
    public IReadOnlyList<string> EipPropertyNames { get; }
    public IReadOnlyList<int> Eips { get; }

    public HardforkLabel(string labelName, Func<JsonElement, T> parse, Expression<Func<IEipTransitionFields, T?>>[] eips)
    {
        LabelName = labelName;
        _jsonKey = ToCamelCase(labelName);
        _parse = parse;
        _eips = [.. eips.Select(static e => new EipAccessor(e.GetName(), e.Compile(), e.GetSetter()))];
        EipPropertyNames = [.. _eips.Select(static e => e.Name)];
        Eips = [.. EipPropertyNames.Select(ParseCanonicalEip).Distinct()];
    }

    public void Apply(IEipTransitionFields target, IReadOnlyDictionary<string, JsonElement> namedForks)
    {
        if (!namedForks.TryGetValue(_jsonKey, out JsonElement element)) return;

        T labelValue = _parse(element);

        foreach (EipAccessor eip in _eips)
        {
            T? current = eip.Read(target);
            if (current is null)
            {
                eip.Write(target, labelValue);
            }
            else if (!current.Value.Equals(labelValue))
            {
                throw new InvalidConfigurationException(
                    $"Chainspec hardfork label '{_jsonKey}' = 0x{labelValue.ToString("x", null)} conflicts with explicit {eip.Name} = 0x{current.Value.ToString("x", null)}. Either remove the conflicting field or align both values.",
                    ExitCodes.ConflictingChainspecEipConfiguration);
            }
        }
    }

    private static int ParseCanonicalEip(string propertyName)
    {
        Match m = EipPropertyPattern.Match(propertyName);
        if (!m.Success) throw new ArgumentException(
            $"Property name '{propertyName}' does not follow the Eip<N>[abc|d][Disable]Transition[Timestamp] convention.",
            nameof(propertyName));
        // Parity chainspecs split EIP-158 (state-clearing) into Eip161abc + Eip161d; both map to EIP-158.
        return m.Groups["split"].Success ? 158 : int.Parse(m.Groups["eip"].Value);
    }

    private static string ToCamelCase(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private readonly record struct EipAccessor(string Name, Func<IEipTransitionFields, T?> Read, Action<IEipTransitionFields, T?> Write);
}
