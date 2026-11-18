// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;

internal sealed class SelfRecordResponseHandler(Node receiver, ulong minimumSequence, INodeRecordSigner? recordSigner = null)
    : ResponseHandler<NodesMsg>(MessageType.Nodes), IDisposable
{
    private const int MaxNodesResponseMessages = 16;

    private readonly INodeRecordSigner _recordSigner = recordSigner ?? new NodeRecordSigner(new Ecdsa());
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _lock = new();
    private bool _done;
    private int _totalMessages;
    private int _receivedMessages;
    private NodeRecord? _record;

    public override Task Task => _completion.Task;

    public override void Fail(Exception exception) => _completion.TrySetException(exception);

    public void Dispose()
    {
        lock (_lock)
        {
            _done = true;
        }
    }

    public NodeRecord? GetRecord()
    {
        if (!Task.IsCompleted)
        {
            throw new InvalidOperationException($"{nameof(GetRecord)} must be called after the response handler completes.");
        }

        return _record;
    }

    public override bool Handle(NodesMsg nodes)
    {
        if (nodes.Total <= 0 || nodes.Total > MaxNodesResponseMessages)
        {
            Complete();
            return true;
        }

        bool complete = false;
        lock (_lock)
        {
            if (_done)
            {
                return true;
            }

            if (_totalMessages != 0 && _totalMessages != nodes.Total)
            {
                complete = CompleteLocked();
            }
            else
            {
                _totalMessages = nodes.Total;
                _receivedMessages++;
                bool handledSelfRecord = TrySetRecord(nodes);

                if (handledSelfRecord || _receivedMessages >= nodes.Total)
                {
                    complete = CompleteLocked();
                }
            }
        }

        if (complete)
        {
            _completion.TrySetResult();
        }

        return true;
    }

    private bool TrySetRecord(NodesMsg nodes)
    {
        for (int i = 0; i < nodes.Records.Count; i++)
        {
            NodeRecord record = nodes.Records[i];
            if (record.EnrSequence < minimumSequence) continue;
            CompressedPublicKey? key = record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1);
            if (key is null || key.Bytes[0] != 2 + (receiver.Id.Bytes[^1] & 1) ||
                !key.Bytes.AsSpan(1).SequenceEqual(receiver.Id.Bytes.AsSpan(0, 32))) continue;

            if (record.Signature is not null && _recordSigner.Verify(record))
            {
                // A valid distance-zero response is still useful for its sequence
                // when this listener cannot use its endpoint.
                _record = record;
            }
            return true;
        }
        return false;
    }

    private void Complete()
    {
        bool complete;
        lock (_lock)
        {
            complete = CompleteLocked();
        }

        if (complete)
        {
            _completion.TrySetResult();
        }
    }

    private bool CompleteLocked()
    {
        if (_done)
        {
            return false;
        }

        _done = true;
        return true;
    }
}
