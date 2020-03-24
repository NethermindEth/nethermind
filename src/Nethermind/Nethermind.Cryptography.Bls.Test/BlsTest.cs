//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Nethermind.Cryptography.Bls.Test
{
    [TestClass]
    public class BlsTest
    {
        // Check against other test suites, e.g. Artemis
        //input: {privkey: '0x47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138',
        //message: '0x0000000000000000000000000000000000000000000000000000000000000000', domain: '0x0000000000000000'}
        //  output: '0xb9d1bf921b3dd048bdce38c2ceac2a2a8093c864881f2415f22b198de935ffa791707855c1656dc21a7af2d502bb46590151d645f062634c3b2cb79c4ed1c4a4b8b3f19f0f5c76965c651553e83d153ff95353735156eff77692f7a62ae653fb'

        // Older results (seem to be different):
        // https://gist.github.com/ChihChengLiang/328bc1db5d2a47950e5364c11f23052a

        private static IList<byte[]> Domains => new List<byte[]>
        {
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 },
            new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            new byte[] { 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef },
            new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff },
        };

        private static IList<byte[]> MessageHashes => new List<byte[]>
        {
            Enumerable.Repeat((byte)0x00, 32).ToArray(),
            Enumerable.Repeat((byte)0x56, 32).ToArray(),
            Enumerable.Repeat((byte)0xab, 32).ToArray(),
        };

        private static IList<string> PrivateKeys => new List<string>
        {
            "263dbd792f5b1be47ed85f8938c0f29586af0d3ac7b977f21c278fe1462040e3",
            "47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138",
            "328388aff0d4a5b7dc9205abd374e7e98f3cd9f3418edb4eafda5fb16473d216",
        };

        [DataTestMethod]
        [DynamicData(nameof(Case03PrivateToPublicKeyData), DynamicDataSourceType.Method)]
        public void Case03PrivateToPublicKey(byte[] privateKey, byte[] expected)
        {
            // Arrange
            var parameters = new BLSParameters()
            {
                PrivateKey = privateKey
            };
            Console.WriteLine("Input:");
            Console.WriteLine("Private Key: [{0}] {1}", privateKey.Length, HexMate.Convert.ToHexString(privateKey));

            // Act
            using var bls = new BLSHerumi(parameters);
            var result = new byte[48];
            var success = bls.TryExportBlsPublicKey(result.AsSpan(), out var bytesWritten);

            Console.WriteLine("Output:");
            Console.WriteLine("Public Key: [{0}] {1}", result.Length, HexMate.Convert.ToHexString(result));

            // Assert
            result.ShouldBe(expected);
        }

        public static IEnumerable<object[]> Case03PrivateToPublicKeyData()
        {
            yield return new object[]
            {
                HexMate.Convert.FromHexString("263dbd792f5b1be47ed85f8938c0f29586af0d3ac7b977f21c278fe1462040e3"),
                HexMate.Convert.FromHexString("a491d1b0ecd9bb917989f0e74f0dea0422eac4a873e5e2644f368dffb9a6e20fd6e10c1b77654d067c0618f6e5a7f79a"),
            };

            yield return new object[]
            {
                HexMate.Convert.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138"),
                HexMate.Convert.FromHexString("b301803f8b5ac4a1133581fc676dfedc60d891dd5fa99028805e5ea5b08d3491af75d0707adab3b70c6a6a580217bf81"),
            };

            yield return new object[]
            {
                HexMate.Convert.FromHexString("328388aff0d4a5b7dc9205abd374e7e98f3cd9f3418edb4eafda5fb16473d216"),
                HexMate.Convert.FromHexString("b53d21a4cfd562c469cc81514d4ce5a6b577d8403d32a394dc265dd190b47fa9f829fdd7963afdf972e5e77854051f6f"),
            };

            //return PrivateKeys.Select(x => new object[] { HexMate.Convert.FromHexString(x) });
        }

        [DataTestMethod]
        [DynamicData(nameof(Case06AggregateSignaturesData), DynamicDataSourceType.Method)]
        public void Case06AggregateSignatures(byte[][] signatures, byte[] expected)
        {
            // Arrange
            var inputSpan = new Span<byte>(new byte[signatures.Length * 96]);
            Console.WriteLine("Input: [{0}]", signatures.Length);
            for (var index = 0; index < signatures.Length; index++)
            {
                signatures[index].CopyTo(inputSpan.Slice(index * 96));
                Console.WriteLine("Signature({0}): [{1}] {2}", index, signatures[index].Length, HexMate.Convert.ToHexString(signatures[index]));
            }

            // Act
            using var bls = new BLSHerumi(new BLSParameters());
            var result = new byte[96];
            var success = bls.TryAggregateSignatures(inputSpan, result, out var bytesWritten);

            Console.WriteLine("Output:");
            Console.WriteLine("Combined: {0} [{1}] {2}", success, bytesWritten, HexMate.Convert.ToHexString(result));

            // Assert
            result.ShouldBe(expected);
        }

        public static IEnumerable<object[]> Case06AggregateSignaturesData()
        {
            yield return new object[]
            {
                new byte[][] {
                    HexMate.Convert.FromHexString("97004641c3f3c9973e5d5064578e7b87230655905546f5e95469dfa41cfae49f3741112cdf425f8a3f9d0fdde2f4980516b1fd6748d87a234589f065145cfe9c697cc6a61211c9322ad4c279c20b8d943c8c2f1dd13fc0418cb2dac4d0a9e34d"),
                    HexMate.Convert.FromHexString("b9d1bf921b3dd048bdce38c2ceac2a2a8093c864881f2415f22b198de935ffa791707855c1656dc21a7af2d502bb46590151d645f062634c3b2cb79c4ed1c4a4b8b3f19f0f5c76965c651553e83d153ff95353735156eff77692f7a62ae653fb"),
                    HexMate.Convert.FromHexString("a234bc1a0acd31b3a8758247a0ea5fbf1a08a5ecdf82037993ec5bfd68836ac01b966b025e55c100e30d18adb327710315b9d4905f108b4be9fb58f7be5690f5c061de2e02fca47f79202bb8ddfa64d32aa0914384941f13f400c5eb1b96965f"),
                },
                HexMate.Convert.FromHexString("a02d658fa52328a088903713509db8faaeaf0daf68415df055fe4f1fa0d848bd8ea6428f9852d4f7f771b496f0e24930106e589fa1dd89f0d2263431f03cdf3950df96f075e89255c909ba8862f5ae406e08a9d46e885ceee3d8863b1d5e6507"),
            };

            yield return new object[]
            {
                new byte[][] {
                    HexMate.Convert.FromHexString("89c3c5d4f8eca4a7f9a9345200b42830c3cdf5cfa4ed3a821c7408d161d55731551a043bf634be5682772d0b9049776804327bcac3ba43a8caf80b19685e85668997c2f8390a805155e752727421bf73651cbd09289c6444a0aaece96f747d27"),
                    HexMate.Convert.FromHexString("b3a8aea823c7ab53be02d8b3d80094188be3d99924efaaee6eff9a7ca876710bf72b1b1de4afb32f5acd35bfbf8a447107efc005f04726c9163d1484b2db64badcfe584c540f2c4cc8d3c0d6dd98dc584fe95552de65bcc58bf606e2b87660ff"),
                    HexMate.Convert.FromHexString("879143289b766dd2adca59a597aadfb87748976f321d47e1892571272af12ea6f3916945ef9a6d80448e47f1d81dbbb513dc73e40e96a0c5601a521d79251c0a03183747f5d9ce78bfe23f77c3c7b33d8b0d2515ce6b935fed3277547542425a"),
                },
                HexMate.Convert.FromHexString("b3087e904c5205bb9a319af70a3aef7a19b74be05407db01a1aa0fad32e915ee2fd1fcb03d649fb90d41f0e6bb9faf32190963e25c98a865cf1851e262554ec7c7a8a2ebb7f27fe2278b544dca30cadb0e5cfcefb87244104e3daf952e5af476"),
            };

            yield return new object[]
            {
                new byte[][] {
                    HexMate.Convert.FromHexString("a8715f2ac11675cf105426368a22a2304d9960a82366c63f96d2e56edf1fda04ba3c6e3d99794d0ead42c2936b24b4f20075d4ccb7edbdff8ae018169a7990b878bb25dda59cb828ab65d3a828bc29b4e0732b7dda1121a6377a34b717733dfd"),
                    HexMate.Convert.FromHexString("93f2210d2eb75824fd2c27bc2ad8527cc2bbf1db8d3fb707e909831955f2172b204d6743150c56295a86089f437d32920576f462d28ef53ccbe45ac1c96a50427b29b4c7c4f89a92686db6be2dfdb0ea44e1442ba3be01ed37e61fafb519a9ec"),
                    HexMate.Convert.FromHexString("b28848b57e3004f17839bce988aecceb4b5dee566d392d8b9f9e7b57c2295fbde9bf102eeff1a89a6bc8bf8b2dc1405c0c8b0c9bea51a8741c6973f918bcd1421a8d4ced2ffeecb9430f7cedf6883809b3a9bcd866956c455682b6d480396a69"),
                },
                HexMate.Convert.FromHexString("b388c574412b766fba799969e1c478a45319041a811e56519c372bae6584d91d214564802e6fa98950f6acb78f5283e10e3e51a85a6a6d546f3d34a6b884a1c6812708e42c548f4fb7908b4130e32e1a39e112b0d838105f79b358e630e6e74a"),
            };

            yield return new object[]
            {
                new byte[][] {
                    HexMate.Convert.FromHexString("abd8f756eda91a448ea8e9ffd1f31d082e63b2848242192bd2c1e1fcae95613702ed03181118c8ba8440a580bef8780f194f196877819998efb85f93c13e43da16cd12a7f3ef99897dede35c0ae1a8f8d66760c2feb201a76111ba5b935945eb"),
                    HexMate.Convert.FromHexString("b6e6d8aa224c5edc502680fae130ac1eb1e769f18b73eee4bafb782c6c244afb8517df2007de038c8b301472543a3eb013e9291ec216d23cee57b8f75d88ab62b27f0902e8d5d96e6d43c4253b76e599983e2ccc33a259f6f74778f2063ef79e"),
                    HexMate.Convert.FromHexString("b39d994a77996c719141d0b2cc2d9184dca9c1369a58265befccf7f36c82ee4be93e623257a329c6426533354aa4d8f60fc1d41b7e56b8fbb1bc118a275d867b432e6bed43cb2f3ddbf62c4a666f3ff042c235a9c7b89d515373ea0f2a0e4e47"),
                },
                HexMate.Convert.FromHexString("ab9f94aae5846301760be418a6f0ccb96178fbfab15ad50755da993c2d1ea4278638eb5157002f0f31824e42d83d6eb018271f6f28f27582bc2dd54820a74af898c6e6ab4cc24368465f0af6026501896f2dbc3f26a55ff0bce2dc378b070e08"),
            };
        }

        [DataTestMethod]
        [DynamicData(nameof(Case07AggregatePublicKeysData), DynamicDataSourceType.Method)]
        public void Case07AggregatePublicKeys(byte[][] publicKeys, byte[] expected)
        {
            // Arrange
            var inputSpan = new Span<byte>(new byte[publicKeys.Length * 48]);
            Console.WriteLine("Input: [{0}]", publicKeys.Length);
            for (var index = 0; index < publicKeys.Length; index++)
            {
                publicKeys[index].CopyTo(inputSpan.Slice(index * 48));
                Console.WriteLine("Public Key({0}): [{1}] {2}", index, publicKeys[index].Length, HexMate.Convert.ToHexString(publicKeys[index]));
            }

            // Act
            using var bls = new BLSHerumi(new BLSParameters());
            var result = new byte[48];
            var success = bls.TryAggregatePublicKeys(inputSpan, result, out var bytesWritten);

            Console.WriteLine("Output:");
            Console.WriteLine("Combined: {0} [{1}] {2}", success, bytesWritten, HexMate.Convert.ToHexString(result));

            // Assert
            result.ShouldBe(expected);
        }

        public static IEnumerable<object[]> Case07AggregatePublicKeysData()
        {
            yield return new object[]
            {
                new byte[][] {
                    HexMate.Convert.FromHexString("a491d1b0ecd9bb917989f0e74f0dea0422eac4a873e5e2644f368dffb9a6e20fd6e10c1b77654d067c0618f6e5a7f79a"),
                    HexMate.Convert.FromHexString("b301803f8b5ac4a1133581fc676dfedc60d891dd5fa99028805e5ea5b08d3491af75d0707adab3b70c6a6a580217bf81"),
                    HexMate.Convert.FromHexString("b53d21a4cfd562c469cc81514d4ce5a6b577d8403d32a394dc265dd190b47fa9f829fdd7963afdf972e5e77854051f6f"),
                },
                HexMate.Convert.FromHexString("a095608b35495ca05002b7b5966729dd1ed096568cf2ff24f3318468e0f3495361414a78ebc09574489bc79e48fca969"),
            };
        }
    }
}
