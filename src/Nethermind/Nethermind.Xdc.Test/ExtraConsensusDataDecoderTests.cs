// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

internal class ExtraConsensusDataDecoderTests
{
    [TestCase("0xec01eae5a02671d34ee512c8a06f194dca9801ecfa8eb6a3590d1b73e50666b07f53b8958180820384c08201c2")]
    [TestCase("0xf9017f01f9017be5a0e4d73fbbbca7237d1ed1b3948a8a5d72d44a24cf44a5d1ed3e4d3811a83178c380820384f9014fb8417bc8bf119c8ead35c9f96a3cf1cf8fe9143e53f3ebbba832c70c1a49fdac3fab55b1dfe75a49bc0b418858f1ceefeb44a10e4b041a142933c8def2b62e3fc16b00b841bf36f8136c4743691e7b30e3bb59fd53a9eb504ac2d34afa2c067edf6512afa0112cd7a1414fc7d090ad567fbb9538d83b45c521679a458ef4df9759bad562fa01b841eac97089028181fa06df89c888cbfc74afd05a771a105d85fec32f5d1e8e4563326df77aa9155ee556b18bd8a536eb0b1dbd0eaeb0ed517545e516a43375a7d300b8417f4aecb525c685d512c7e39fa960ada9a32ab2e29d6a51d4340365c330d4cef16cc0bf77c7331ff2ac817e80d129e70ea69ea577ba97dfa87dc11f9499afa91701b841bb1044f6380d5b94724babe2283e864d0d291536fe6d2739fba648172df7ce9668006b67c14eb1aff399f883c4ea5cf0dd15360a97dd0a241dd7449c3513f237018201c2")]
    public void Decode_XdcExtraDataRlp_IsEquivalentAfterReencoding(string extraDataRlp)
    {
        ExtraConsensusDataDecoder decoder = new();
        Rlp.ValueDecoderContext context = new Rlp.ValueDecoderContext(Bytes.FromHexString(extraDataRlp));
        ExtraFieldsV2 decodedExtraData = decoder.Decode(ref context);

        Rlp encodedExtraData = decoder.Encode(decodedExtraData);

        ExtraFieldsV2 unencoded = decoder.Decode(new RlpStream(encodedExtraData.Bytes));

        unencoded.Should().BeEquivalentTo(decodedExtraData);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Decode_XdcExtraDataRlp_IsEquivalentAfterReencoding(bool useRlpStream)
    {
        ExtraFieldsV2 extraFields = new ExtraFieldsV2(1, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 1, 1), [new Signature(new byte[64], 0), new Signature(new byte[64], 0), new Signature(new byte[64], 0)], 0));
        ExtraConsensusDataDecoder decoder = new();
        var stream = new RlpStream(decoder.GetLength(extraFields));
        decoder.Encode(stream, extraFields);

        ExtraFieldsV2 decodedExtraData;
        if (useRlpStream)
        {
            stream.Position = 0;
            decodedExtraData = decoder.Decode(stream);
        }
        else
        {
            Rlp.ValueDecoderContext context = new Rlp.ValueDecoderContext(stream.Data);
            decodedExtraData = decoder.Decode(ref context);
        }

        decodedExtraData.Should().BeEquivalentTo(extraFields);
    }

}
