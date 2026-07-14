// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Blocks;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Encoding;

/// <summary>
/// Regression tests for a Gnosis/AuRa startup crash: <see cref="BlockStore"/> must decode blocks with the
/// chain's <see cref="IHeaderDecoder"/> (the <see cref="AuRaHeaderDecoder"/>), not the base
/// <see cref="HeaderDecoder"/>. The DI factory used to omit the decoder, so <see cref="BlockStore"/> fell back
/// to the base decoder. On an AuRa header whose <c>step</c> is empty (e.g. the Gnosis genesis, step 0), the base
/// decoder reads that empty item as a null mixHash and then reads the 65-byte AuRa signature as the 8-byte nonce
/// via a 32-byte-limited <c>DecodeUInt256</c>, throwing <see cref="RlpLimitException"/> during block-tree init.
/// </summary>
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
}
