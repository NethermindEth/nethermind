// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;

internal sealed class SelfRecordResponseHandler(Node receiver, ulong minimumSequence, IDiscv5RecordFilter recordFilter)
    : ResponseHandler<NodesMsg>(MessageType.Nodes), IDisposable
{
    private const int MaxNodesResponseMessages = 16;

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _allowNonRoutableRelays = receiver.Address.Address.IsLoopbackOrPrivateOrLinkLocal;
    private readonly Lock _lock = new();
    private bool _done;
    private int _totalMessages;
    private int _receivedMessages;
    private NodeRecord? _record;

    public override Task Task => _completion.Task;

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
                TrySetRecord(nodes);

                if (_record is not null || _receivedMessages >= nodes.Total)
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

    private void TrySetRecord(NodesMsg nodes)
    {
        for (int i = 0; i < nodes.Records.Count; i++)
        {
            NodeRecord record = nodes.Records[i];
            if (record.EnrSequence >= minimumSequence &&
                KademliaAdapter.IsAcceptableNodeRecord(record, receiver.Id.Hash, _allowNonRoutableRelays, recordFilter))
            {
                _record = record;
                return;
            }
        }
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
