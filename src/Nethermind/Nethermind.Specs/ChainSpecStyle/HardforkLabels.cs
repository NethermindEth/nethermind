// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;

namespace Nethermind.Specs.ChainSpecStyle;

/// <summary>
/// Shorthand hardfork labels for the Parity-style chainspec JSON and the EIP-7949 Geth-style
/// genesis. Each label is a single activation value — a block number for pre-Shanghai forks
/// (<c>ulong?</c>) and a timestamp for post-merge forks (<c>ulong?</c>) — that fans out at load
/// time to the full set of per-EIP transition fields that make up that fork.
/// </summary>
/// <remarks>
/// Labels are sourced from <see cref="IHasNamedForks"/> — populated by <c>[JsonExtensionData]</c>
/// on the Parity side and by typed <c>&lt;fork&gt;Time</c> / <c>&lt;fork&gt;Block</c> property
/// setters on the Geth side — and applied to an <see cref="ChainParameters"/> target. A
/// label and an explicit per-EIP field may coexist with matching values (redundant); conflicting
/// values are rejected at load.
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
#if ZK_EVM
    // The HardforkLabelsGenerator is not wired into the ZisK guest build (the guest builds its
    // spec from an embedded chain_config and never enumerates the label registry), so the
    // generated partial impl is absent there — stub it. Mainline keeps the source-generated
    // partial; replacing it with this stub unconditionally left HardforkLabels.All empty and
    // broke ChainSpec transition building (Eip4844/BlobSchedule), failing the .NET test suites.
    private static IReadOnlyList<IHardforkLabel> BuildAll() => System.Array.Empty<IHardforkLabel>();
#else
    private static partial IReadOnlyList<IHardforkLabel> BuildAll();
#endif

    /// <summary>
    /// Expands every hardfork label that <paramref name="source"/> carries — looked up in either
    /// <see cref="IHasNamedForks.NamedForkBlocks"/> or <see cref="IHasNamedForks.NamedForkTimestamps"/>
    /// depending on each label's <see cref="IHardforkLabel.Kind"/> — into the constituent per-EIP
    /// transition fields on <paramref name="target"/>. Throws
    /// <see cref="InvalidConfigurationException"/> when a label disagrees with an explicit per-EIP
    /// value already on the target.
    /// </summary>
    /// <remarks>
    /// Parity-style chainspecs pass the same instance for both parameters (it implements both
    /// interfaces). Geth-style genesis loaders pass the destination <see cref="ChainParameters"/>
    /// as target and the parsed config as source.
    /// </remarks>
    public static void ExpandAll(this ChainParameters target, IHasNamedForks source)
    {
        foreach (IHardforkLabel label in All) label.Apply(target, source);
    }

    /// <param name="name">Fork class name (e.g. <c>Homestead</c>); the JSON wire key is its camelCase form.</param>
    internal static HardforkLabel<ulong> Block(
        string name,
        params Expression<Func<ChainParameters, ulong?>>[] eips) =>
        new(name, HardforkLabelKind.Block,
            (s, k) => s.NamedForkBlocks is { } d && d.TryGetValue(k, out ulong v) ? v : null,
            eips);

    /// <inheritdoc cref="Block"/>
    internal static HardforkLabel<ulong> Time(
        string name,
        params Expression<Func<ChainParameters, ulong?>>[] eips) =>
        new(name, HardforkLabelKind.Timestamp,
            (s, k) => s.NamedForkTimestamps is { } d && d.TryGetValue(k, out ulong v) ? v : null,
            eips);
}

/// <summary>
/// Wire-format object that surfaces hardfork shorthand keys as strongly-typed lookups.
/// <see cref="HardforkLabels.ExpandAll"/> consumes recognized entries to populate the per-EIP
/// transition fields on an <see cref="ChainParameters"/> target.
/// </summary>
public interface IHasNamedForks
{
    /// <summary>Pre-merge fork → activation block number. Case-insensitive lookup.</summary>
    IReadOnlyDictionary<string, ulong>? NamedForkBlocks { get; }

    /// <summary>Post-merge fork → activation timestamp. Case-insensitive lookup.</summary>
    IReadOnlyDictionary<string, ulong>? NamedForkTimestamps { get; }
}

/// <summary>Whether a label fans out to block-number EIPs (<see cref="Block"/>) or timestamp EIPs (<see cref="Timestamp"/>).</summary>
public enum HardforkLabelKind { Block, Timestamp }

public interface IHardforkLabel
{
    /// <summary>Whether this label is read from <see cref="IHasNamedForks.NamedForkBlocks"/> or <see cref="IHasNamedForks.NamedForkTimestamps"/>.</summary>
    HardforkLabelKind Kind { get; }

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
    /// If <paramref name="source"/> exposes this label's key in the dictionary matching
    /// <see cref="Kind"/>, copies the value into each constituent EIP field on
    /// <paramref name="target"/> that is still unset. Throws
    /// <see cref="InvalidConfigurationException"/> when an EIP field is already set to a
    /// different value.
    /// </summary>
    void Apply(ChainParameters target, IHasNamedForks source);
}

internal sealed class HardforkLabel<T> : IHardforkLabel
    where T : struct, IEquatable<T>, IFormattable
{
    // Eip{N}[abc|d][Disable]Transition[Timestamp] — captures the EIP number and the Parity-split
    // marker so EipPropertyNames can be normalized to canonical EIPs in `Eips`.
    private static readonly Regex EipPropertyPattern =
        new(@"^Eip(?<eip>\d+)(?<split>abc|d)?(?:Disable)?Transition(?:Timestamp)?$", RegexOptions.Compiled);

    private readonly Func<IHasNamedForks, string, T?> _readValue;
    private readonly EipAccessor[] _eips;

    public HardforkLabelKind Kind { get; }
    public string LabelName { get; }
    public IReadOnlyList<string> EipPropertyNames { get; }
    public IReadOnlyList<int> Eips { get; }

    public HardforkLabel(
        string labelName,
        HardforkLabelKind kind,
        Func<IHasNamedForks, string, T?> readValue,
        Expression<Func<ChainParameters, T?>>[] eips)
    {
        LabelName = labelName;
        Kind = kind;
        _readValue = readValue;
        _eips = [.. eips.Select(static e => new EipAccessor(e.GetName(), e.Compile(), e.GetSetter()))];
        EipPropertyNames = [.. _eips.Select(static e => e.Name)];
        Eips = [.. EipPropertyNames.Select(ParseCanonicalEip).Distinct()];
    }

    public void Apply(ChainParameters target, IHasNamedForks source)
    {
        T? labelValue = _readValue(source, LabelName);
        if (labelValue is null) return;

        foreach (EipAccessor eip in _eips)
        {
            T? current = eip.Read(target);
            if (current is null)
            {
                eip.Write(target, labelValue.Value);
            }
            else if (!current.Value.Equals(labelValue.Value))
            {
                throw new InvalidConfigurationException(
                    $"Chainspec hardfork label '{LabelName}' = 0x{labelValue.Value.ToString("x", null)} conflicts with explicit {eip.Name} = 0x{current.Value.ToString("x", null)}. Either remove the conflicting field or align both values.",
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

    private readonly record struct EipAccessor(string Name, Func<ChainParameters, T?> Read, Action<ChainParameters, T?> Write);
}
