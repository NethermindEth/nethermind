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

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Les;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class StatusMessageSerializerTests
    {
        [Test]
        public void RoundTripWithAllData()
        {
            StatusMessage statusMessage = new StatusMessage();
            statusMessage.ProtocolVersion = 3;
            statusMessage.ChainId = 1;
            statusMessage.TotalDifficulty = 131200;
            statusMessage.BestHash = Keccak.Compute("1");
            statusMessage.HeadBlockNo = 4;
            statusMessage.GenesisHash = Keccak.Compute("0");
            statusMessage.AnnounceType = 1;
            statusMessage.ServeHeaders = true;
            statusMessage.ServeChainSince = 0;
            statusMessage.ServeRecentChain = 1000;
            statusMessage.ServeStateSince = 1;
            statusMessage.ServeRecentState = 500;
            statusMessage.TxRelay = true;
            statusMessage.BufferLimit = 1000;
            statusMessage.MaximumRechargeRate = 100;
            statusMessage.MaximumRequestCosts = CostTracker.DefaultRequestCostTable;

            StatusMessageSerializer serializer = new StatusMessageSerializer();
            byte[] bytes = serializer.Serialize(statusMessage);
            // [["protocolVersion", 3], ["networkId", 1], ["headTd", 131200], ["headHash", 0xc89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc6], ["headNum", 4], ["genesisHash", 0x044852b2a670ade5407e78fb2863c51de9fcb96542a07186fe3aeda6bb8a116d], ["announceType", 1], ["serveHeaders", []], ["serveChainSince", 0], ["serveRecentChain", 1000], ["serveStateSince", 1], ["serveRecentState", 500], ["txRelay", []], ["flowControl/BL", 1000], ["flowControl/MRC", 100], ["flowControl/MRR", [[2, 150000, 30000], [4, 0, 700000], [6, 0, 1000000], [10, 0, 450000], [15, 0, 600000], [17, 0, 1000000], [19, 0, 450000], [20, 0, 250000]]]]
            byte[] expectedBytes = Bytes.FromHexString("f90176d18f70726f746f636f6c56657273696f6e03cb896e6574776f726b496401cb8668656164546483020080ea886865616448617368a0c89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc6c987686561644e756d04ed8b67656e6573697348617368a0044852b2a670ade5407e78fb2863c51de9fcb96542a07186fe3aeda6bb8a116dce8c616e6e6f756e63655479706501ce8c736572766548656164657273c0d18f7365727665436861696e53696e636580d4907365727665526563656e74436861696e8203e8d18f7365727665537461746553696e636501d4907365727665526563656e7453746174658201f4c987747852656c6179c0d28e666c6f77436f6e74726f6c2f424c8203e8d18f666c6f77436f6e74726f6c2f4d525264f84c8f666c6f77436f6e74726f6c2f4d5243f83ac802830249f0827530c60480830aae60c60680830f4240c60a808306ddd0c60f80830927c0c61180830f4240c613808306ddd0c614808303d090");

            Assert.True(Bytes.AreEqual(bytes, expectedBytes), "bytes");

            StatusMessage deserialized = serializer.Deserialize(bytes);

            Assert.AreEqual(statusMessage.ProtocolVersion, deserialized.ProtocolVersion, $"{nameof(deserialized.ProtocolVersion)}");
            Assert.AreEqual(statusMessage.ChainId, deserialized.ChainId, $"{nameof(deserialized.ChainId)}");
            Assert.AreEqual(statusMessage.TotalDifficulty, deserialized.TotalDifficulty, $"{nameof(deserialized.TotalDifficulty)}");
            Assert.AreEqual(statusMessage.BestHash, deserialized.BestHash, $"{nameof(deserialized.BestHash)}");
            Assert.AreEqual(statusMessage.HeadBlockNo, deserialized.HeadBlockNo, $"{nameof(deserialized.HeadBlockNo)}");
            Assert.AreEqual(statusMessage.GenesisHash, deserialized.GenesisHash, $"{nameof(deserialized.GenesisHash)}");
            Assert.AreEqual(statusMessage.AnnounceType, deserialized.AnnounceType, $"{nameof(deserialized.AnnounceType)}");
            Assert.AreEqual(statusMessage.ServeHeaders, deserialized.ServeHeaders, $"{nameof(deserialized.ServeHeaders)}");
            Assert.AreEqual(statusMessage.ServeChainSince, deserialized.ServeChainSince, $"{nameof(deserialized.ServeChainSince)}");
            Assert.AreEqual(statusMessage.ServeRecentChain, deserialized.ServeRecentChain, $"{nameof(deserialized.ServeRecentChain)}");
            Assert.AreEqual(statusMessage.ServeStateSince, deserialized.ServeStateSince, $"{nameof(deserialized.ServeStateSince)}");
            Assert.AreEqual(statusMessage.ServeRecentState, deserialized.ServeRecentState, $"{nameof(deserialized.ServeRecentState)}");
            Assert.AreEqual(statusMessage.TxRelay, deserialized.TxRelay, $"{nameof(deserialized.TxRelay)}");
            Assert.AreEqual(statusMessage.BufferLimit, deserialized.BufferLimit, $"{nameof(deserialized.BufferLimit)}");
            Assert.AreEqual(statusMessage.MaximumRechargeRate, deserialized.MaximumRechargeRate, $"{nameof(deserialized.MaximumRechargeRate)}");
            Assert.AreEqual(statusMessage.MaximumRequestCosts, deserialized.MaximumRequestCosts, $"{nameof(deserialized.MaximumRequestCosts)}");
        }
    }
}
