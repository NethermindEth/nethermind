// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal abstract record Discv5Message : IDisposable
{
    public abstract Discv5MessageType MessageType { get; }

    public Discv5RequestId RequestId { get; }

    private IDisposable? _owner;

    protected Discv5Message(Discv5RequestId requestId, IDisposable? owner = null)
    {
        RequestId = requestId;
        _owner = owner;
    }

    public void Dispose()
    {
        DisposeCore();
        _owner?.Dispose();
        _owner = null;
    }

    protected virtual void DisposeCore()
    {
    }
}
