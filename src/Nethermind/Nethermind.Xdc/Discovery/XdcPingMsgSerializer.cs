// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.Serializers;

namespace Nethermind.Xdc.Discovery;

public class XdcPingMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
    : PingMsgSerializer(ecdsa, nodeKey, nodeIdResolver)
{
    protected override byte MsgTypeByte => 5;
}
