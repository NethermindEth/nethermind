// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Blockchain.Blocks;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Encoding;

public class AuRaBlockStoreDecoderTests
{
    // Gnosis genesis shape: empty step + 65-byte (r|s|v) AuRa signature.
    private static Block AuRaGenesisShapedBlock() =>
        Build.A.Block.WithNumber(0).WithAura(0, new byte[65]).TestObject;

    [Test]
    public void Reads_back_aura_block_when_header_decoder_is_injected()
    {
        Block block = AuRaGenesisShapedBlock();

        TestMemDb db = new();
        BlockStore store = new(db, new AuRaHeaderDecoder());
        store.Insert(block);

        Block? retrieved = store.Get(block.Number, block.Hash!);

        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.Header, Is.InstanceOf<AuRaBlockHeader>());
        Assert.That(((AuRaBlockHeader)retrieved.Header).AuRaSignature, Is.EqualTo(new byte[65]));
    }

    [Test]
    public void Base_header_decoder_misreads_aura_signature_as_nonce()
    {
        Block block = AuRaGenesisShapedBlock();

        TestMemDb db = new();
        // Persist with the AuRa-aware decoder, as the node does.
        new BlockStore(db, new AuRaHeaderDecoder()).Insert(block);

        // The pre-fix wiring left BlockStore without a decoder, so it fell back to the base HeaderDecoder.
        BlockStore baseStore = new(db);

        Assert.That(() => baseStore.Get(block.Number, block.Hash!), Throws.InstanceOf<RlpLimitException>());
    }

    [Test]
    public void Bad_block_store_preserves_aura_header_when_decoder_is_injected()
    {
        Block block = AuRaGenesisShapedBlock();

        TestMemDb db = new();
        BadBlockStore store = new(db, maxSize: 100, headerDecoder: new AuRaHeaderDecoder());
        store.Insert(block);

        Block retrieved = store.GetAll().Single();

        Assert.That(retrieved.Header, Is.InstanceOf<AuRaBlockHeader>());
        Assert.That(((AuRaBlockHeader)retrieved.Header).AuRaSignature, Is.EqualTo(new byte[65]));
        Assert.That(retrieved.Hash, Is.EqualTo(block.Hash));
    }
}
