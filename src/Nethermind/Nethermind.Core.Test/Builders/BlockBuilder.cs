// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.State.Proofs;

namespace Nethermind.Core.Test.Builders
{
    public class BlockBuilder : BuilderBase<Block>
    {
        public BlockBuilder()
        {
            BlockHeader header = Build.A.BlockHeader.TestObject;
            TestObjectInternal = new Block(header);
            header.Hash = TestObjectInternal.CalculateHash();
        }

        public BlockBuilder WithHeader(BlockHeader header)
        {
            TestObjectInternal = TestObjectInternal.WithReplacedHeader(header);
            return this;
        }

        public BlockBuilder WithNumber(long number)
        {
            TestObjectInternal.Header.Number = number;
            return this;
        }

        public BlockBuilder WithBaseFeePerGas(UInt256 baseFeePerGas)
        {
            TestObjectInternal.Header.BaseFeePerGas = baseFeePerGas;
            return this;
        }

        public BlockBuilder WithExtraData(byte[] extraData)
        {
            TestObjectInternal.Header.ExtraData = extraData;
            return this;
        }

        public BlockBuilder WithGasLimit(long gasLimit)
        {
            TestObjectInternal.Header.GasLimit = gasLimit;
            return this;
        }

        public BlockBuilder WithTimestamp(ulong timestamp)
        {
            TestObjectInternal.Header.Timestamp = timestamp;
            return this;
        }

        public BlockBuilder WithTransactions(int txCount, IReleaseSpec releaseSpec)
        {
            Transaction[] txs = new Transaction[txCount];
            for (int i = 0; i < txCount; i++)
            {
                txs[i] = new Transaction();
            }

            return WithTransactions(txs);
        }

        public BlockBuilder WithTransactions(int txCount, ISpecProvider specProvider)
        {
            Transaction[] txs = new Transaction[txCount];
            for (int i = 0; i < txCount; i++)
            {
                txs[i] = new Transaction();
            }

            TxReceipt[] receipts = new TxReceipt[txCount];
            for (int i = 0; i < txCount; i++)
            {
                receipts[i] = Build.A.Receipt.TestObject;
            }

            BlockBuilder result = WithTransactions(txs);
            ReceiptTrie receiptTrie = new(specProvider.GetSpec(TestObjectInternal.Header), receipts);
            receiptTrie.UpdateRootHash();
            TestObjectInternal.Header.ReceiptsRoot = receiptTrie.RootHash;
            return result;
        }

        public BlockBuilder WithTransactions(params Transaction[] transactions)
        {
            TestObjectInternal = TestObjectInternal.WithReplacedBody(
                TestObjectInternal.Body.WithChangedTransactions(transactions));
            TxTrie trie = new(transactions);
            trie.UpdateRootHash();

            TestObjectInternal.Header.TxRoot = trie.RootHash;
            return this;
        }

        public BlockBuilder WithTxRoot(Keccak txRoot)
        {
            TestObjectInternal.Header.TxRoot = txRoot;
            return this;
        }

        public BlockBuilder WithBeneficiary(Address address)
        {
            TestObjectInternal.Header.Beneficiary = address;
            return this;
        }

        public BlockBuilder WithPostMergeFlag(bool postMergeFlag)
        {
            TestObjectInternal.Header.IsPostMerge = postMergeFlag;
            return this;
        }

        public BlockBuilder WithTotalDifficulty(long difficulty)
        {
            TestObjectInternal.Header.TotalDifficulty = (ulong)difficulty;
            return this;
        }

        public BlockBuilder WithTotalDifficulty(UInt256? difficulty)
        {
            TestObjectInternal.Header.TotalDifficulty = difficulty;
            return this;
        }

        public BlockBuilder WithNonce(ulong nonce)
        {
            TestObjectInternal.Header.Nonce = nonce;
            return this;
        }

        public BlockBuilder WithMixHash(Keccak mixHash)
        {
            TestObjectInternal.Header.MixHash = mixHash;
            return this;
        }

        public BlockBuilder WithDifficulty(UInt256 difficulty)
        {
            TestObjectInternal.Header.Difficulty = difficulty;
            return this;
        }

        public BlockBuilder WithParent(BlockHeader blockHeader)
        {
            TestObjectInternal.Header.Number = blockHeader?.Number + 1 ?? 0;
            TestObjectInternal.Header.Timestamp = blockHeader?.Timestamp + 1 ?? 0;
            TestObjectInternal.Header.ParentHash = blockHeader is null ? Keccak.Zero : blockHeader.Hash;
            return this;
        }

        public BlockBuilder WithParent(Block block)
        {
            return WithParent(block.Header);
        }

        public BlockBuilder WithUncles(params Block[] uncles)
        {
            TestObjectInternal = TestObjectInternal.WithReplacedBody(
                TestObjectInternal.Body.WithChangedUncles(uncles.Select(o => o.Header).ToArray()));
            return this;
        }

        public BlockBuilder WithUncles(params BlockHeader[] uncles)
        {
            TestObjectInternal = TestObjectInternal.WithReplacedBody(
                TestObjectInternal.Body.WithChangedUncles(uncles));
            return this;
        }

        public BlockBuilder WithParentHash(Keccak parent)
        {
            TestObjectInternal.Header.ParentHash = parent;
            return this;
        }

        public BlockBuilder WithStateRoot(Keccak stateRoot)
        {
            TestObjectInternal.Header.StateRoot = stateRoot;
            return this;
        }

        public BlockBuilder WithWithdrawalsRoot(Keccak? withdrawalsRoot)
        {
            TestObjectInternal.Header.WithdrawalsRoot = withdrawalsRoot;

            return this;
        }

        public BlockBuilder WithBloom(Bloom bloom)
        {
            TestObjectInternal.Header.Bloom = bloom;
            return this;
        }

        public BlockBuilder WithAura(long step, byte[]? signature = null)
        {
            TestObjectInternal.Header.AuRaStep = step;
            TestObjectInternal.Header.AuRaSignature = signature;
            return this;
        }

        public BlockBuilder WithAuthor(Address? author)
        {
            TestObjectInternal.Header.Author = author;
            return this;
        }

        public BlockBuilder Genesis => WithNumber(0).WithParentHash(Keccak.Zero).WithMixHash(Keccak.Zero);

        protected override void BeforeReturn()
        {
            base.BeforeReturn();
            TestObjectInternal.Header.Hash = TestObjectInternal.Header.CalculateHash();
        }

        public BlockBuilder WithReceiptsRoot(Keccak keccak)
        {
            TestObjectInternal.Header.ReceiptsRoot = keccak;
            return this;
        }

        public BlockBuilder WithGasUsed(long gasUsed)
        {
            TestObjectInternal.Header.GasUsed = gasUsed;
            return this;
        }

        public BlockBuilder WithWithdrawals(int count)
        {
            var withdrawals = new Withdrawal[count];

            for (var i = 0; i < count; i++)
                withdrawals[i] = new();

            return WithWithdrawals(withdrawals);
        }

        public BlockBuilder WithWithdrawals(Withdrawal[]? withdrawals)
        {
            TestObjectInternal = TestObjectInternal
                .WithReplacedBody(TestObjectInternal.Body.WithChangedWithdrawals(withdrawals));

            TestObjectInternal.Header.WithdrawalsRoot = withdrawals is null
                ? null
                : new WithdrawalTrie(withdrawals).RootHash;

            return this;
        }
    }
}
