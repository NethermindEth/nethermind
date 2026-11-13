// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Crypto;
using Nethermind.Eez.Config;
using Nethermind.Network;
using Nethermind.Network.Enr;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Network;

internal sealed class EezPeerRecordProvider(INodeRecordProvider inner, IEezConfig config,
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey key, IEthereumEcdsa ecdsa) : INodeRecordProvider, IDisposable
{
    private readonly Lock _lock = new();
    private readonly PrivateKey _key = key.Unprotect();
    private bool _disposed;
    private NodeRecord? _source;
    private NodeRecord? _advertised;

    public async ValueTask<NodeRecord> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        NodeRecord source = await inner.GetCurrentAsync(cancellationToken);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(source, _source)) return _advertised!;
            NodeRecord record = NodeRecord.FromEnrString(source.ToString());
            record.SetEntry(new PortEntry(config.SequencerP2PPort));
            record.EnrSequence = Math.Max(source.EnrSequence, _advertised is null ? 0 : checked(_advertised.EnrSequence + 1));
            new NodeRecordSigner(ecdsa, _key).Sign(record);
            _source = source;
            _advertised = record;
            return record;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _key.Dispose();
        }
    }

    private sealed class PortEntry(int port) : EnrContentEntry<byte[]>(Rlp.Encode(port).Bytes)
    {
        public override string Key => "eez";
        protected override int GetRlpLengthOfValue() => Value.Length;
        protected override void EncodeValue(ref RlpWriter writer) => writer.WriteEncoded(Value);
    }
}
