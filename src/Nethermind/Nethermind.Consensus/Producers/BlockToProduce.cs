// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

//TODO: Redo clique block producer
[assembly: InternalsVisibleTo("Nethermind.Consensus.Clique")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Consensus.Producers
{
    [DebuggerDisplay("{Hash} ({Number})")]
    public class BlockToProduce : Block
    {
        private IEnumerable<Transaction>? _transactions;

        public new IEnumerable<Transaction> Transactions
        {
            get => _transactions ?? base.Transactions;
            set
            {
                _transactions = value;
                if (_transactions is Transaction[] transactionsArray)
                {
                    base.Transactions = transactionsArray;
                }
            }
        }

        public BlockToProduce(BlockHeader header) : base(
        header,
        new(
            null,
            null,
            header.WithdrawalsRoot is null ? null : [])
        )
        { }

        public BlockToProduce(BlockHeader blockHeader,
            IEnumerable<Transaction> transactions,
            IEnumerable<BlockHeader> uncles,
            IEnumerable<Withdrawal>? withdrawals = null)
            : base(blockHeader, Array.Empty<Transaction>(), uncles, withdrawals) => Transactions = transactions;

        public long TxByteLength { get; internal set; }

        /// <summary>EIP-8288 <c>recursive_stark_gas</c> owed by the transactions selected so far.</summary>
        /// <remarks>Charged to the header only after execution, so selection must hold it back itself.</remarks>
        public ulong RecursiveStarkGas { get; internal set; }

        internal List<AggregationInput> LeanProofInputs { get; } = [];
        internal List<FrameDependency> LeanDependencies { get; } = [];
        internal long LeanWitnessBytes { get; set; }

        public override Block WithReplacedHeader(BlockHeader newHeader)
        {
            BlockToProduce replacement = new(newHeader, Transactions, Uncles, Withdrawals)
            {
                InclusionListTransactions = InclusionListTransactions,
                InclusionListRecursiveStark = InclusionListRecursiveStark,
                TxByteLength = TxByteLength,
                RecursiveStarkGas = RecursiveStarkGas,
                LeanWitnessBytes = LeanWitnessBytes
            };
            replacement.LeanProofInputs.AddRange(LeanProofInputs);
            replacement.LeanDependencies.AddRange(LeanDependencies);
            return replacement;
        }
    }
}
