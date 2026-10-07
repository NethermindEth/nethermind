// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Proving;
using Nethermind.Logging;

namespace Nethermind.Eez.Sequencer;

/// <summary>The check a batch passes before any attester is asked to sign it.</summary>
public interface IBatchCheck
{
    /// <param name="attesters">The configured attesters, in the order <paramref name="registration"/> keys them.</param>
    /// <exception cref="EezSettlementException">The batch claims something the span does not show.</exception>
    /// <exception cref="EezStatelessException">The span does not re-execute from its witnesses.</exception>
    /// <exception cref="ProveException">No configured attester is registered.</exception>
    void Check(ProveRequest request, QuorumRegistration registration, IReadOnlyList<IAttester> attesters);
}

/// <summary>
/// Runs the attesters' own check on the batch the first registered attester is sent, so a batch nobody can attest is
/// caught here. It re-executes the span statelessly from the same witnesses the attesters get.
/// </summary>
public sealed class LocalSettlementCheck(ISpecProvider specProvider, EezSettlementContext context, ILogManager logManager) : IBatchCheck
{
    public void Check(ProveRequest request, QuorumRegistration registration, IReadOnlyList<IAttester> attesters)
    {
        int first = Array.FindIndex(registration.VerificationKeys, static k => k != default);
        if (first < 0)
        {
            throw new ProveException(ProveFailureKind.Backend, "No configured attester is registered on the rollup manager.");
        }

        IAttester attester = attesters[first];
        ProveRequest own = request.For(attester.ProofSystem);
        SettlementCheck check = new(specProvider, context with { ProofSystem = attester.ProofSystem, VerificationKey = registration.VerificationKeys[first] }, logManager);
        EezStatelessBlock[] statelessBlocks = new EezStatelessBlock[own.Blocks.Count];
        (Hash256, Hash256)[] claims = new (Hash256, Hash256)[own.Blocks.Count];
        for (int i = 0; i < statelessBlocks.Length; i++)
        {
            statelessBlocks[i] = new EezStatelessBlock(own.Blocks[i].Rlp, own.Blocks[i].Witness);
            claims[i] = (own.Blocks[i].Hash, own.Blocks[i].ParentHash);
        }

        byte[] calldata = EezCalldata.EncodePostAndVerifyBatch(own.Batch);
        check.Verify(calldata, check.Execute(calldata, statelessBlocks, claims, own.FromBlock));
    }
}
