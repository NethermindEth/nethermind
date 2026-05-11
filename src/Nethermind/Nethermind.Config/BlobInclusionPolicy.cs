// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

/// <summary>
/// Controls which blob transactions local block production may include.
/// </summary>
public enum BlobInclusionPolicy
{
    /// <summary>Include only blob transactions with all blob data fully available locally.</summary>
    Conservative,

    /// <summary>Also include sparse blob transactions that have validated sampled cells locally.</summary>
    Optimistic,

    /// <summary>Use optimistic local inclusion criteria and future proposer-duty resampling hooks when available.</summary>
    Proactive
}
