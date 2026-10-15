// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>The canonical hash-only EIP-8288 wrapper body carried by EIP-8437, checked before wrapper validation.</summary>
public static class LeanBodies
{
    /// <summary>Checks the hash-only EIP-8288 wrapper encoding before envelope recovery.</summary>
    public static List<ValueHash256> ParseWrapper(ReadOnlySpan<byte> body) => ParseWrapper(body, out _);

    internal static List<ValueHash256> ParseWrapper(ReadOnlySpan<byte> body, out int verificationBytes)
    {
        verificationBytes = 0;
        if (body.Length > Eip8288Constants.MaxWrapperBytes) throw new RlpException("Wrapper exceeds MAX_WRAPPER_BYTES");
        LeanRlpReader outer = new(body);
        LeanRlpReader wrapper = outer.ReadList();
        outer.End();
        LeanRlpReader transactions = wrapper.ReadList();
        int count = transactions.CountRemaining(Eip8288Constants.MaxTxsPerWrapper);
        if (count == 0) throw new RlpException("Wrapper carries no transactions");
        List<ValueHash256> hashes = new(count);
        for (int i = 0; i < count; i++)
        {
            ValueHash256 hash = transactions.ReadHash();
            if (i > 0 && hashes[i - 1].CompareTo(hash) >= 0) throw new RlpException("Wrapper hashes are not strictly ascending");
            hashes.Add(hash);
        }
        transactions.End();
        ulong mode = wrapper.ReadUInt64();
        if (mode > 1) throw new RlpException("Unknown wrapper mode");
        LeanRlpReader content = wrapper.ReadList();
        wrapper.End();
        LeanRlpReader dependencies = content.ReadList();
        int dependencyLimit = mode == 0
            ? Eip8288Constants.MaxLeanSigDepsPerWrapper + Eip8288Constants.MaxLeanStarkDepsPerWrapper
            : Eip8288Constants.MaxDepsPerAggregate;
        int dependencyCount = dependencies.CountRemaining(dependencyLimit);
        if (dependencyCount == 0) throw new RlpException("Wrapper dependencies are empty");
        ReadOnlySpan<byte> previous = default;
        for (int i = 0; i < dependencyCount; i++)
        {
            ReadOnlySpan<byte> dependency = dependencies.ReadBytes();
            if (dependency.Length != Eip8288Constants.DependencyTripleLength || !dependency[..31].IsZero()
                || !Eip8288Dependencies.IsAcceptedScheme(dependency[31])) throw new RlpException("Invalid dependency");
            if (i > 0 && previous.SequenceCompareTo(dependency) >= 0) throw new RlpException("Dependencies are not canonical");
            previous = dependency;
        }
        if (mode == 0)
        {
            LeanRlpReader proofs = content.ReadList();
            if (proofs.CountRemaining(dependencyLimit) != dependencyCount) throw new RlpException("Witness count mismatch");
            while (proofs.HasMore) verificationBytes += proofs.ReadBytes().Length;
        }
        else if (mode == 1)
        {
            ReadOnlySpan<byte> proof = content.ReadBytes();
            CheckAggregateProof(proof);
            verificationBytes = proof.Length;
        }
        else throw new RlpException("Unknown wrapper mode");
        content.End();
        return hashes;
    }

    /// <summary>EIP-8288 applies the fork's <c>MAX_AGGREGATE_PROOF_BYTES</c> to every aggregate proof before proof decoding or
    /// verification; the transport's object ceiling does not replace it.</summary>
    private static void CheckAggregateProof(ReadOnlySpan<byte> proof)
    {
        if (proof.Length > Eip8288Constants.MaxAggregateProofBytes) throw new RlpException("Aggregate proof exceeds MAX_AGGREGATE_PROOF_BYTES");
    }
}
