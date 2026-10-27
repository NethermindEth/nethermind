// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

public enum ProofWrapperAcceptanceStatus
{
    /// <summary>A proof or dependency-rule failure: EIP-8437 discards the wrapper without penalizing its sender.</summary>
    Invalid,
    /// <summary>A malformed or noncanonical encoding, including fork-independent count, length and ordering violations.</summary>
    Malformed,
    Accepted,
    PoolRejected,
    Busy,
    LocalFailure,
    /// <summary>A wrapper this client does not support, such as one carrying blobs: refused locally, never invalid.</summary>
    Refused
}

/// <summary>A valid proof can cover transactions rejected by local pool policy.</summary>
public readonly record struct ProofWrapperAcceptance(ProofWrapperAcceptanceStatus Status, Result<Hash256[]> Result)
{
    /// <summary>For <see cref="ProofWrapperAcceptanceStatus.Invalid"/>, the target fork's verifier the wrapper was judged under;
    /// another verifier may accept it.</summary>
    public Eip8288Verifier? Verifier { get; init; }

    public bool HasValidProof => Status is ProofWrapperAcceptanceStatus.Accepted or ProofWrapperAcceptanceStatus.PoolRejected;
    public static ProofWrapperAcceptance Invalid(string error, Eip8288Verifier verifier) =>
        new(ProofWrapperAcceptanceStatus.Invalid, Result<Hash256[]>.Fail(error)) { Verifier = verifier };
    public static ProofWrapperAcceptance Malformed(string error) => new(ProofWrapperAcceptanceStatus.Malformed, Result<Hash256[]>.Fail(error));
    public static ProofWrapperAcceptance LocalFailure(string error) => new(ProofWrapperAcceptanceStatus.LocalFailure, Result<Hash256[]>.Fail(error));
    public static ProofWrapperAcceptance Refused(string error) => new(ProofWrapperAcceptanceStatus.Refused, Result<Hash256[]>.Fail(error));
}
