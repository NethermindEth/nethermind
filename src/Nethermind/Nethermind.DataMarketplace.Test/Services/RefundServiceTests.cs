//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Refunds.Services;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Int256;
using Nethermind.Evm;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class RefundServiceTests : ContractInteractionTest
    {
        private IDepositDetailsRepository _depositRepository;
        
        [SetUp]
        public async Task Setup()
        {
            await Prepare();
            _depositRepository = Substitute.For<IDepositDetailsRepository>();
        }

        [Test]
        public async Task can_claim_refund()
        {
            uint timestamp = 1546871954;
            _bridge.NextBlockPlease(timestamp);
            
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Keccak assetId = Keccak.Compute("data asset");
            uint expiryTime = timestamp + 4;
            UInt256 value = 1.Ether();
            uint units = 10U;
            byte[] salt = new byte[16];
            
            AbiSignature depositAbiDef = new AbiSignature("deposit",
                new AbiBytes(32),
                new AbiUInt(32),
                new AbiUInt(96),
                new AbiUInt(32),
                new AbiBytes(16),
                AbiType.Address,
                AbiType.Address);
            byte[] depositData = _abiEncoder.Encode(AbiEncodingStyle.Packed, depositAbiDef, assetId.Bytes, units, value, expiryTime, salt, _providerAccount, _consumerAccount);
            Keccak depositId = Keccak.Compute(depositData);

            Deposit deposit = new Deposit(depositId, units, expiryTime, value);
            Keccak depositTxHash = await depositService.MakeDepositAsync(_consumerAccount, deposit, 20.GWei());
            _bridge.IncrementNonce(_consumerAccount);
            TxReceipt depositTxReceipt = _bridge.GetReceipt(depositTxHash);
            TestContext.WriteLine("GAS USED FOR DEPOSIT: {0}", depositTxReceipt.GasUsed);
            Assert.AreEqual(StatusCode.Success, depositTxReceipt.StatusCode, $"deposit made {depositTxReceipt.Error} {Encoding.UTF8.GetString(depositTxReceipt.ReturnValue ?? new byte[0])}");

            // calls revert and cannot reuse the same state - use only for manual debugging
//            Assert.True(depositService.VerifyDeposit(deposit.Id), "deposit verified");

            RefundService refundService = new RefundService(_ndmBridge, _abiEncoder, _depositRepository,
                _contractAddress, LimboLogs.Instance, _wallet);

            // it will not work so far as we do everything within the same block and timestamp is wrong
            
            _bridge.NextBlockPlease(expiryTime + 1);
            RefundClaim refundClaim = new RefundClaim(depositId, assetId, units, value, expiryTime, salt, _providerAccount, _consumerAccount);
            UInt256 balanceBefore = _state.GetBalance(_consumerAccount);
            Keccak refundTxHash = await refundService.ClaimRefundAsync(_consumerAccount, refundClaim, 20.GWei());
            TxReceipt refundReceipt = _bridge.GetReceipt(refundTxHash);
            TestContext.WriteLine("GAS USED FOR REFUND CLAIM: {0}", refundReceipt.GasUsed);
            Assert.AreEqual(StatusCode.Success, refundReceipt.StatusCode, $"refund claim {refundReceipt.Error} {Encoding.UTF8.GetString(refundReceipt.ReturnValue ?? new byte[0])}");
            UInt256 balanceAfter = _state.GetBalance(_consumerAccount);
            Assert.Greater(balanceAfter, balanceBefore);
        }
        
        [Test]
        public async Task can_claim_early_refund()
        {
            uint timestamp = 1546871954;
            _bridge.NextBlockPlease(timestamp);
            
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Keccak assetId = Keccak.Compute("data asset");
            uint expiryTime = timestamp + (uint)TimeSpan.FromDays(4).TotalSeconds;
            UInt256 value = 1.Ether();
            uint units = 10U;
            byte[] pepper = new byte[16];
            
            AbiSignature depositAbiDef = new AbiSignature("deposit",
                new AbiBytes(32),
                new AbiUInt(32),
                new AbiUInt(96),
                new AbiUInt(32),
                new AbiBytes(16),
                AbiType.Address,
                AbiType.Address);
            
            byte[] depositData = _abiEncoder.Encode(AbiEncodingStyle.Packed, depositAbiDef, assetId.Bytes, units, value, expiryTime, pepper, _providerAccount, _consumerAccount);
            Keccak depositId = Keccak.Compute(depositData);

            Deposit deposit = new Deposit(depositId, units, expiryTime, value);
            Keccak depositTxHash = await depositService.MakeDepositAsync(_consumerAccount, deposit, 20.GWei());
            _bridge.IncrementNonce(_consumerAccount);
            TxReceipt depositTxReceipt = _bridge.GetReceipt(depositTxHash);
            TestContext.WriteLine("GAS USED FOR DEPOSIT: {0}", depositTxReceipt.GasUsed);
            Assert.AreEqual(StatusCode.Success, depositTxReceipt.StatusCode, $"deposit made {depositTxReceipt.Error} {Encoding.UTF8.GetString(depositTxReceipt.ReturnValue ?? new byte[0])}");

            // calls revert and cannot reuse the same state - use only for manual debugging
            // Assert.True(depositService.VerifyDeposit(deposit.Id), "deposit verified");

            uint claimableAfter = timestamp + (uint)TimeSpan.FromDays(1).TotalSeconds;
            AbiSignature earlyRefundAbiDef = new AbiSignature("earlyRefund", new AbiBytes(32), new AbiUInt(32));
            byte[] earlyRefundData = _abiEncoder.Encode(AbiEncodingStyle.Packed, earlyRefundAbiDef, depositId.Bytes, claimableAfter);
            RefundService refundService = new RefundService(_ndmBridge, _abiEncoder, _depositRepository,
                _contractAddress, LimboLogs.Instance, _wallet);
            // it will not work so far as we do everything within the same block and timestamp is wrong
            
            uint newTimestamp = 1546871954 + (uint)TimeSpan.FromDays(2).TotalSeconds;
            _bridge.NextBlockPlease(newTimestamp);
            
            Signature earlySig = _wallet.Sign(Keccak.Compute(earlyRefundData), _providerAccount);
            EarlyRefundClaim earlyRefundClaim = new EarlyRefundClaim(depositId, assetId, units, value, expiryTime, pepper, _providerAccount, claimableAfter, earlySig,_consumerAccount);
            UInt256 balanceBefore = _state.GetBalance(_consumerAccount);
            
            Keccak refundTxHash = await refundService.ClaimEarlyRefundAsync(_consumerAccount, earlyRefundClaim, 20.GWei());
            TxReceipt refundReceipt = _bridge.GetReceipt(refundTxHash);
            TestContext.WriteLine("GAS USED FOR EARLY REFUND CLAIM: {0}", refundReceipt.GasUsed);
            Assert.AreEqual(StatusCode.Success, refundReceipt.StatusCode, $"early refund claim {refundReceipt.Error} {Encoding.UTF8.GetString(refundReceipt.ReturnValue ?? new byte[0])}");
            UInt256 balanceAfter = _state.GetBalance(_consumerAccount);
            Assert.Greater(balanceAfter, balanceBefore);
        }
        
        [Test]
        public async Task can_not_claim_early_refund_with_wrong_signature()
        {
            uint timestamp = 1546871954;
            _bridge.NextBlockPlease(timestamp);
            
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Keccak assetId = Keccak.Compute("data asset");
            uint expiryTime = timestamp + (uint)TimeSpan.FromDays(4).TotalSeconds;
            UInt256 value = 1.Ether();
            uint units = 10U;
            byte[] pepper = new byte[16];
            
            AbiSignature depositAbiDef = new AbiSignature("deposit",
                new AbiBytes(32),
                new AbiUInt(32),
                new AbiUInt(96),
                new AbiUInt(32),
                new AbiBytes(16),
                AbiType.Address,
                AbiType.Address);
            
            byte[] depositData = _abiEncoder.Encode(AbiEncodingStyle.Packed, depositAbiDef, assetId.Bytes, units, value, expiryTime, pepper, _providerAccount, _consumerAccount);
            Keccak depositId = Keccak.Compute(depositData);

            Deposit deposit = new Deposit(depositId, units, expiryTime, value);
            Keccak depositTxHash = await depositService.MakeDepositAsync(_consumerAccount, deposit, 20.GWei());
            _bridge.IncrementNonce(_consumerAccount);
            TxReceipt depositTxReceipt = _bridge.GetReceipt(depositTxHash);
            TestContext.WriteLine("GAS USED FOR DEPOSIT: {0}", depositTxReceipt.GasUsed);
            Assert.AreEqual(StatusCode.Success, depositTxReceipt.StatusCode, $"deposit made {depositTxReceipt.Error} {Encoding.UTF8.GetString(depositTxReceipt.ReturnValue ?? new byte[0])}");

            // calls revert and cannot reuse the same state - use only for manual debugging
            // Assert.True(depositService.VerifyDeposit(deposit.Id), "deposit verified");

            uint claimableAfter = timestamp + (uint)TimeSpan.FromDays(1).TotalSeconds;
            AbiSignature earlyRefundAbiDef = new AbiSignature("earlyRefund", new AbiBytes(32), new AbiUInt(256));
            byte[] earlyRefundData = _abiEncoder.Encode(AbiEncodingStyle.Packed, earlyRefundAbiDef, depositId.Bytes, claimableAfter);
            RefundService refundService = new RefundService(_ndmBridge, _abiEncoder, _depositRepository,
                _contractAddress, LimboLogs.Instance, _wallet);
            // it will not work so far as we do everything within the same block and timestamp is wrong
            
            uint newTimestamp = 1546871954 + (uint)TimeSpan.FromDays(2).TotalSeconds;
            _bridge.NextBlockPlease(newTimestamp);
            
            Signature earlySig = _wallet.Sign(Keccak.Compute(earlyRefundData), _providerAccount);
            EarlyRefundClaim earlyRefundClaim = new EarlyRefundClaim(depositId, assetId, units, value, expiryTime, pepper, _providerAccount, claimableAfter, earlySig,_consumerAccount);
            UInt256 balanceBefore = _state.GetBalance(_consumerAccount);
            Keccak refundTxHash = await refundService.ClaimEarlyRefundAsync(_consumerAccount, earlyRefundClaim, 20.GWei());
            TxReceipt refundReceipt = _bridge.GetReceipt(refundTxHash);
            TestContext.WriteLine("GAS USED FOR EARLY REFUND CLAIM: {0}", refundReceipt.GasUsed);
            Assert.AreEqual(StatusCode.Failure, refundReceipt.StatusCode, $"early refund claim {refundReceipt.Error} {Encoding.UTF8.GetString(refundReceipt.ReturnValue ?? new byte[0])}");
            UInt256 balanceAfter = _state.GetBalance(_consumerAccount);
            Assert.Greater(balanceBefore, balanceAfter);
        }

        [Test]
        public async Task set_early_refund_ticket_should_fail_if_deposit_does_not_exits()
        {
            const RefundReason reason = RefundReason.DataDiscontinued;
            var ticket = new EarlyRefundTicket(TestItem.KeccakA, 0, null);
            var refundService = new RefundService(_ndmBridge, _abiEncoder, _depositRepository, _contractAddress, LimboLogs.Instance, _wallet);
            await refundService.SetEarlyRefundTicketAsync(ticket, reason);
            await _depositRepository.Received().GetAsync(ticket.DepositId);
            await _depositRepository.DidNotReceiveWithAnyArgs().UpdateAsync(null);
        }
        
        [Test]
        public async Task set_early_refund_ticket_should_succeed_if_deposit_exists()
        {
            const RefundReason reason = RefundReason.DataDiscontinued;
            var deposit = new Deposit(TestItem.KeccakA, 1, 1, 1);
            var depositDetails = new DepositDetails(deposit, null, null, null, 0, null, 0);
            var ticket = new EarlyRefundTicket(deposit.Id, 0, null);
            var refundService = new RefundService(_ndmBridge, _abiEncoder, _depositRepository, _contractAddress,
                LimboLogs.Instance, _wallet);
            _depositRepository.GetAsync(ticket.DepositId).Returns(depositDetails);
            await refundService.SetEarlyRefundTicketAsync(ticket, reason);
            depositDetails.EarlyRefundTicket.Should().Be(ticket);
            await _depositRepository.Received().GetAsync(ticket.DepositId);
            await _depositRepository.Received().UpdateAsync(depositDetails);
        }
    }
}
