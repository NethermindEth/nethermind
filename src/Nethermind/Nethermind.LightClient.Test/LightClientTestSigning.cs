// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.LightClient.Test;

internal static class LightClientTestSigning
{
    internal static SyncAggregate SignHeader(BeaconBlockHeader header, ulong domainSlot, byte key, int participants, BeaconChainSpec spec)
    {
        Hash256 domain = Domains.ComputeDomain(DomainType.SyncCommittee, spec.VersionForEpoch(spec.GetEpoch(domainSlot)), spec.GenesisValidatorsRoot);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(header), domain);
        BlsSigner.Signature signature = new(new Bls.P2(new long[Bls.P2.Sz]));
        BlsSigner.Signature memberSignature = BlsSigner.Sign(Key(key), signingRoot.Bytes);
        BitArray bits = new(Presets.SyncCommitteeSize);
        for (int i = 0; i < participants; i++)
        {
            bits[i] = true;
            signature.Aggregate(memberSignature);
        }
        return new SyncAggregate { SyncCommitteeBits = bits, SyncCommitteeSignature = new BlsSignature(signature.Bytes) };
    }

    internal static Bls.SecretKey Key(byte key)
    {
        byte[] bytes = new byte[32];
        bytes[0] = key;
        return new Bls.SecretKey(bytes, Bls.ByteOrder.LittleEndian);
    }
}
