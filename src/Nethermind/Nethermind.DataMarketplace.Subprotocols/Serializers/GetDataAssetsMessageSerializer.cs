// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class GetDataAssetsMessageSerializer : IMessageSerializer<GetDataAssetsMessage>
    {
        public byte[] Serialize(GetDataAssetsMessage message)
            => Array.Empty<byte>();

        public GetDataAssetsMessage Deserialize(byte[] bytes)
            => new GetDataAssetsMessage();
    }
}
