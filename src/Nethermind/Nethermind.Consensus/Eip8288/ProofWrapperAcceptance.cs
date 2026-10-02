// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Eip8288;

public enum ProofWrapperAcceptanceStatus
{
    Invalid,
    Accepted,
    PoolRejected,
    Busy
}

/// <summary>A valid proof can cover transactions rejected by local pool policy.</summary>
public readonly record struct ProofWrapperAcceptance(ProofWrapperAcceptanceStatus Status, Result<Hash256[]> Result)
{
    public bool HasValidProof => Status is ProofWrapperAcceptanceStatus.Accepted or ProofWrapperAcceptanceStatus.PoolRejected;
    public static ProofWrapperAcceptance Invalid(string error) => new(ProofWrapperAcceptanceStatus.Invalid, Result<Hash256[]>.Fail(error));
}
