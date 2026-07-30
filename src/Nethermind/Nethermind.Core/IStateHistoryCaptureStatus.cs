// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Live health of archival state-history capture, for features whose safety depends on history actually being
/// recorded — not merely enabled in the config.
/// </summary>
/// <remarks>
/// Receipt derivation is the canonical consumer: it skips persisting receipt bodies on the promise that they can be
/// re-executed from state history. Capture can self-disable at runtime (a permanent gap, a reorged capture, repeated
/// write failures); from that moment a skipped body is permanently lost once its block leaves the in-memory tier, so
/// the skip must follow this signal rather than the config flag.
/// </remarks>
public interface IStateHistoryCaptureStatus
{
    /// <summary>Whether capture is enabled and has not self-disabled; when <c>false</c>, data whose recovery
    /// depends on future history must be persisted instead of skipped.</summary>
    bool CaptureHealthy { get; }
}
