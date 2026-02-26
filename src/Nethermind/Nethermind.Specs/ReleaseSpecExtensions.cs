// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Specs;

public static class ReleaseSpecExtensions
{
    public static SpecSnapshot GetSnapshot(this IReleaseSpec spec) =>
        spec is ReleaseSpec rs ? rs.Snapshot : new SpecSnapshot(spec);

    public static IReleaseSpec ForSystemTransaction(this IReleaseSpec spec, bool isAura, bool isGenesis) =>
        spec switch
        {
            ReleaseSpec releaseSpec => releaseSpec.SystemSpec,
            { IsEip158Enabled: false } => spec,
            _ when !isAura && isGenesis => spec,
            _ => new SystemTransactionSpec(spec)
        };

    /// <summary>
    /// Fallback decorator for non-ReleaseSpec implementations (e.g. OverridableReleaseSpec in tests).
    /// </summary>
    private sealed class SystemTransactionSpec(IReleaseSpec spec) : ReleaseSpecDecorator(spec)
    {
        public override bool IsEip158Enabled => false;
    }
}
