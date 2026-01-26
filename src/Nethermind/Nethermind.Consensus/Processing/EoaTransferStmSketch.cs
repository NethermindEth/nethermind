// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Sketch of a block-STM-style planner for EOA-to-EOA simple transfers.
/// This is analysis-only and does not change execution behavior.
/// </summary>
internal static class EoaTransferStmSketch
{
    internal readonly record struct Plan(
        int TotalTransactions,
        int SimpleTransfers,
        int IndependentTransfers,
        int DuplicateSenders,
        int DuplicateRecipients,
        int NonEoaSenders,
        int NonEoaRecipients
    );

    internal static Plan Analyze(Block block, IWorldState worldState, IReleaseSpec spec)
    {
        int total = block.Transactions.Length;
        if (total == 0)
        {
            return new Plan(0, 0, 0, 0, 0, 0, 0);
        }

        int simpleTransfers = 0;
        int nonEoaSenders = 0;
        int nonEoaRecipients = 0;
        HashSet<AddressAsKey> seenSenders = new();
        HashSet<AddressAsKey> seenRecipients = new();
        int duplicateSenders = 0;
        int duplicateRecipients = 0;

        for (int i = 0; i < total; i++)
        {
            Transaction tx = block.Transactions[i];
            if (!IsSimpleTransfer(tx, spec))
            {
                continue;
            }

            simpleTransfers++;

            Address sender = tx.SenderAddress!;
            Address recipient = tx.To!;

            if (!IsEoa(worldState, sender))
            {
                nonEoaSenders++;
            }

            if (!IsEoa(worldState, recipient))
            {
                nonEoaRecipients++;
            }

            if (!seenSenders.Add(sender))
            {
                duplicateSenders++;
            }

            if (!seenRecipients.Add(recipient))
            {
                duplicateRecipients++;
            }
        }

        int independentTransfers = simpleTransfers - duplicateSenders - duplicateRecipients - nonEoaSenders - nonEoaRecipients;
        if (independentTransfers < 0) independentTransfers = 0;

        return new Plan(
            total,
            simpleTransfers,
            independentTransfers,
            duplicateSenders,
            duplicateRecipients,
            nonEoaSenders,
            nonEoaRecipients
        );
    }

    private static bool IsSimpleTransfer(Transaction tx, IReleaseSpec spec)
    {
        if (tx.IsContractCreation || tx.To is null)
        {
            return false;
        }

        if (tx.DataLength != 0)
        {
            return false;
        }

        if (tx.SupportsBlobs)
        {
            return false;
        }

        if (tx.IsSystem())
        {
            return false;
        }

        if (spec.IsPrecompile(tx.To))
        {
            return false;
        }

        return true;
    }

    private static bool IsEoa(IWorldState worldState, Address address) =>
        worldState.GetCodeHash(address) == Keccak.OfAnEmptyString.ValueHash256;
}
