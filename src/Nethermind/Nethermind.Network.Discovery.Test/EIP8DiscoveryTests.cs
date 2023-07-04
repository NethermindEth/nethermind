// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class EIP8DiscoveryTests
    {
        private readonly PrivateKey _privateKey = new("b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291");
        private readonly IMessageSerializationService _messageSerializationService;

        public EIP8DiscoveryTests()
        {
            _messageSerializationService = Build.A.SerializationService().WithDiscovery(_privateKey).TestObject;
        }

        [Test]
        public void PingFormatTest()
        {
            string encodedPing = "e9614ccfd9fc3e74360018522d30e1419a143407ffcce748de3e22116b7e8dc92ff74788c0b6663a" +
                                 "aa3d67d641936511c8f8d6ad8698b820a7cf9e1be7155e9a241f556658c55428ec0563514365799a" +
                                 "4be2be5a685a80971ddcfa80cb422cdd0101ec04cb847f000001820cfa8215a8d790000000000000" +
                                 "000000000000000000018208ae820d058443b9a3550102";
            PingMsg ping = _messageSerializationService.Deserialize<PingMsg>(Bytes.FromHexString(encodedPing));
            Assert.That(ping.Version, Is.EqualTo(4));

            encodedPing = "577be4349c4dd26768081f58de4c6f375a7a22f3f7adda654d1428637412c3d7fe917cadc56d4e5e" +
                          "7ffae1dbe3efffb9849feb71b262de37977e7c7a44e677295680e9e38ab26bee2fcbae207fba3ff3" +
                          "d74069a50b902a82c9903ed37cc993c50001f83e82022bd79020010db83c4d001500000000abcdef" +
                          "12820cfa8215a8d79020010db885a308d313198a2e037073488208ae82823a8443b9a355c5010203" +
                          "040531b9019afde696e582a78fa8d95ea13ce3297d4afb8ba6433e4154caa5ac6431af1b80ba7602" +
                          "3fa4090c408f6b4bc3701562c031041d4702971d102c9ab7fa5eed4cd6bab8f7af956f7d565ee191" +
                          "7084a95398b6a21eac920fe3dd1345ec0a7ef39367ee69ddf092cbfe5b93e5e568ebc491983c09c7" +
                          "6d922dc3";
            ping = _messageSerializationService.Deserialize<PingMsg>(Bytes.FromHexString(encodedPing));
            Assert.That(ping.Version, Is.EqualTo(555));
        }

        [Test]
        public void PongFormatTest()
        {
            string encodedPong = "09b2428d83348d27cdf7064ad9024f526cebc19e4958f0fdad87c15eb598dd61d08423e0bf66b206" +
                                 "9869e1724125f820d851c136684082774f870e614d95a2855d000f05d1648b2d5945470bc187c2d2" +
                                 "216fbe870f43ed0909009882e176a46b0102f846d79020010db885a308d313198a2e037073488208" +
                                 "ae82823aa0fbc914b16819237dcd8801d7e53f69e9719adecb3cc0e790c57e91ca4461c9548443b9" +
                                 "a355c6010203c2040506a0c969a58f6f9095004c0177a6b47f451530cab38966a25cca5cb58f0555" +
                                 "42124e";
            PongMsg pong = _messageSerializationService.Deserialize<PongMsg>(Bytes.FromHexString(encodedPong));
            Assert.That(pong.ExpirationTime, Is.EqualTo(1136239445));
        }

        [Test]
        public void FindNodeFormatTest()
        {
            string encodedFindNode = "c7c44041b9f7c7e41934417ebac9a8e1a4c6298f74553f2fcfdcae6ed6fe53163eb3d2b52e39fe91" +
                                     "831b8a927bf4fc222c3902202027e5e9eb812195f95d20061ef5cd31d502e47ecb61183f74a504fe" +
                                     "04c51e73df81f25c4d506b26db4517490103f84eb840ca634cae0d49acb401d8a4c6b6fe8c55b70d" +
                                     "115bf400769cc1400f3258cd31387574077f301b421bc84df7266c44e9e6d569fc56be0081290476" +
                                     "7bf5ccd1fc7f8443b9a35582999983999999280dc62cc8255c73471e0a61da0c89acdc0e035e260a" +
                                     "dd7fc0c04ad9ebf3919644c91cb247affc82b69bd2ca235c71eab8e49737c937a2c396";
            FindNodeMsg msg = _messageSerializationService.Deserialize<FindNodeMsg>(Bytes.FromHexString(encodedFindNode));
            Assert.That(msg.ExpirationTime, Is.EqualTo(1136239445));
        }

        [Test]
        public void NeighborsFormatTest()
        {
            string encodedNeighbors = "c679fc8fe0b8b12f06577f2e802d34f6fa257e6137a995f6f4cbfc9ee50ed3710faf6e66f932c4c8" +
                                      "d81d64343f429651328758b47d3dbc02c4042f0fff6946a50f4a49037a72bb550f3a7872363a83e1" +
                                      "b9ee6469856c24eb4ef80b7535bcf99c0004f9015bf90150f84d846321163782115c82115db84031" +
                                      "55e1427f85f10a5c9a7755877748041af1bcd8d474ec065eb33df57a97babf54bfd2103575fa8291" +
                                      "15d224c523596b401065a97f74010610fce76382c0bf32f84984010203040101b840312c55512422" +
                                      "cf9b8a4097e9a6ad79402e87a15ae909a4bfefa22398f03d20951933beea1e4dfa6f968212385e82" +
                                      "9f04c2d314fc2d4e255e0d3bc08792b069dbf8599020010db83c4d001500000000abcdef12820d05" +
                                      "820d05b84038643200b172dcfef857492156971f0e6aa2c538d8b74010f8e140811d53b98c765dd2" +
                                      "d96126051913f44582e8c199ad7c6d6819e9a56483f637feaac9448aacf8599020010db885a308d3" +
                                      "13198a2e037073488203e78203e8b8408dcab8618c3253b558d459da53bd8fa68935a719aff8b811" +
                                      "197101a4b2b47dd2d47295286fc00cc081bb542d760717d1bdd6bec2c37cd72eca367d6dd3b9df73" +
                                      "8443b9a355010203b525a138aa34383fec3d2719a0";
            NeighborsMsg msg = _messageSerializationService.Deserialize<NeighborsMsg>(Bytes.FromHexString(encodedNeighbors));
            Assert.That(msg.ExpirationTime, Is.EqualTo(1136239445));
            Assert.That(msg.Nodes[0].IdHash.ToString(true), Is.EqualTo("0xca25217b2fbd0ae6d435be1f0f99282a930dac3ca4c3358900da74e7caa7eee4"));
            Assert.That(msg.Nodes[1].IdHash.ToString(true), Is.EqualTo("0x6b1e430439aaf971bf408b99c0097bc560238d10d5642e44cdd89d8192bb0554"));
            Assert.That(msg.Nodes[2].IdHash.ToString(true), Is.EqualTo("0x7d38ab8183dce7e53e5d5536956a8de7ab45aaeb12948848a30efae067f2b2e9"));
            Assert.That(msg.Nodes[3].IdHash.ToString(true), Is.EqualTo("0xaaa7e418c80aa0a3e9a29af1ecf831fdcc1093510d0c73febaea674e69e7e58b"));
        }
    }
}
