// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Crypto;

/// <summary>
/// zkEVM guest variant of the log topic table: every topic gets its own instance, so the call sites stay
/// unconditional and the guest allocates exactly as it did before the table.
/// </summary>
/// <remarks>
/// The guest executes one block on one thread and exits, so a shared instance is rarely reused, while each
/// lookup would add a software hash of the topic, a 32-byte compare and a slot write.
/// </remarks>
internal static partial class LogTopicCache
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Hash256 Get(ReadOnlySpan<byte> topic) => new(topic);
}
