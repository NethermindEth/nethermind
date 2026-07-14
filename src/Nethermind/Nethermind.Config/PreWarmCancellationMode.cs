// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

/// <summary>
/// Experimental mid-EVM cancellation of exceptional warm jobs overtaken by canonical execution.
/// </summary>
public enum PreWarmCancellationMode
{
    /// <summary>No mid-EVM cancellation; warm execution runs the non-cancelable VM path.</summary>
    None,

    /// <summary>Cancel an eligible in-flight warm once canonical execution has passed its transaction.</summary>
    Passed,

    /// <summary>Cancel an eligible in-flight warm as soon as canonical execution reaches its transaction.</summary>
    Reached,
}
