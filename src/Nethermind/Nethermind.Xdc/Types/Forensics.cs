// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System;
using System.Collections.Generic;

namespace Nethermind.Xdc.Types;

public class ForensicsInfo
{
    public IList<string> HashPath { get; set; } = [];
    public QuorumCertificate QuorumCert { get; set; } = null!;
    public IReadOnlyList<string> SignerAddresses { get; set; } = [];
}

public class ForensicsContent
{
    public ulong DivergingBlockNumber { get; set; }
    public string DivergingBlockHash { get; set; } = string.Empty;
    public bool AcrossEpoch { get; set; }
    public ForensicsInfo SmallerRoundInfo { get; set; } = null!;
    public ForensicsInfo LargerRoundInfo { get; set; } = null!;
}

public class VoteEquivocationContent
{
    public Vote SmallerRoundVote { get; set; } = null!;
    public Vote LargerRoundVote { get; set; } = null!;
    public Address Signer { get; set; }
}

public class ForensicProof
{
    public string Id { get; set; } = string.Empty;
    public string ForensicsType { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public class ForensicsEvent : EventArgs
{
    public ForensicProof ForensicsProof { get; set; } = null!;
}
