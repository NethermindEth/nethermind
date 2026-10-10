// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core.Threading;

/// <summary>
/// No-op counter for the zkVM guest — see the std counterpart for the striped one.
/// </summary>
/// <remarks>
/// Every use is a metrics counter the guest never reads, and the guest is single-threaded, so the
/// striping the std type exists for buys nothing. Empty bodies inline away, taking the surrounding
/// <c>Metrics.Increment*</c> calls with them.
/// </remarks>
public sealed partial class StripedLong
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(long value) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment() { }

    public long Sum => 0;
}
