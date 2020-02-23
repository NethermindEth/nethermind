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

using System.IO;
using System.Reflection;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.DataMarketplace.Subprotocols.Serializers;
using Nethermind.Network;
using Nethermind.Network.P2P;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Subprotocols.Test
{
    [TestFixture]
    public class MessageTests
    {
        public MessageTests()
        {
            DataAssetDecoder.Init();
            DataAssetRulesDecoder.Init();
            DataAssetRuleDecoder.Init();
            DataAssetProviderDecoder.Init();
        }
        
        [Test]
        public void Message_have_valid_protocol_and_can_serialize_and_deserialize()
        {
            Test(new ConsumerAddressChangedMessage(Address.SystemUser), new ConsumerAddressChangedMessageSerializer());
            Test(new DataAssetDataMessage(Keccak.OfAnEmptySequenceRlp, "client", "data", 1), new DataAssetDataMessageSerializer());
            Test(new DataAssetMessage(new DataAsset(Keccak.OfAnEmptyString, "name", "description", 1, DataAssetUnitType.Time, 1000, 10000, new DataAssetRules(new DataAssetRule(1),new DataAssetRule(2)), new DataAssetProvider(Address.SystemUser, "provider"))), new DataAssetMessageSerializer());
        }

        [Test]
        public void Invalid_messages_should_fail()
        {
            Assert.Throws<InvalidDataException>(() => Test(new ConsumerAddressChangedMessage(null), new ConsumerAddressChangedMessageSerializer()));
            Assert.Throws<InvalidDataException>(() => Test(new DataAssetDataMessage(null, "client", "data", 1), new DataAssetDataMessageSerializer()));
        }

        private void Test<T>(T message, IMessageSerializer<T> serializer) where T : P2PMessage
        {
            message.Protocol.Should().Be("ndm");

            FieldInfo fieldInfo = typeof(NdmMessageCode).GetField(message.GetType().Name.Replace("Message", string.Empty), BindingFlags.Static | BindingFlags.Public);
            message.PacketType.Should().Be((int) fieldInfo.GetValue(null));

            byte[] firstSer = serializer.Serialize(message);
            byte[] secondSer = serializer.Serialize(serializer.Deserialize(firstSer));

            firstSer.Should().BeEquivalentTo(secondSer);
        }
    }
}