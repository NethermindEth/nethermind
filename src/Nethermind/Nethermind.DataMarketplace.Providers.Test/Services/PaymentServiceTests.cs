/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Services;
using Nethermind.DataMarketplace.Test;
using Nethermind.Evm;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Providers.Test.Services
{
    internal class PaymentServiceTests : ContractInteractionTest
    {       
        [SetUp]
        public void Setup()
        {
            Prepare();
            _paymentService = new PaymentService(_ndmBridge, _abiEncoder, _wallet, _contractAddress, _logManager, _txPool);
        }

        private AbiSignature _receiptAbiDef = new AbiSignature("receipt", new AbiBytes(32), new AbiUInt(32), new AbiUInt(32));
        private PaymentService _paymentService;

        [Test]
        public async Task Can_claim_payment()
        {
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Keccak assetId = Keccak.Compute("data asset");
            uint expiryTime = 1547051589;
            BigInteger value = (BigInteger) 336.Ether() / 100;
            uint units = 336U;
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

            Deposit deposit = new Deposit(depositId, units, expiryTime, (UInt256) value);
            Keccak depositTxHash = await depositService.MakeDepositAsync(_consumerAccount, deposit, 20.GWei());
            TxReceipt depositTxReceipt = _bridge.GetReceipt(depositTxHash);
            TestContext.WriteLine("GAS USED FOR DEPOSIT: {0}", depositTxReceipt.GasUsed);
            Assert.AreEqual(StatusCode.Success, depositTxReceipt.StatusCode, $"deposit made {depositTxReceipt.Error} {Encoding.UTF8.GetString(depositTxReceipt.ReturnValue ?? new byte[0])}");

            // calls revert and cannot reuse the same state - use only for manual debugging
//            Assert.True(depositService.VerifyDeposit(deposit.Id), "deposit verified");

            DataRequest dataRequest = new DataRequest(assetId, units, (UInt256) value, expiryTime, salt, _providerAccount, _consumerAccount, new Signature(new byte[65]));
            Assert.AreEqual(deposit.Id, depositId, "depositID");

            await ClaimPaymentFor(dataRequest, depositId, 0, 27, _providerAccount);

            foreach (GethLikeTxTrace gethLikeTxTrace in _bridge.GethTracer.BuildResult().Skip(1))
            {
                TestContext.WriteLine(new EthereumJsonSerializer().Serialize(gethLikeTxTrace, true));
            }
        }

        [Test]
        public async Task Can_claim_payment_for_cold_wallet()
        {
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Keccak assetId = Keccak.Compute("data asset");
            uint expiryTime = 1547051589;
            BigInteger value = (BigInteger) 336.Ether() / 100;
            uint units = 336U;
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

            Deposit deposit = new Deposit(depositId, units, expiryTime, (UInt256) value);
            Keccak depositTxHash = await depositService.MakeDepositAsync(_consumerAccount, deposit, 20.GWei());
            TxReceipt depositTxReceipt = _bridge.GetReceipt(depositTxHash);
            TestContext.WriteLine("GAS USED FOR DEPOSIT: {0}", depositTxReceipt.GasUsed);
            Assert.AreEqual(StatusCode.Success, depositTxReceipt.StatusCode, $"deposit made {depositTxReceipt.Error} {Encoding.UTF8.GetString(depositTxReceipt.ReturnValue ?? new byte[0])}");

            // calls revert and cannot reuse the same state - use only for manual debugging
//            Assert.True(depositService.VerifyDeposit(deposit.Id), "deposit verified");

            DataRequest dataRequest = new DataRequest(assetId, units, (UInt256) value, expiryTime, salt, _providerAccount, _consumerAccount, new Signature(new byte[65]));
            Assert.AreEqual(deposit.Id, depositId, "depositID");

            await ClaimPaymentFor(dataRequest, depositId, 0, 27, TestItem.AddressA);

            foreach (GethLikeTxTrace gethLikeTxTrace in _bridge.GethTracer.BuildResult().Skip(1))
            {
                TestContext.WriteLine(new EthereumJsonSerializer().Serialize(gethLikeTxTrace, true));
            }
        }

        [Test]
        public async Task Can_claim_series_of_payments()
        {
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Keccak assetId = Keccak.Compute("data asset");
            uint expiryTime = 1547051589;
            BigInteger value = (BigInteger) 336.Ether() / 100;
            uint units = 336U;
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

            Deposit deposit = new Deposit(depositId, units, expiryTime, (UInt256) value);
            Keccak depositTxHash = await depositService.MakeDepositAsync(_consumerAccount, deposit, 20.GWei());
            TxReceipt depositTxReceipt = _bridge.GetReceipt(depositTxHash);
            TestContext.WriteLine("GAS USED FOR DEPOSIT: {0}", depositTxReceipt.GasUsed);
            Assert.AreEqual(StatusCode.Success, depositTxReceipt.StatusCode, $"deposit made {depositTxReceipt.Error} {Encoding.UTF8.GetString(depositTxReceipt.ReturnValue ?? new byte[0])}");

            // calls revert and cannot reuse the same state - use only for manual debugging
//            Assert.True(depositService.VerifyDeposit(deposit.Id), "deposit verified");

            DataRequest dataRequest = new DataRequest(assetId, units, (UInt256) value, expiryTime, salt, _providerAccount, _consumerAccount, new Signature(new byte[65]));
            Assert.AreEqual(deposit.Id, depositId, "depositID");

            await ClaimPaymentFor(dataRequest, depositId, 0, 27, _providerAccount);
            await ClaimPaymentFor(dataRequest, depositId, 28, 55, _providerAccount);
            await ClaimPaymentFor(dataRequest, depositId, 56, 83, _providerAccount);
            await ClaimPaymentFor(dataRequest, depositId, 84, 111, _providerAccount);
            await ClaimPaymentFor(dataRequest, depositId, 112, 139, _providerAccount);
            await ClaimPaymentFor(dataRequest, depositId, 140, 167, _providerAccount);
            await ClaimPaymentFor(dataRequest, depositId, 168, 195, _providerAccount);
            await ClaimPaymentFor(dataRequest, depositId, 196, 223, _providerAccount);
            await ClaimPaymentFor(dataRequest, depositId, 224, 251, _providerAccount);
            await ClaimPaymentFor(dataRequest, depositId, 252, 279, _providerAccount);
            await ClaimPaymentFor(dataRequest, depositId, 280, 307, _providerAccount);
            await ClaimPaymentFor(dataRequest, depositId, 308, 335, _providerAccount);
        }

        private async Task<TxReceipt> ClaimPaymentFor(DataRequest dataRequest, Keccak depositId, uint start, uint end, Address payToAccount)
        {
            UInt256 payToBalanceBefore = _state.GetBalance(payToAccount);
            UInt256 providerBalanceBefore = _state.GetBalance(_providerAccount);
            UInt256 feeBalanceBefore = _state.GetBalance(_feeAccount);

            UnitsRange unitRange = new UnitsRange(start, end);
            UInt256 value = dataRequest.Units * (UInt256) dataRequest.Value / dataRequest.Units;
            uint claimedUnits = unitRange.Units;
            UInt256 claimedValue = claimedUnits * (UInt256) dataRequest.Value / dataRequest.Units;
            byte[] receiptData = _abiEncoder.Encode(AbiEncodingStyle.Packed, _receiptAbiDef, depositId.Bytes, unitRange.From, unitRange.To);
            Signature receiptSig = _wallet.Sign(Keccak.Compute(receiptData), _consumerAccount);
            PaymentClaim paymentClaim = new PaymentClaim(Keccak.Zero, depositId, dataRequest.DataAssetId,
                "data asset", dataRequest.Units, claimedUnits, unitRange, value, (UInt256) claimedValue,
                dataRequest.ExpiryTime, dataRequest.Pepper, _providerAccount, _consumerAccount, receiptSig, 0, Array.Empty<TransactionInfo>(), PaymentClaimStatus.Unknown);

            UInt256 gasPrice = 20.GWei();
            Keccak paymentTxHash = await _paymentService.ClaimPaymentAsync(paymentClaim, payToAccount, gasPrice);
            _bridge.IncrementNonce(_providerAccount);

            UInt256 feeBalanceAfter = _state.GetBalance(_feeAccount);
            UInt256 providerBalanceAfter = _state.GetBalance(_providerAccount);
            UInt256 payToBalanceAfter = _state.GetBalance(payToAccount);
            
            TxReceipt txReceipt = _bridge.GetReceipt(paymentTxHash);
            TestContext.WriteLine("GAS USED FOR PAYMENT CLAIM: {0}", txReceipt.GasUsed);
            TestContext.WriteLine($"(FEE) BALANCE BEFORE: {feeBalanceBefore}");
            TestContext.WriteLine($"(FEE) DIFFERENCE: {feeBalanceAfter - feeBalanceBefore}");
            TestContext.WriteLine($"(PAY TO) BALANCE BEFORE: {payToBalanceBefore}");
            TestContext.WriteLine($"(PAY TO) DIFFERENCE: {payToBalanceAfter - payToBalanceBefore}");
            TestContext.WriteLine($"(PROVIDER) BALANCE BEFORE: {providerBalanceBefore}");
            TestContext.WriteLine($"(PROVIDER) DIFFERENCE: {(BigInteger) providerBalanceAfter - (BigInteger) providerBalanceBefore}");
            
            bool isProviderSameAsPayTo = _providerAccount.Equals(payToAccount);
            UInt256 expectedPayment = (8 * dataRequest.Value * (unitRange.To - unitRange.From + 1)) / (dataRequest.Units * 10);
            UInt256 expectedGasFee = (UInt256) txReceipt.GasUsed * 20.GWei();
            UInt256 expectedFee = 2 * dataRequest.Value * unitRange.Units / (dataRequest.Units * 10);

            Assert.AreEqual((UInt256) 20, 100 * expectedFee / (expectedPayment + expectedFee), "fee %");
            Assert.AreEqual(expectedFee, feeBalanceAfter - feeBalanceBefore, "fee");
            Assert.AreEqual(providerBalanceBefore - expectedGasFee + (isProviderSameAsPayTo ? UInt256.One : UInt256.Zero) * expectedPayment, providerBalanceAfter, $"before: {providerBalanceBefore}, after: {providerBalanceAfter}");
            Assert.AreEqual(payToBalanceBefore - (isProviderSameAsPayTo ? UInt256.One : UInt256.Zero) * expectedGasFee + expectedPayment, payToBalanceAfter, $"before: {payToBalanceBefore}, after: {payToBalanceAfter}");
            return txReceipt;
        }
    }
}