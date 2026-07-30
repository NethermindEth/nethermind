// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Live health of archival state-history capture, for features whose safety depends on history actually being
/// recorded — not merely enabled in the config.
/// </summary>
/// <remarks>
/// Capture can self-disable at runtime (permanent gap, reorged capture, repeated write failures); consumers that
/// skip persisting data on the promise of later re-execution must follow this signal, not the config flag.
/// </remarks>
public interface IStateHistoryCaptureStatus
{
    /// <summary>Whether capture is enabled and has not self-disabled; when <c>false</c>, data whose recovery
    /// depends on future history must be persisted instead of skipped.</summary>
    bool CaptureHealthy { get; }
}
