// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Numerics;

namespace Nethermind.Xdc.RPC;

/// <summary>Status and stake of a single masternode candidate at an epoch checkpoint.</summary>
public class XdcCandidateInfo
{
    /// <summary>One of <c>MASTERNODE</c>, <c>PROPOSED</c> or <c>SLASHED</c>; empty when the address is neither.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Stake backing the candidate, in Wei, or <c>-1</c> when the address is a masternode for the epoch but no
    /// longer appears in the candidate list, so no stake can be read for it.
    /// </summary>
    public BigInteger Capacity { get; set; }
}

/// <summary>Candidate set of one epoch, keyed by candidate address.</summary>
public class XdcCandidatesResult
{
    /// <summary>Epoch the candidate set describes.</summary>
    public long Epoch { get; set; }

    /// <summary>Whether the epoch could be classified; <see langword="false"/> leaves <see cref="Candidates"/> unset.</summary>
    public bool Success { get; set; }

    /// <summary>Candidates keyed by address.</summary>
    public Dictionary<string, XdcCandidateInfo>? Candidates { get; set; }
}

/// <summary>Status of a single candidate within one epoch.</summary>
public class XdcCandidateStatusResult
{
    /// <inheritdoc cref="XdcCandidatesResult.Epoch"/>
    public long Epoch { get; set; }

    /// <summary>Whether the epoch could be classified; <see langword="false"/> leaves the status empty.</summary>
    public bool Success { get; set; }

    /// <inheritdoc cref="XdcCandidateInfo.Status"/>
    public string Status { get; set; } = string.Empty;

    /// <inheritdoc cref="XdcCandidateInfo.Capacity"/>
    public BigInteger Capacity { get; set; }
}
