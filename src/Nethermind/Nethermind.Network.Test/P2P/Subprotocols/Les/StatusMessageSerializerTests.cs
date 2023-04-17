// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Les;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class StatusMessageSerializerTests
    {
        [Test]
        public void RoundTripWithAllData()
        {
            StatusMessage statusMessage = new();
            statusMessage.ProtocolVersion = 3;
            statusMessage.NetworkId = 1;
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

            StatusMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, statusMessage, "f90176d18f70726f746f636f6c56657273696f6e03cb896e6574776f726b496401cb8668656164546483020080ea886865616448617368a0c89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc6c987686561644e756d04ed8b67656e6573697348617368a0044852b2a670ade5407e78fb2863c51de9fcb96542a07186fe3aeda6bb8a116dce8c616e6e6f756e63655479706501ce8c736572766548656164657273c0d18f7365727665436861696e53696e636580d4907365727665526563656e74436861696e8203e8d18f7365727665537461746553696e636501d4907365727665526563656e7453746174658201f4c987747852656c6179c0d28e666c6f77436f6e74726f6c2f424c8203e8d18f666c6f77436f6e74726f6c2f4d525264f84c8f666c6f77436f6e74726f6c2f4d5243f83ac802830249f0827530c60480830aae60c60680830f4240c60a808306ddd0c60f80830927c0c61180830f4240c613808306ddd0c614808303d090");
        }
    }
}
