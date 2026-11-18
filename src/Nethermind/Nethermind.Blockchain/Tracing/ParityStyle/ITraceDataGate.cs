// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Tracing.ParityStyle;

/// <summary>
/// A limit that the <see cref="TraceDataBudget"/>s of several tracers share on top of their own: each budget passes it
/// every byte it charges and gives back.
/// </summary>
public interface ITraceDataGate
{
    /// <summary>
    /// Counts <paramref name="bytes"/> that a budget charged. When they do not fit and <paramref name="mayWait"/> is set,
    /// it may first wait for room. It counts them also when it throws, so that giving them back balances it.
    /// </summary>
    /// <param name="mayWait">Unset for a charge that fails anyway, as it passes the budget's own limit.</param>
    void Charge(long bytes, bool mayWait);

    /// <summary>Counts <paramref name="bytes"/> only when they fit, without waiting.</summary>
    /// <returns>Whether the bytes were counted.</returns>
    bool TryCharge(long bytes);

    /// <summary>Gives back <paramref name="bytes"/> that a budget counted.</summary>
    void Release(long bytes);
}
