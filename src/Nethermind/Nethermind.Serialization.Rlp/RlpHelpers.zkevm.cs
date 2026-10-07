// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.Rlp;

internal static partial class RlpHelpers
{
    // Left to the compiler: with no call, the guest's byte-string decodes are 1.3M ziskemu steps cheaper.
    private const MethodImplOptions LargerByteArrayInlining = default;
}
