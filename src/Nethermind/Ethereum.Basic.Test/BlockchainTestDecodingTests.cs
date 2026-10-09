// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Messages;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

public class BlockchainTestDecodingTests : BlockchainTestBase
{
    /// <remarks>
    /// A zero-length logs bloom decodes only where EIP-7668 is active, so the harness must decode with the test's
    /// fork rules. The block repeats the genesis timestamp, so header validation is the first to reject it, and
    /// that rejection shows that it decoded.
    /// </remarks>
    [Test]
    public async Task Decodes_genesis_and_blocks_with_the_eip7668_zero_length_bloom()
    {
        Block genesis = CreateEip7668Block(Build.A.Block.Genesis);
        Block block = CreateEip7668Block(Build.A.Block.WithNumber(1).WithParentHash(genesis.Hash!));
        BlockchainTest test = new()
        {
            Name = nameof(Decodes_genesis_and_blocks_with_the_eip7668_zero_length_bloom),
            Network = new OverridableReleaseSpec(Bogota.Instance) { IsEip7668Enabled = true },
            GenesisRlp = Rlp.Encode(genesis),
            GenesisBlockHeader = new TestBlockHeaderJson { Hash = genesis.Hash!.ToString() },
            Blocks = [new TestBlockJson { Rlp = Rlp.Encode(block).Bytes.ToHexString(true), ExpectException = "BlockException.INVALID_BLOCK_TIMESTAMP_OLDER_THAN_PARENT" }],
            Pre = [],
            PostState = [],
            LastBlockHash = genesis.Hash,
        };

        EthereumTestResult result = await RunTest(test);

        Assert.That(result.Error, Is.EqualTo(BlockErrorMessages.InvalidTimestamp));
    }

    private static Block CreateEip7668Block(BlockBuilder builder) => builder
        .WithPostMergeRules()
        .WithBloom(Bloom.ZeroLength)
        .WithStateRoot(Keccak.EmptyTreeHash)
        .WithWithdrawals(0)
        .WithBlobGasUsed(0)
        .WithExcessBlobGas(0)
        .WithParentBeaconBlockRoot(Keccak.Zero)
        .WithEmptyRequestsHash()
        .WithBlockAccessListHash(Keccak.OfAnEmptySequenceRlp)
        .WithSlotNumber(0)
        .TestObject;
}
