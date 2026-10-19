// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;

namespace Nethermind.Specs;

public static partial class ReleaseSpecExtensions
{
    /// <remarks>
    /// A zkVM guest only runs <see cref="ReleaseSpec"/>s. Without a decorator, <see cref="ReleaseSpec"/> is the only
    /// implementation of <see cref="IReleaseSpec"/> the guest's whole-program compiler sees, which lets it turn
    /// every <see cref="IReleaseSpec"/> call into a direct, inlinable call.
    /// </remarks>
    private static IReleaseSpec WithoutEip158ForSystemTransaction(IReleaseSpec spec) =>
        throw new NotSupportedException("A zkVM guest runs system transactions on a ReleaseSpec only.");
}
