// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Deposits
{
    public class TransactionVerifierTests
    {
        private ITransactionVerifier _transactionVerifier;
        private INdmBlockchainBridge _blockchainBridge;
        private uint _requiredBlockConfirmations;

        [SetUp]
        public void Setup()
        {
            _blockchainBridge = Substitute.For<INdmBlockchainBridge>();
            _requiredBlockConfirmations = 2;
            _transactionVerifier = new TransactionVerifier(_blockchainBridge, _requiredBlockConfirmations);
        }

        [Test]
        public async Task verify_async_should_return_result_with_confirmed_property_equal_to_false_if_latest_block_is_null()
        {
            NdmTransaction transaction = GetTransaction();
            TransactionVerifierResult result = await _transactionVerifier.VerifyAsync(transaction);
            result.BlockFound.Should().BeFalse();
            result.Confirmed.Should().BeFalse();
            await _blockchainBridge.Received().GetLatestBlockAsync();
        }

        [Test]
        public async Task verify_async_should_return_result_with_confirmed_property_equal_to_false_if_block_was_not_found_and_required_number_of_confirmations_was_not_achieved()
        {
            Block block = GetBlock();
            NdmTransaction transaction = GetTransaction();
            _blockchainBridge.GetLatestBlockAsync().Returns(block);
            TransactionVerifierResult result = await _transactionVerifier.VerifyAsync(transaction);
            result.BlockFound.Should().BeTrue();
            result.Confirmed.Should().BeFalse();
            await _blockchainBridge.Received().GetLatestBlockAsync();
            await _blockchainBridge.Received().FindBlockAsync(block.ParentHash);
        }

        [Test]
        public async Task verify_async_should_return_result_with_confirmed_property_equal_to_false_if_block_hash_is_same_as_tx_hash_and_required_number_of_confirmations_was_not_achieved()
        {
            Block block = GetBlock();
            NdmTransaction transaction = GetTransaction();
            block.Header.Hash = transaction.BlockHash;
            _blockchainBridge.GetLatestBlockAsync().Returns(block);
            TransactionVerifierResult result = await _transactionVerifier.VerifyAsync(transaction);
            result.BlockFound.Should().BeTrue();
            result.Confirmed.Should().BeFalse();
            await _blockchainBridge.Received().GetLatestBlockAsync();
            await _blockchainBridge.DidNotReceive().FindBlockAsync(block.ParentHash);
        }

        [Test]
        public async Task verify_async_should_return_result_with_confirmed_property_equal_to_true_if_required_number_of_confirmations_is_achieved()
        {
            NdmTransaction transaction = GetTransaction();
            Block block = GetBlock();
            Block parentBlock = GetBlock();
            _blockchainBridge.GetLatestBlockAsync().Returns(block);
            _blockchainBridge.FindBlockAsync(block.ParentHash).Returns(parentBlock);
            TransactionVerifierResult result = await _transactionVerifier.VerifyAsync(transaction);
            result.BlockFound.Should().BeTrue();
            result.Confirmed.Should().BeTrue();
            result.Confirmations.Should().Be(2);
            result.RequiredConfirmations.Should().Be(2);
            await _blockchainBridge.Received().GetLatestBlockAsync();
            await _blockchainBridge.Received(2).FindBlockAsync(block.ParentHash);
        }

        private static NdmTransaction GetTransaction(long blockNumber = 1, Keccak blockHash = null)
            => new NdmTransaction(null, true, blockNumber, blockHash, 0);

        private static Block GetBlock() => Build.A.Block.WithNumber(2).TestObject;
    }
}
