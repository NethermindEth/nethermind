// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// Executes a <see cref="DerivedBlock"/> on its parent's state and fills in what execution decides: the roots, the
/// gas, the bloom and the hash. The processor must execute transactions strictly: a derived transaction that is
/// invalid on the parent's state fails the block rather than being left out of it.
/// </summary>
public sealed class DerivedBlockBuilder(ISpecProvider specProvider, EezSettlementContext context)
{
    private readonly EthereumEcdsa _ecdsa = new(specProvider.ChainId);

    /// <exception cref="EezSettlementException">A transaction does not decode, has no sender, or does not execute on the parent's state.</exception>
    public (Block Block, TxReceipt[] Receipts) Build(BlockHeader parent, DerivedBlock derived, IBlockProcessor processor, IWorldState worldState)
    {
        IReleaseSpec spec = specProvider.GetSpec(parent.Number + 1, DerivedHeader.Timestamp(parent, context));
        BlockHeader header = DerivedHeader.Build(parent, spec, context, derived.Beneficiary, derived.ExtraData);
        Transaction[] transactions = Decode(derived.Transactions, header, spec);
        Withdrawal[]? withdrawals = DerivedHeader.Withdrawals(spec);
        header.TxRoot = TxTrie.CalculateRoot(transactions);
        header.WithdrawalsRoot = withdrawals is null ? null : WithdrawalTrie.CalculateRoot(withdrawals);
        Block block = new(header, transactions, [], withdrawals);

        if (!worldState.TryBeginScope(parent, out IDisposable? scope))
        {
            throw new EezSettlementException(EezSettlementFailure.InternalInvariant, $"The state of block {parent.Number} is not available.");
        }

        using (scope)
        {
            try
            {
                return processor.ProcessOne(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance, spec);
            }
            catch (InvalidBlockException e)
            {
                throw new EezSettlementException($"Derived block {header.Number} does not execute: {e.Message}");
            }
        }
    }

    private Transaction[] Decode(byte[][] encoded, BlockHeader header, IReleaseSpec spec)
    {
        Transaction[] transactions = new Transaction[encoded.Length];
        for (int i = 0; i < encoded.Length; i++)
        {
            Transaction transaction;
            try
            {
                transaction = TxDecoder.Instance.Decode(encoded[i], RlpBehaviors.SkipTypedWrapping)!;
            }
            catch (RlpException e)
            {
                throw new EezSettlementException(EezSettlementFailure.InvalidDaPayload, $"Transaction {i} of derived block {header.Number} does not decode: {e.Message}");
            }

            if (!transaction.IsEezSystemTransaction())
            {
                if (!_ecdsa.TryRecoverAddress(transaction, out Address? sender, !spec.ValidateChainId))
                {
                    throw new EezSettlementException(EezSettlementFailure.InvalidDaPayload, $"Transaction {i} of derived block {header.Number} has no recoverable sender.");
                }

                transaction.SenderAddress = sender;
            }

            transactions[i] = transaction;
        }

        return transactions;
    }
}
