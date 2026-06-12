// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Base class for all named hard-fork specs. Each fork declares a <see cref="Parent"/> (the
/// previous fork in the chain) and an <see cref="Apply"/> method that sets only the properties
/// that changed in that fork.
///
/// <para>
/// On construction the full ancestor chain is replayed from root to <c>this</c>:
/// <code>
///   Olympic.Apply(this) → Frontier.Apply(this) → … → ThisFork.Apply(this)
/// </code>
/// so the resulting instance carries the cumulative state of all preceding forks plus its own delta.
/// </para>
///
/// <para>
/// Gnosis forks (see <see cref="GnosisForks.NamedGnosisReleaseSpec"/>) override <see cref="Apply"/>
/// to call <c>mainnetFork.Apply(spec)</c> first, then add their own Gnosis-specific overrides.
/// Concrete Gnosis forks call <c>base.Apply(spec)</c> to get the mainnet delta.
/// </para>
/// </summary>
public abstract class NamedReleaseSpec : ReleaseSpec
{
    /// <summary>The preceding fork in the chain, or <c>null</c> for the root (Olympic).</summary>
    public NamedReleaseSpec? Parent { get; }

    /// <summary>
    /// Whether this fork is at or after the PoW → PoS merge. Set inside <see cref="Apply"/> on the
    /// merge-boundary forks (<see cref="Paris"/> on mainnet, <see cref="GnosisForks.ShanghaiGnosis"/>
    /// on Gnosis since it skipped a Paris-equivalent); descendants inherit because each ancestor's
    /// <see cref="Apply"/> is replayed onto them in <see cref="ReplayAncestors"/>.
    /// </summary>
    public bool IsPostMerge { get; set; }

    /// <summary>
    /// Engine API method versions surfaced by this fork. <c>null</c> on forks that don't expose the
    /// corresponding endpoint (e.g. pre-merge forks have no <c>engine_*</c> at all; Paris has no
    /// <c>getPayloadBodies*</c>). Set inside <see cref="Apply"/> only when this fork *changes* the
    /// value — values flow forward through the <see cref="ReplayAncestors"/> chain.
    /// </summary>
    public int? EngineApiNewPayloadVersion { get; set; }

    /// <inheritdoc cref="EngineApiNewPayloadVersion"/>
    public int? EngineApiGetPayloadVersion { get; set; }

    /// <inheritdoc cref="EngineApiNewPayloadVersion"/>
    public int? EngineApiForkchoiceVersion { get; set; }

    /// <inheritdoc cref="EngineApiNewPayloadVersion"/>
    public int? EngineApiPayloadBodiesByHashVersion { get; set; }

    /// <inheritdoc cref="EngineApiNewPayloadVersion"/>
    public int? EngineApiPayloadBodiesByRangeVersion { get; set; }

    /// <summary>
    /// URL <c>{fork}</c> segment for the SSZ-REST Engine API surface — the lowercase fork class name
    /// (e.g. <c>"paris"</c>). Whether the segment is actually routable is decided by the engine-API
    /// layer's supported-fork set, not here.
    /// </summary>
    public string? EngineApiUrlSegment => Name?.ToLowerInvariant();

    protected NamedReleaseSpec(NamedReleaseSpec? parent)
    {
        Parent = parent;
        ReplayAncestors(this);
    }

    /// <summary>
    /// Recursively walks the parent chain from root to <paramref name="fork"/>, calling
    /// <see cref="Apply"/> on each ancestor to accumulate their property changes
    /// into <c>this</c> instance (the spec being constructed).
    /// </summary>
    private void ReplayAncestors(NamedReleaseSpec? fork)
    {
        if (fork is null) return;
        ReplayAncestors(fork.Parent);
        fork.Apply(this);
    }

    /// <summary>
    /// Sets only this fork's own property changes on <paramref name="spec"/>.
    /// Each concrete fork overrides this to configure the EIPs and parameters it introduces.
    /// Gnosis forks should call <c>base.Apply(spec)</c> first to include the mainnet delta.
    /// </summary>
    public abstract void Apply(NamedReleaseSpec spec);
}

/// <summary>
/// Generic base that adds a per-fork-type singleton <see cref="Instance"/>.
/// Concrete forks inherit as: <c>class Cancun() : NamedReleaseSpec&lt;Cancun&gt;(Shanghai.Instance)</c>.
/// </summary>
public abstract class NamedReleaseSpec<TSelf>(NamedReleaseSpec? parent) : NamedReleaseSpec(parent)
    where TSelf : NamedReleaseSpec<TSelf>, new()
{
    public static NamedReleaseSpec Instance { get; } = new TSelf();
}

public static class NamedReleaseSpecExtensions
{
    /// <summary>
    /// True iff this fork changes at least one engine-API method version vs. its
    /// <see cref="NamedReleaseSpec.Parent"/>. Used to filter forks that only tweak per-fork
    /// constants (blob-parameter overrides etc.) and reuse the parent's engine-API surface.
    /// </summary>
    public static bool IntroducesEngineApiChange(this NamedReleaseSpec spec) =>
        spec.EngineApiNewPayloadVersion != spec.Parent?.EngineApiNewPayloadVersion
        || spec.EngineApiGetPayloadVersion != spec.Parent?.EngineApiGetPayloadVersion
        || spec.EngineApiForkchoiceVersion != spec.Parent?.EngineApiForkchoiceVersion
        || spec.EngineApiPayloadBodiesByHashVersion != spec.Parent?.EngineApiPayloadBodiesByHashVersion
        || spec.EngineApiPayloadBodiesByRangeVersion != spec.Parent?.EngineApiPayloadBodiesByRangeVersion;
}
