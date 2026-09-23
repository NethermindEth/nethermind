// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

/// <summary>The logical processors block processing runs on, on an Intel hybrid CPU.</summary>
public enum ProcessingCores
{
    /// <summary>Any the process may run on.</summary>
    All,

    /// <summary>Both hyperthreads of every performance core.</summary>
    Performance,

    /// <summary>One hyperthread of every performance core.</summary>
    PerformancePhysical,
}
