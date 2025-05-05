// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using FluentAssertions.Equivalency;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.WurdumTestPlugin.Tests;

public class ArbitrumTransactionParserTests
{
    private const int ChainId = 1;

    [Test]
    public void Parse_SubmitRetryable_ParsesCorrectly()
    {
        var message = new L1IncomingMessage(
            new(
                ArbitrumL1MessageKind.SubmitRetryable,
                new Address("0xDD6Bd74674C356345DB88c354491C7d3173c6806"),
                117,
                1745999206,
                new Hash256("0x0000000000000000000000000000000000000000000000000000000000000001"),
                295),
            "AAAAAAAAAAAAAAAAP6sYRiLcGbYQk0m5SBFJO/KkU2IAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAI4byb8EAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAjmgvhUZ1IAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGTUgAAAAAAAAAAAAAAACTtMEUtA7PH8NHRUAKG5uRFcNOQgAAAAAAAAAAAAAAAJO0wRS0Ds8fw0dFQAobm5EVw05CAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAUggAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAO5rKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            null);

        var transaction = (ArbitrumTransaction<ArbitrumSubmitRetryableTx>)L2MessageParser.ParseL2Transactions(message, ChainId, new()).Single();

        transaction.Inner.Should().BeEquivalentTo(new ArbitrumSubmitRetryableTx(
            ChainId,
            new("0x0000000000000000000000000000000000000000000000000000000000000001"),
            new("0xDD6Bd74674C356345DB88c354491C7d3173c6806"),
            295,
            10021000000413000,
            1000000000,
            21000,
            new("0x3fAB184622Dc19b6109349B94811493BF2a45362"),
            10000000000000000,
            new("0x93B4c114B40ECf1Fc34745400a1b9B9115c34E42"),
            413000,
            new("0x93B4c114B40ECf1Fc34745400a1b9B9115c34E42"),
            Array.Empty<byte>()));
    }

    [Test]
    public void Parse_L2Message_EthLegacy_ParsesCorrectly()
    {
        var message = new L1IncomingMessage(
            new(
                ArbitrumL1MessageKind.L2Message,
                new Address("0xDD6Bd74674C356345DB88c354491C7d3173c6806"),
                117,
                1745999206,
                new Hash256("0x0000000000000000000000000000000000000000000000000000000000000002"),
                295),
            "BPilgIUXSHboAIMBhqCAgLhTYEWAYA5gADmAYADzUP5//////////////////////////////////////////+A2AWAAgWAggjeANYKCNPWAFRVgOVeBgv1bgIJSUFBQYBRgDPMboCIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIioCIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIi",
            null);

        var transaction = L2MessageParser.ParseL2Transactions(message, ChainId, new()).Single();

        transaction.Should().BeEquivalentTo(new Transaction
        {
            Type = TxType.Legacy,
            Nonce = 0,
            GasPrice = 100000000000,
            GasLimit = 100000,
            To = null,
            Value = 0,
            Data = Convert.FromHexString("604580600e600039806000f350fe7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe03601600081602082378035828234f58015156039578182fd5b8082525050506014600cf3"),
            Signature = new(
                UInt256.Parse("15438945231642159389809464667825054380435997955418741871927677867721750618658"),
                UInt256.Parse("15438945231642159389809464667825054380435997955418741871927677867721750618658"),
                27)
        }, o => o.ForTransaction());
    }

    [Test]
    public void Parse_Deposit_ParsesCorrectly()
    {
        var message = new L1IncomingMessage(
            new(
                ArbitrumL1MessageKind.EthDeposit,
                new Address("0x502fae7d46d88F08Fc2F8ed27fCB2Ab183Eb3e1F"),
                165,
                1745999255,
                new Hash256("0x0000000000000000000000000000000000000000000000000000000000000009"),
                8),
            "Px6ufUbYjwj8L47Sf8sqsYPrLQ4AAAAAAAAAAAAAAAAAAAAAAAAAAAAAFS0Cx+FK9oAAAA==",
            null);

        var transaction = (ArbitrumTransaction<ArbitrumDepositTx>)L2MessageParser.ParseL2Transactions(message, ChainId, new()).Single();

        transaction.Inner.Should().BeEquivalentTo(new ArbitrumDepositTx(
            ChainId,
            new("0x0000000000000000000000000000000000000000000000000000000000000009"),
            new("0x502fae7d46d88F08Fc2F8ed27fCB2Ab183Eb3e1F"),
            new("0x3f1Eae7D46d88F08fc2F8ed27FCb2AB183EB2d0E"),
            UInt256.Parse("100000000000000000000000")));
    }

    [Test]
    public void Parse_L2Message_DynamicFeeTx_ParsesCorrectly()
    {
        var message = new L1IncomingMessage(
            new(
                ArbitrumL1MessageKind.L2Message,
                new Address("0xA4b000000000000000000073657175656e636572"),
                166,
                1745999257,
                null,
                8),
            "BAL4doMGSrqAhFloLwCEZVPxAIJSCJReFJfdHwjIey2P4j6aq2wd6DPZJ4kFa8deLWMQAACAwICgTJ7ERDhsUJoSmXYhVhdHIN5YgHJ2PBS1e9YImp0iAfmgTkKAGg0ukQ/BHPiMnbTpFqIuHlSBgQff7dPFFlMlhP4=",
            null);

        var transaction = L2MessageParser.ParseL2Transactions(message, ChainId, new()).Single();

        transaction.Should().BeEquivalentTo(new Transaction
        {
            ChainId = 412346,
            Type = TxType.EIP1559,
            Nonce = 0,
            GasPrice = 1500000000, // DynamicFeeTx.GasTipCap
            DecodedMaxFeePerGas = 1700000000, // DynamicFeeTx.GasFeeCap
            GasLimit = 21000,
            To = new("0x5E1497dD1f08C87b2d8FE23e9AAB6c1De833D927"),
            Value = UInt256.Parse("100000000000000000000"),
            Data = Array.Empty<byte>(),
            Signature = new(
                UInt256.Parse("34656292910065621035852780818211523586495092995652367972786234253091016933881"),
                UInt256.Parse("35397898221649370395961710411641180996206548691370223704696374300050614224126"),
                27)
        }, o => o.ForTransaction());
    }

    [Test]
    public void Parse_Internal_ParsesCorrectly()
    {
        var message = new L1IncomingMessage(
            new(
                ArbitrumL1MessageKind.BatchPostingReport,
                new Address("0xe2148eE53c0755215Df69b2616E552154EdC584f"),
                185,
                1745999275,
                new Hash256("0x000000000000000000000000000000000000000000000000000000000000000a"),
                8),
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGgR1aviFI7lPAdVIV32myYW5VIVTtxYTy77YI0r5OtTqBq17j1Lv4FmDlUUIb5DT9toNVdVxepdAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAAAAAAAAAAA",
            148376);

        var transaction = (ArbitrumTransaction<ArbitrumInternalTx>)L2MessageParser.ParseL2Transactions(message, ChainId, new()).Single();

        transaction.Inner.Should().BeEquivalentTo(new ArbitrumInternalTx(
            ChainId,
            1745999275,
            new("0xe2148ee53c0755215df69b2616e552154edc584f"),
            1,
            148376,
            8));
    }

    [Test]
    public void Parse_L2FundedByL1_Contract_ParsesCorrectly()
    {
        var message = new L1IncomingMessage(
            new(
                ArbitrumL1MessageKind.L2FundedByL1,
                new Address("0x502fae7d46d88f08fc2f8ed27fcb2ab183eb3e1f"),
                194,
                1746443431,
                new Hash256("0x000000000000000000000000000000000000000000000000000000000000000b"),
                8),
            "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABMLAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACPDRgAAAAAAAAAAAAAAAAARtX/jSFhPBC5DbGv3w8Pe8XHeSQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA0J3gig==",
            null);

        var transactions = L2MessageParser.ParseL2Transactions(message, ChainId, new());
        var deposit = (ArbitrumTransaction<ArbitrumDepositTx>)transactions[0];
        var contract = (ArbitrumTransaction<ArbitrumContractTx>)transactions[1];

        deposit.Inner.Should().BeEquivalentTo(new ArbitrumDepositTx(
            ChainId,
            new("0x9115655cbcdb654012cf1b2f7e5dbf11c9ef14e152a19d5f8ea75a329092d5a6"),
            new("0x0000000000000000000000000000000000000000"),
            new("0x502fae7d46d88F08Fc2F8ed27fCB2Ab183Eb3e1F"),
            UInt256.Zero));
        contract.Inner.Should().BeEquivalentTo(new ArbitrumContractTx(
            ChainId,
            new("0xfc80cd5fe514767bc6e66ec558e68a5429ea70b50fa6caa3b53fc9278e918632"),
            new("0x502fae7d46d88F08Fc2F8ed27fCB2Ab183Eb3e1F"),
            600000000,
            312000,
            new("0x11B57FE348584f042E436c6Bf7c3c3deF171de49"),
            UInt256.Zero,
            Convert.FromHexString("d09de08a")), o => o.ForArbitrumContractTx());
    }
}

public static class AssertionExtensions
{
    public static EquivalencyAssertionOptions<Transaction> ForTransaction(this EquivalencyAssertionOptions<Transaction> options)
    {
        return options
            .Using<Memory<byte>>(context => context.Subject.ToArray().Should().BeEquivalentTo(context.Expectation.ToArray())).WhenTypeIs<Memory<byte>>()
            .Excluding(t => t.Hash);
    }

    public static EquivalencyAssertionOptions<ArbitrumContractTx> ForArbitrumContractTx(this EquivalencyAssertionOptions<ArbitrumContractTx> options)
    {
        return options
            .Using<ReadOnlyMemory<byte>>(context => context.Subject.ToArray().Should().BeEquivalentTo(context.Expectation.ToArray()))
            .WhenTypeIs<ReadOnlyMemory<byte>>();
    }
}
