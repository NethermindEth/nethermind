// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// Live health of archival state-history capture, for features whose safety depends on history actually being
/// recorded — not merely enabled in the config.
/// </summary>
public interface IStateHistoryCaptureStatus
{
    /// <summary>Whether capture is enabled and has not self-disabled; when <c>false</c>, data whose recovery
    /// depends on future history must be persisted instead of skipped.</summary>
    bool CaptureHealthy { get; }

    /// <summary>Raised when captured history becomes crash-durable up to a block (the new contiguous watermark):
    /// retained data whose recovery depends on history may be dropped for blocks at or below it.</summary>
    event Action<ulong>? WatermarkAdvanced;

    /// <summary>Raised when capture permanently stops (see <see cref="CaptureHealthy"/>), before the pending state
    /// persist resumes: data retained for blocks above the watermark must be persisted by the handler — after the
    /// persist prunes those blocks' snapshots there is no other recovery source.</summary>
    event Action? CaptureDisabled;
}
