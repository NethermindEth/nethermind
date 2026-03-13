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
/// Gnosis forks (see <see cref="GnosisForks.NamedGnosisReleaseSpec"/>) extend this by overriding
/// <see cref="ApplyDelta"/> to inject the corresponding mainnet fork's delta before their own
/// <see cref="Apply"/>, since their parent chain diverges from mainnet at an earlier point.
/// </para>
/// </summary>
public abstract class NamedReleaseSpec : ReleaseSpec
{
    /// <summary>The preceding fork in the chain, or <c>null</c> for the root (Olympic).</summary>
    public NamedReleaseSpec? Parent { get; }

    protected NamedReleaseSpec(NamedReleaseSpec? parent)
    {
        Parent = parent;
        ReplayAncestors(this);
    }

    /// <summary>
    /// Recursively walks the parent chain from root to <paramref name="fork"/>, calling
    /// <see cref="ApplyDelta"/> on each ancestor to accumulate their property changes
    /// into <c>this</c> instance (the spec being constructed).
    /// </summary>
    private void ReplayAncestors(NamedReleaseSpec? fork)
    {
        if (fork is null) return;
        ReplayAncestors(fork.Parent);
        fork.ApplyDelta(this);
    }

    /// <summary>
    /// Applies this fork's full contribution to <paramref name="target"/>.
    ///
    /// <para>
    /// For mainnet forks, this simply delegates to <see cref="Apply"/>.
    /// Gnosis forks override this to first apply the corresponding mainnet fork's delta
    /// (via <c>mainnetFork.Apply(target)</c>), then their own <see cref="Apply"/>.
    /// This is needed because a Gnosis fork's <see cref="Parent"/> points to the previous
    /// Gnosis fork (or a mainnet fork before the divergence point), not to the mainnet fork
    /// it corresponds to — so the mainnet delta would otherwise be missing.
    /// </para>
    /// </summary>
    protected virtual void ApplyDelta(ReleaseSpec target) => Apply(target);

    /// <summary>
    /// Sets only this fork's own property changes on <paramref name="spec"/>.
    /// Each concrete fork overrides this to configure the EIPs and parameters it introduces.
    /// </summary>
    public abstract void Apply(ReleaseSpec spec);
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
