// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Stateless.Execution;
using Nethermind.Stateless.Execution.IO;
using Nethermind.StatelessInputGen;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

[NonParallelizable]
public class TrustedInputTests
{
    [Test]
    public async Task Nonzero_post_merge_seal_fields_are_rejected([Values] bool difficulty)
    {
        (byte[] input, _) = await CreateInput(amsterdam: false);
        TrustedPayload payload = TrustedInput.Read(input);
        using Witness witness = payload.Witness;
        Block block = payload.GetBlock();
        if (difficulty) block.Header.Difficulty = 1;
        else block.Header.Nonce = 1;
        byte[] tampered = TrustedInput.Write(payload.ChainId, ProtocolFork.Current, block, payload.EncodedTransactions, witness);
        Assert.That(Execute(tampered).IsSuccess, Is.False);
    }

    public enum BodyRoot { Transactions, Withdrawals, Uncles }

    /// <summary>The header's hash binds the body only through the roots the trusted path checks itself.</summary>
    [Test]
    public async Task Header_root_the_body_does_not_match_fails_the_block([Values] BodyRoot root)
    {
        (byte[] input, _) = await CreateInput(amsterdam: false);
        TrustedPayload payload = TrustedInput.Read(input);
        using Witness witness = payload.Witness;
        Block block = payload.GetBlock();
        switch (root)
        {
            case BodyRoot.Transactions: block.Header.TxRoot = TestItem.KeccakA; break;
            case BodyRoot.Withdrawals: block.Header.WithdrawalsRoot = TestItem.KeccakA; break;
            default: block.Header.UnclesHash = TestItem.KeccakA; break;
        }

        byte[] tampered = TrustedInput.Write(payload.ChainId, ProtocolFork.Current, block, payload.EncodedTransactions, witness);
        Assert.That(Execute(tampered).IsSuccess, Is.False);
    }

    [Test]
    public async Task Public_output_distinguishes_pinned_future_fork()
    {
        (byte[] input, _) = await CreateInput(amsterdam: true);
        StatelessValidationResult pinned = Execute(input);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(16), (uint)ProtocolFork.Current);
        StatelessValidationResult scheduled = Execute(input);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pinned.IsSuccess, Is.True);
            Assert.That(scheduled.IsSuccess, Is.False);
            Assert.That(pinned.NewPayloadRequestRoot, Is.EqualTo(scheduled.NewPayloadRequestRoot));
            Assert.That(pinned.SchemaId, Is.Not.EqualTo(scheduled.SchemaId));
        }
    }

    [Test]
    public void Collector_retains_only_recovery_bytes([Values(0, 1, 127, 128, 129, 1048576)] int length)
    {
        StatelessExecutor.EcRecoverInputCollector collector = new();
        byte[] input = new byte[length];
        new Random(42).NextBytes(input);
        collector.ReportAction(3000, default, TestItem.AddressA, ECRecoverPrecompile.Address, input, ExecutionType.CALL, true);
        Assert.That(collector.Inputs, Has.Count.EqualTo(1));
        Assert.That(collector.Inputs[0], Is.EqualTo(input.AsSpan(0, Math.Min(length, 128)).ToArray()));
    }

    [Test]
    public void Collector_preserves_parallel_recoveries()
    {
        StatelessExecutor.EcRecoverInputCollector collector = new();
        CompositeBlockTracer tracer = new();
        tracer.Add(collector);
        IBlockTracer parallelTracer = tracer.GetParallelSafeTracer();
        Parallel.For(0, 10_000, i => parallelTracer.StartNewTxTrace(null).ReportAction(3000, default, TestItem.AddressA,
            ECRecoverPrecompile.Address, BitConverter.GetBytes(i), ExecutionType.CALL, true));

        Assert.That(collector.Inputs, Has.Count.EqualTo(10_000));
        Assert.That(collector.Inputs.ConvertAll(input => BitConverter.ToInt32(input)), Is.EquivalentTo(Enumerable.Range(0, 10_000)));
    }

    private const int SenderHintsSection = 48;
    private const int TransactionsList = 56;
    private const int WitnessHeadersList = 60;
    private const int StateNodesList = 64;
    private const int CodesList = 68;
    // Codes carry their execution padding before the padding to 8.
    private const int CodeExecutionPadding = 34;

    [Test]
    public async Task Converted_input_validates_and_reports_the_block_hash([Values] bool amsterdam)
    {
        (byte[] input, Block block) = await CreateInput(amsterdam);

        StatelessValidationResult result = Execute(input);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.NewPayloadRequestRoot, Is.EqualTo(block.Hash));
            Assert.That(result.SchemaId, Is.EqualTo(amsterdam ? 0x9501 : 0x8001));
        }
    }

    [Test]
    public async Task Written_input_validates()
    {
        (Block block, Witness witness, ISpecProvider specProvider) = StatelessInputGeneratorTests.CreateBlock(amsterdam: false, currentChainActivation: true);
        using (witness)
        {
            // The SSZ encoding fills the block in as the engine API would, as a node's own block is.
            await InputGenerator.EncodeInput(block, witness, specProvider);
            byte[] input = TrustedInput.Write(specProvider.ChainId, block, witness);

            Assert.That(Execute(input).IsSuccess, Is.True);
        }
    }

    /// <summary>Every item the guest uses where it lies is still bound to what the block commits to.</summary>
    [Test]
    public async Task Tampered_item_fails_the_block([Values(TransactionsList, WitnessHeadersList, StateNodesList, CodesList)] int list)
    {
        (byte[] input, _) = await CreateInput(amsterdam: false);

        ForEachItem(input, list, item => item[^1] ^= 1);

        Assert.That(Execute(input).IsSuccess, Is.False);
    }

    /// <summary>The code store serves codes where they lie only when the list offers them with their hashes.</summary>
    [Test]
    public async Task Codes_are_read_with_their_hashes()
    {
        (byte[] input, _) = await CreateInput(amsterdam: false);
        using Witness witness = TrustedInput.Read(input).Witness;

        Assert.That(witness.Codes, Is.InstanceOf<IReadOnlyList<(ValueHash256 Hash, ReadOnlyMemory<byte> Code)>>());
        IReadOnlyList<(ValueHash256 Hash, ReadOnlyMemory<byte> Code)> codes = (IReadOnlyList<(ValueHash256, ReadOnlyMemory<byte>)>)witness.Codes;
        Assert.That(codes, Is.Not.Empty);
        foreach ((ValueHash256 hash, ReadOnlyMemory<byte> code) in codes)
            Assert.That(hash, Is.EqualTo(ValueKeccak.Compute(code.Span)));
    }

    [Test]
    public async Task Sender_hints_hold_each_signers_key_and_signature_point()
    {
        (byte[] input, Block block) = await CreateInput(amsterdam: false);
        ReadOnlySpan<byte> hints = SenderHints(input);
        ReadOnlySpan<byte> keys = TrustedInput.SenderKeys(hints);
        ReadOnlySpan<byte> ys = TrustedInput.SignatureYs(hints);

        Assert.That(hints.Length, Is.EqualTo(block.Transactions.Length * TrustedInput.SenderHintLength).And.Positive);
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(PublicKey.ComputeAddress(keys.Slice(i * 64, 64)), Is.EqualTo(block.Transactions[i].SenderAddress));
                AssertSignaturePoint(block.Transactions[i].Signature!, ys.Slice(i * 32, 32));
            }
        }
    }

    /// <summary>Hints cover the ECRECOVER calls that recover a key, and the EIP-7702 authorizations, in columns.</summary>
    [Test]
    public void Recovery_hints_hold_each_recovery_with_its_key()
    {
        EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet);
        ValueHash256 message = ValueKeccak.Compute("message"u8);
        Signature signature = ecdsa.Sign(TestItem.PrivateKeyB, in message);
        byte[] call = new byte[128];
        message.Bytes.CopyTo(call);
        call[63] = (byte)(27 + signature.RecoveryId);
        signature.Bytes.CopyTo(call.AsSpan(64));
        byte[] badV = (byte[])call.Clone();
        badV[63] = 29;
        AuthorizationTuple authorization = ecdsa.Sign(TestItem.PrivateKeyC, BlockchainIds.Mainnet, TestItem.AddressD, 0);
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.WithType(TxType.SetCode)
            .WithAuthorizationCode(authorization).SignedAndResolved(TestItem.PrivateKeyA).TestObject).TestObject;

        byte[] hints = TrustedInput.RecoveryHints(BlockchainIds.Mainnet, block, [call, badV]);

        ReadOnlySpan<byte> keys = TrustedInput.RecoveryHintColumn(hints, RecoveryHintField.Key);
        ReadOnlySpan<byte> ys = TrustedInput.RecoveryHintColumn(hints, RecoveryHintField.Y);
        ReadOnlySpan<byte> signatures = TrustedInput.RecoveryHintColumn(hints, RecoveryHintField.Signature);
        Assert.That(hints, Has.Length.EqualTo(2 * TrustedInput.RecoveryHintLength));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TrustedInput.RecoveryHintColumn(hints, RecoveryHintField.Message)[..32].ToArray(), Is.EqualTo(message.ToByteArray()));
            Assert.That(signatures[..64].ToArray(), Is.EqualTo(signature.Bytes.ToArray()));
            Assert.That(TrustedInput.RecoveryHintColumn(hints, RecoveryHintField.RecoveryId).ToArray(),
                Is.EqualTo(new[] { signature.RecoveryId, authorization.AuthoritySignature.RecoveryId }));
            Assert.That(PublicKey.ComputeAddress(keys[..64]), Is.EqualTo(TestItem.AddressB));
            Assert.That(PublicKey.ComputeAddress(keys[64..]), Is.EqualTo(TestItem.AddressC));
            AssertSignaturePoint(signature, ys[..32]);
            AssertSignaturePoint(authorization.AuthoritySignature, ys[32..]);
        }
    }

    /// <summary>A guest checks hints in a batch and recovers the senders when the batch fails, so a hint changes no result.</summary>
    [Test]
    public async Task Wrong_sender_hint_leaves_the_block_valid()
    {
        (byte[] input, _) = await CreateInput(amsterdam: false);

        SenderHints(input)[0] ^= 1;

        Assert.That(Execute(input).IsSuccess, Is.True);
    }

    [Test]
    public async Task Sender_hint_weights_follow_the_block_hash_and_every_hint()
    {
        (byte[] input, Block block) = await CreateInput(amsterdam: false);
        byte[] hints = SenderHints(input).ToArray();
        byte[] weights = TrustedInput.HintWeights(block.Hash!, hints, []);

        byte[] otherHints = (byte[])hints.Clone();
        otherHints[^1] ^= 1;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(weights, Has.Length.EqualTo(block.Transactions.Length * 16));
            Assert.That(TrustedInput.HintWeights(block.Hash!, hints, []), Is.EqualTo(weights));
            Assert.That(TrustedInput.HintWeights(block.Hash!, otherHints, []), Is.Not.EqualTo(weights));
            Assert.That(TrustedInput.HintWeights(Keccak.Compute(block.Hash!.Bytes), hints, []), Is.Not.EqualTo(weights));
        }
    }

    [Test]
    public async Task Malformed_input_reports_the_sentinel([Values(0, 4, 17, SenderHintsSection + 4, 72)] int corruptedOffset)
    {
        (byte[] input, _) = await CreateInput(amsterdam: false);
        input[corruptedOffset] ^= 1;

        StatelessValidationResult result = Execute(input);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.SchemaId, Is.Zero);
        }
    }

    /// <summary>An item length that runs past the input, or into a code's execution padding, makes the input malformed.</summary>
    [Test]
    public async Task Bad_item_length_reports_the_sentinel(
        [Values(TransactionsList, WitnessHeadersList, StateNodesList, CodesList)] int list,
        [Values] bool pastTheInput)
    {
        (byte[] input, _) = await CreateInput(amsterdam: false);
        int position = (int)BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(list)) + 8;
        int padding = list == CodesList ? CodeExecutionPadding : 0;
        uint room = (uint)(input.Length - position - TrustedInput.ItemPrefixLength);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(position), pastTheInput ? uint.MaxValue : room - (uint)padding + 1);

        StatelessValidationResult result = Execute(input);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.SchemaId, Is.Zero);
        }
    }

    private static async Task<(byte[] Input, Block Block)> CreateInput(bool amsterdam)
    {
        (Block block, Witness witness, ISpecProvider specProvider) = StatelessInputGeneratorTests.CreateBlock(amsterdam, currentChainActivation: true);
        using (witness)
        {
            byte[] stateless = (await InputGenerator.EncodeInput(block, witness, specProvider))!;
            return (TrustedInput.FromStatelessInput(stateless), block);
        }
    }

    private static StatelessValidationResult Execute(byte[] input)
    {
        StatelessValidationResult.Decode(StatelessExecutor.ExecuteTrusted(input), out StatelessValidationResult result);
        return result;
    }

    /// <summary>Asserts that <paramref name="y"/> is the y of <paramref name="signature"/>'s R on secp256k1.</summary>
    private static void AssertSignaturePoint(Signature signature, ReadOnlySpan<byte> y)
    {
        BigInteger p = BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F", NumberStyles.HexNumber);
        BigInteger x = new(signature.RAsSpan, isUnsigned: true, isBigEndian: true);
        BigInteger value = new(y, isUnsigned: true, isBigEndian: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(value * value % p, Is.EqualTo((x * x * x + 7) % p));
            Assert.That(value.IsEven, Is.EqualTo((signature.RecoveryId & 1) == 0));
        }
    }

    private static Span<byte> SenderHints(byte[] input) => input.AsSpan(
        (int)BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(SenderHintsSection)),
        (int)BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(SenderHintsSection + 4)));

    private static void ForEachItem(byte[] input, int list, Action<Span<byte>> mutate)
    {
        int position = (int)BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(list));
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(position));
        position += 8;
        Assert.That(count, Is.Positive);
        for (int i = 0; i < count; i++)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(position));
            mutate(input.AsSpan(position + TrustedInput.ItemPrefixLength, length));
            int padding = list == CodesList ? CodeExecutionPadding : 0;
            position += TrustedInput.ItemPrefixLength + ((length + padding + 7) & ~7);
        }
    }
}
