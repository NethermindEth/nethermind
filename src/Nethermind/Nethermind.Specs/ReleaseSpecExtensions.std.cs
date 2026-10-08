// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Specs;

public static partial class ReleaseSpecExtensions
{
    private static IReleaseSpec WithoutEip158ForSystemTransaction(IReleaseSpec spec) => new SystemTransactionSpec(spec);

    /// <summary>
    /// Fallback decorator for non-ReleaseSpec implementations (e.g. OverridableReleaseSpec in tests).
    /// </summary>
    private sealed class SystemTransactionSpec(IReleaseSpec spec) : ReleaseSpecDecorator(spec)
    {
        public override bool IsEip158Enabled => false;
    }
}
