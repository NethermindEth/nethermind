// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.Rlp;

internal static partial class RlpHelpers
{
    // Cold path, kept out of line so the short-string case stays small.
    private const MethodImplOptions LargerByteArrayInlining = MethodImplOptions.NoInlining;
}
