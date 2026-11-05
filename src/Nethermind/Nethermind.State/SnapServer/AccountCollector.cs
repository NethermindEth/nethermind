// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.State.SnapServer;

public class AccountCollector : RangeQueryVisitor.ILeafValueCollector, IDisposable
{
    private readonly DeferredRlpItemList.Builder _builder = new();

    public int Collect(in ValueHash256 path, CappedArray<byte> value)
    {
        if (value.IsNull)
        {
            AddAccount(path, [Rlp.EmptyListByte]);
            return 32 + 1;
        }

        RlpReader ctx = new(value.AsSpan());
        Account accnt = AccountDecoder.Instance.Decode(ref ctx)
            ?? throw new TrieException("An account leaf must contain a decodable account value.");
        Span<byte> slimRlp = stackalloc byte[AccountDecoder.Slim.GetLength(accnt)];
        RlpWriter writer = new(slimRlp);
        AccountDecoder.Slim.Encode(accnt, ref writer);
        AddAccount(path, slimRlp);
        return 32 + slimRlp.Length;
    }

    /// <summary>Hands the collected accounts over as a list; the collector must not be used afterwards.</summary>
    public IPathWithAccountList ToPathWithAccountList()
    {
        _builder.BeginRootContainer().Dispose();
        return new RlpPathWithAccountList(_builder.ToRlpItemList());
    }

    public void Dispose() => _builder.Dispose();

    private void AddAccount(in ValueHash256 path, ReadOnlySpan<byte> slimRlp)
    {
        // A root writer carries only the root entry index, so a fresh one per account appends to the same list.
        using DeferredRlpItemList.Builder.Writer pair = _builder.BeginRootContainer().BeginContainer();
        pair.WriteValue(path.Bytes);
        pair.WriteRaw(slimRlp);
    }
}
