// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.BeaconChain.Spec;

/// <summary>BLS signature domain types as defined in consensus-specs.</summary>
public static class DomainType
{
    public static ReadOnlySpan<byte> BeaconProposer => [0x00, 0x00, 0x00, 0x00];
    public static ReadOnlySpan<byte> BeaconAttester => [0x01, 0x00, 0x00, 0x00];
    public static ReadOnlySpan<byte> Randao => [0x02, 0x00, 0x00, 0x00];
    public static ReadOnlySpan<byte> Deposit => [0x03, 0x00, 0x00, 0x00];
    public static ReadOnlySpan<byte> VoluntaryExit => [0x04, 0x00, 0x00, 0x00];
    public static ReadOnlySpan<byte> SelectionProof => [0x05, 0x00, 0x00, 0x00];
    public static ReadOnlySpan<byte> AggregateAndProof => [0x06, 0x00, 0x00, 0x00];
    public static ReadOnlySpan<byte> SyncCommittee => [0x07, 0x00, 0x00, 0x00];
    public static ReadOnlySpan<byte> SyncCommitteeSelectionProof => [0x08, 0x00, 0x00, 0x00];
    public static ReadOnlySpan<byte> ContributionAndProof => [0x09, 0x00, 0x00, 0x00];
    public static ReadOnlySpan<byte> BlsToExecutionChange => [0x0A, 0x00, 0x00, 0x00];

    // Gloas (specs/gloas/beacon-chain.md "Constants/Domains", fetched from consensus-specs master 2026-09-19)
    public static ReadOnlySpan<byte> BeaconBuilder => [0x0B, 0x00, 0x00, 0x00];
    /// <summary>Used to seed the payload timeliness committee shuffling (<c>compute_ptc</c>).</summary>
    public static ReadOnlySpan<byte> PtcAttester => [0x0C, 0x00, 0x00, 0x00];
    public static ReadOnlySpan<byte> ProposerPreferences => [0x0D, 0x00, 0x00, 0x00];
    public static ReadOnlySpan<byte> BuilderDeposit => [0x0E, 0x00, 0x00, 0x00];
}

/// <summary>Signing domain and signing root computation per consensus-specs.</summary>
public static class Domains
{
    public static Hash256 ComputeForkDataRoot(byte[] currentVersion, Hash256 genesisValidatorsRoot)
    {
        ForkData.Merkleize(new ForkData { CurrentVersion = currentVersion, GenesisValidatorsRoot = genesisValidatorsRoot }, out UInt256 root);
        return new Hash256(root.ToLittleEndian());
    }

    /// <summary>Computes the 32-byte signing domain: domain type + first 28 bytes of the fork data root.</summary>
    public static Hash256 ComputeDomain(ReadOnlySpan<byte> domainType, byte[] forkVersion, Hash256 genesisValidatorsRoot)
    {
        Hash256 forkDataRoot = ComputeForkDataRoot(forkVersion, genesisValidatorsRoot);
        Span<byte> domain = stackalloc byte[32];
        domainType.CopyTo(domain);
        forkDataRoot.Bytes[..28].CopyTo(domain[4..]);
        return new Hash256(domain);
    }

    public static Hash256 ComputeSigningRoot(Hash256 objectRoot, Hash256 domain)
    {
        SigningData.Merkleize(new SigningData { ObjectRoot = objectRoot, Domain = domain }, out UInt256 root);
        return new Hash256(root.ToLittleEndian());
    }
}
