// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Core.Metric;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Les;
using Nethermind.Network.P2P.Subprotocols.NodeData;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Wit;

namespace Nethermind.Network.P2P;

public readonly record struct VersionedProtocol(string Protocol, byte Version);

public record struct P2PMessageKey(VersionedProtocol Protocol, int PacketType) : IMetricLabels
{
    private static readonly FrozenDictionary<(string, int), string> MessageNames =
        FromMessageCodeClass(Contract.P2P.Protocol.P2P, typeof(P2PMessageCode))

            .Concat(FromMessageCodeClass(Contract.P2P.Protocol.Eth, typeof(Eth62MessageCode)))
            .Concat(FromMessageCodeClass(Contract.P2P.Protocol.Eth, typeof(Eth63MessageCode)))
            .Concat(FromMessageCodeClass(Contract.P2P.Protocol.Eth, typeof(Eth65MessageCode)))
            .Concat(FromMessageCodeClass(Contract.P2P.Protocol.Eth, typeof(Eth66MessageCode)))
            .Concat(FromMessageCodeClass(Contract.P2P.Protocol.Eth, typeof(Eth68MessageCode)))

            .Concat(FromMessageCodeClass(Contract.P2P.Protocol.NodeData, typeof(NodeDataMessageCode)))
            .Concat(FromMessageCodeClass(Contract.P2P.Protocol.Wit, typeof(WitMessageCode)))
            .Concat(FromMessageCodeClass(Contract.P2P.Protocol.Les, typeof(LesMessageCode)))

            .Concat(FromMessageCodeClass(Contract.P2P.Protocol.Snap, typeof(SnapMessageCode)))

            .ToFrozenDictionary();

    private static IEnumerable<KeyValuePair<(string, int), string>> FromMessageCodeClass(string protocol, Type classType) =>
        classType.GetFields(
                BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType.IsAssignableTo(typeof(int)))
            .Select(field => KeyValuePair.Create((protocol, (int)field.GetValue(null)), field.Name));

    private string[]? _labels = null;
    public string[] Labels => _labels ??= CalculateLabel();

    private string[] CalculateLabel()
    {
        return [$"{Protocol.Protocol}{Protocol.Version}", GetMessageType()];
    }

    private string GetMessageType()
    {
        if (!MessageNames.TryGetValue((Protocol.Protocol, PacketType), out string messageName))
        {
#if DEBUG
            throw new NotImplementedException($"Message name for protocol {Protocol.Protocol} message id {PacketType} not set.");
#else
            return PacketType.ToString(); // Just use the integer directly then
#endif
        }
        return messageName;
    }

    public override string ToString()
    {
        return string.Join(',', Labels);
    }
}
