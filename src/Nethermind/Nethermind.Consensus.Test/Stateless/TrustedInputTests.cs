// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Stateless.Execution;
using Nethermind.Stateless.Execution.IO;
using Nethermind.StatelessInputGen;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

[NonParallelizable]
public class TrustedInputTests
{
    private const int TransactionsList = 56;
    private const int WitnessHeadersList = 60;
    private const int StateNodesList = 64;
    private const int CodesList = 68;

    [Test]
    public async Task Converted_input_validates_and_reports_the_block_hash([Values] bool amsterdam)
    {
        (byte[] input, Block block) = await CreateInput(amsterdam);

        StatelessValidationResult result = Execute(input);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.NewPayloadRequestRoot, Is.EqualTo(block.Hash));
            Assert.That(result.SchemaId, Is.EqualTo(TrustedInput.SchemaId));
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
    [TestCase(TransactionsList)]
    [TestCase(WitnessHeadersList)]
    [TestCase(StateNodesList)]
    [TestCase(CodesList)]
    public async Task Tampered_item_fails_the_block(int list)
    {
        (byte[] input, _) = await CreateInput(amsterdam: false);

        ForEachItem(input, list, item => item[^1] ^= 1);

        Assert.That(Execute(input).IsSuccess, Is.False);
    }

    [Test]
    public async Task Malformed_input_reports_the_sentinel([Values(0, 4, 72)] int corruptedOffset)
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
            // Codes carry their execution padding before the padding to 8.
            int padding = list == CodesList ? 34 : 0;
            position += TrustedInput.ItemPrefixLength + ((length + padding + 7) & ~7);
        }
    }
}
