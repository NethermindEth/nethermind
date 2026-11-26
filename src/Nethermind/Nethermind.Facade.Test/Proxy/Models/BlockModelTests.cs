// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Proxy.Models;

[Parallelizable(ParallelScope.All)]
public class BlockModelTests
{
    // Absent blob fields stay absent rather than becoming zero.
    [TestCase(10UL, 11UL)]
    [TestCase(null, null)]
    public void ToBlock_copies_every_header_field_the_model_carries(ulong? blobGasUsed, ulong? excessBlobGas)
    {
        // Every value set is distinct, so a field copied into the wrong place shows.
        BlockModel<Hash256> model = new()
        {
            Difficulty = 1,
            ExtraData = [2],
            GasLimit = 3,
            GasUsed = 4,
            Nonce = 5,
            Number = 6,
            Timestamp = 7,
            BaseFeePerGas = 8,
            TotalDifficulty = 9,
            BlobGasUsed = blobGasUsed,
            ExcessBlobGas = excessBlobGas,
            Hash = TestItem.KeccakA,
            MixHash = TestItem.KeccakB,
            ParentHash = TestItem.KeccakC,
            ReceiptsRoot = TestItem.KeccakD,
            Sha3Uncles = TestItem.KeccakE,
            StateRoot = TestItem.KeccakF,
            TransactionsRoot = TestItem.KeccakG,
            Miner = TestItem.AddressA,
        };

        BlockHeader header = model.ToBlock().Header;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.Difficulty, Is.EqualTo((UInt256)1), nameof(model.Difficulty));
            Assert.That(header.ExtraData, Is.EqualTo(new byte[] { 2 }), nameof(model.ExtraData));
            Assert.That(header.GasLimit, Is.EqualTo(3UL), nameof(model.GasLimit));
            Assert.That(header.GasUsed, Is.EqualTo(4UL), nameof(model.GasUsed));
            Assert.That(header.Nonce, Is.EqualTo(5UL), nameof(model.Nonce));
            Assert.That(header.Number, Is.EqualTo(6UL), nameof(model.Number));
            Assert.That(header.Timestamp, Is.EqualTo(7UL), nameof(model.Timestamp));
            Assert.That(header.BaseFeePerGas, Is.EqualTo((UInt256)8), nameof(model.BaseFeePerGas));
            Assert.That(header.TotalDifficulty, Is.EqualTo((UInt256)9), nameof(model.TotalDifficulty));
            Assert.That(header.BlobGasUsed, Is.EqualTo(blobGasUsed), nameof(model.BlobGasUsed));
            Assert.That(header.ExcessBlobGas, Is.EqualTo(excessBlobGas), nameof(model.ExcessBlobGas));
            Assert.That(header.Hash, Is.EqualTo(TestItem.KeccakA), nameof(model.Hash));
            Assert.That(header.MixHash, Is.EqualTo(TestItem.KeccakB), nameof(model.MixHash));
            Assert.That(header.ParentHash, Is.EqualTo(TestItem.KeccakC), nameof(model.ParentHash));
            Assert.That(header.ReceiptsRoot, Is.EqualTo(TestItem.KeccakD), nameof(model.ReceiptsRoot));
            Assert.That(header.UnclesHash, Is.EqualTo(TestItem.KeccakE), nameof(model.Sha3Uncles));
            Assert.That(header.StateRoot, Is.EqualTo(TestItem.KeccakF), nameof(model.StateRoot));
            Assert.That(header.TxRoot, Is.EqualTo(TestItem.KeccakG), nameof(model.TransactionsRoot));
            Assert.That(header.Beneficiary, Is.EqualTo(TestItem.AddressA), nameof(model.Miner));
        }
    }
}
