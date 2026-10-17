// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;
using Google.Protobuf;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

internal sealed class SessionOperation : IDisposable
{
    private readonly StreamingSession _session;
    private BlockWitness? _block;
    private Finalize? _finalize;
    private BlockWitness[] _blocks = [];
    private Begin? _predecessor;

    internal SessionBinding Binding { get; }
    internal ulong RequestId { get; }
    internal ByteString PreStateRoot { get; } = ByteString.Empty;
    internal long Size { get; }
    internal long WitnessItems { get; }
    internal int Duplicate { get; } = -1;
    internal bool Completed { get; set; }
    internal bool IsDisposed { get; private set; }
    internal BlockWitness? Block => _block?.Clone();
    internal Finalize? Finalize => _finalize?.Clone();
    internal Begin? Predecessor => _predecessor?.Clone();

    internal SessionOperation(StreamingSession session, SessionBinding binding, ulong requestId, BlockWitness block,
        ByteString preStateRoot, long size, long witnessItems, int duplicate)
    {
        _session = session;
        Binding = binding;
        RequestId = requestId;
        _block = block;
        PreStateRoot = preStateRoot;
        Size = size;
        WitnessItems = witnessItems;
        Duplicate = duplicate;
    }

    internal SessionOperation(StreamingSession session, SessionBinding binding, ulong requestId, Finalize finalize,
        BlockWitness[] blocks, Begin predecessor)
    {
        _session = session;
        Binding = binding;
        RequestId = requestId;
        _finalize = finalize;
        _blocks = blocks;
        _predecessor = predecessor;
    }

    internal BlockWitness[] CopyBlocks()
    {
        BlockWitness[] blocks = _blocks;
        BlockWitness[] copy = new BlockWitness[blocks.Length];
        for (int i = 0; i < copy.Length; i++) copy[i] = blocks[i].Clone();
        return copy;
    }

    internal void Release()
    {
        IsDisposed = true;
        _block = null;
        _finalize = null;
        _blocks = [];
        _predecessor = null;
    }

    internal Proof SignCurrent(EezAttestationSigner signer, ValueHash256 publicInputs, CancellationToken cancellation) =>
        _session.SignCurrent(this, signer, publicInputs, cancellation);

    public void Dispose() => _session.ReleaseOperation(this);
}
