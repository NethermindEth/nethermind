// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal abstract record Discv5Message : IDisposable
{
    public abstract MessageType MessageType { get; }

    public RequestId RequestId { get; }

    private ArrayPoolSpan<byte> _owner;
    private bool _hasOwner;

    protected Discv5Message(in RequestId requestId, ArrayPoolSpan<byte>? owner = null)
    {
        RequestId = requestId;
        if (owner is { } ownerValue)
        {
            _owner = ownerValue;
            _hasOwner = true;
        }
    }

    public void Dispose()
    {
        DisposeCore();
        if (_hasOwner)
        {
            _owner.Dispose();
            _hasOwner = false;
        }
    }

    protected virtual void DisposeCore()
    {
    }
}
