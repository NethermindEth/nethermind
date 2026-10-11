// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

/// <summary>Adds <c>leanq = [1, udp_port]</c> to the local node record once the ethp2p listener is bound.</summary>
/// <remarks>
/// The record is re-signed with the node key above both the inner record's sequence and every sequence this provider has
/// returned, so each published record has its own sequence and peers holding an older one fetch the new one.
/// </remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed class LeanEthp2pNodeRecordProvider(INodeRecordProvider inner, LeanEthp2pHost host, IEcdsa ecdsa,
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey) : INodeRecordProvider
{
    private readonly NodeRecordSigner _signer = new(ecdsa, nodeKey.Unprotect());
    private readonly Lock _lock = new();
    private (NodeRecord Inner, int Port, NodeRecord Record)? _cached;
    private ulong _lastSequence;

    public async ValueTask<NodeRecord> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        NodeRecord record = await inner.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            if (host.LocalEndPoint is not IPEndPoint endPoint)
            {
                _lastSequence = Math.Max(_lastSequence, record.EnrSequence);
                return record;
            }
            if (_cached is { } cached && cached.Inner == record && cached.Port == endPoint.Port) return cached.Record;
            RlpReader reader = new(record.ToRlpBytes());
            NodeRecord extended = _signer.Deserialize(ref reader);
            extended.SetEntry(new LeanqEntry(endPoint.Port));
            // The inner provider may later sign a different record at inner + 1, so the extension also stays above every
            // sequence already returned.
            extended.EnrSequence = _lastSequence = Math.Max(_lastSequence, record.EnrSequence) + 1;
            _signer.Sign(extended);
            _cached = (record, endPoint.Port, extended);
            return extended;
        }
    }
}
