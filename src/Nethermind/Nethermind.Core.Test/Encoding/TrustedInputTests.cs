// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

/// <summary>Checks the decoders that read with <see cref="RlpBehaviors.TrustedInput"/> against their full checks.</summary>
public class TrustedInputTests
{
    public interface IDecoderCase
    {
        byte[] Canonical { get; }

        /// <summary>Decodes <paramref name="rlp"/> as a whole and encodes the result again.</summary>
        byte[] DecodeAndEncode(byte[] rlp, RlpBehaviors mode);
    }

    private sealed class DecoderCase<T>(string name, IRlpDecoder<T> decoder, T item, RlpBehaviors behaviors = RlpBehaviors.None) : IDecoderCase
    {
        public byte[] Canonical { get; } = decoder.Encode(item, behaviors).Bytes;

        public byte[] DecodeAndEncode(byte[] rlp, RlpBehaviors mode)
        {
            RlpReader reader = new(rlp);
            T? decoded = decoder.Decode(ref reader, behaviors | mode);
            reader.CheckEnd();
            return decoder.Encode(decoded, behaviors).Bytes;
        }

        public override string ToString() => name;
    }

    private static IEnumerable<IDecoderCase> Cases()
    {
        LogEntry log = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA, TestItem.KeccakB).WithData([0, 0, 1, 2]).TestObject;
        TxReceipt receipt = Build.A.Receipt.WithAllFieldsFilled.WithLogs(log).WithTxType(TxType.EIP1559).TestObject;
        Transaction[] transactions =
        [
            Build.A.Transaction.SignedAndResolved().TestObject,
            Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(10).WithChainId(TestBlockchainIds.ChainId)
                .WithAccessList(new TestAccessListBuilder().TestObject).SignedAndResolved().TestObject,
            Build.A.Transaction.WithShardBlobTxTypeAndFields().WithChainId(TestBlockchainIds.ChainId).SignedAndResolved().TestObject,
        ];
        Block block = Build.A.Block.WithTransactions(transactions).WithWithdrawals(2).TestObject;

        yield return new DecoderCase<Account?>("account", AccountDecoder.Instance,
            new Account(1, 100, TestItem.KeccakA, TestItem.KeccakB));
        yield return new DecoderCase<Account?>("slim account", AccountDecoder.Slim, new Account(0, 127));
        yield return new DecoderCase<BlockHeader>("header", new HeaderDecoder(), block.Header);
        yield return new DecoderCase<BlockBody>("body", BlockBodyDecoder.Instance, block.Body);
        yield return new DecoderCase<Block>("block", new BlockDecoder(), block);
        yield return new DecoderCase<BlockInfo>("block info", BlockInfoDecoder.Instance, new BlockInfo(TestItem.KeccakA, 1000) { WasProcessed = true });
        yield return new DecoderCase<ChainLevelInfo>("chain level", new ChainLevelDecoder(),
            new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, 1000), new BlockInfo(TestItem.KeccakB, (UInt256)ulong.MaxValue + 1)));
        yield return new DecoderCase<LogEntry?>("log", LogEntryDecoder.Instance, log);
        yield return new DecoderCase<LogEntry?>("compact log", CompactLogEntryDecoder.Instance, log);
        yield return new DecoderCase<TxReceipt>("receipt message", new ReceiptMessageDecoder(), receipt, RlpBehaviors.Eip658Receipts);
        yield return new DecoderCase<TxReceipt>("storage receipt", new ReceiptStorageDecoder(), receipt, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);
        yield return new DecoderCase<TxReceipt>("compact receipt", CompactReceiptStorageDecoder.Instance, receipt, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);
        yield return new DecoderCase<Withdrawal>("withdrawal", new WithdrawalDecoder(), TestItem.WithdrawalA_1Eth);
        foreach (Transaction transaction in transactions)
        {
            yield return new DecoderCase<Transaction>($"{transaction.Type} transaction", TxDecoder.Instance, transaction);
        }
    }

    [TestCaseSource(nameof(Cases))]
    public void Trusted_mode_reads_canonical_input_like_full_mode(IDecoderCase decoderCase)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoderCase.DecodeAndEncode(decoderCase.Canonical, RlpBehaviors.None), Is.EqualTo(decoderCase.Canonical), "full checks");
            Assert.That(decoderCase.DecodeAndEncode(decoderCase.Canonical, RlpBehaviors.TrustedInput), Is.EqualTo(decoderCase.Canonical), "trusted");
        }
    }

    [Test]
    public void Truncated_input_throws_in_both_modes(
        [ValueSource(nameof(Cases))] IDecoderCase decoderCase,
        [Values(RlpBehaviors.None, RlpBehaviors.TrustedInput)] RlpBehaviors mode)
    {
        byte[] canonical = decoderCase.Canonical;
        for (int length = 0; length < canonical.Length; length++)
        {
            byte[] truncated = canonical.AsSpan(0, length).ToArray();
            Assert.That(() => decoderCase.DecodeAndEncode(truncated, mode), Throws.InstanceOf<RlpReaderException>(), $"length {length}");
        }
    }
}
