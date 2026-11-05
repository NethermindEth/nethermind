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
    private const int NullPairContentLength = Rlp.LengthOfKeccakRlp + 1;

    private readonly DeferredRlpItemList.Builder _builder = new();

    public int Collect(in ValueHash256 path, CappedArray<byte> value)
    {
        if (value.IsNull)
        {
            // A null leaf goes out as an empty account list, which is how AccountDecoder encodes a null account.
            Span<byte> nullPair = stackalloc byte[Rlp.LengthOfSequence(NullPairContentLength)];
            RlpWriter nullPairWriter = new(nullPair);
            nullPairWriter.StartSequence(NullPairContentLength);
            nullPairWriter.Encode(path);
            nullPairWriter.EncodeNullObject();
            _builder.BeginRootContainer().WriteRaw(nullPair);
            return 32 + 1;
        }

        if (!AccountDecoder.Instance.TryDecodeStruct(value.AsSpan(), out AccountStruct account))
            throw new TrieException("An account leaf must contain a decodable account value.");

        ValueHash256 storageRoot = account.StorageRoot;
        ReadOnlySpan<byte> slimStorageRoot = account.HasStorage ? storageRoot.Bytes : [];
        ReadOnlySpan<byte> slimCodeHash = account.HasCode ? account.CodeHash.Bytes : [];
        int accountContentLength = Rlp.LengthOf(account.Nonce) + Rlp.LengthOf(account.Balance)
            + Rlp.LengthOf(slimStorageRoot) + Rlp.LengthOf(slimCodeHash);
        int pairContentLength = Rlp.LengthOfKeccakRlp + Rlp.LengthOfSequence(accountContentLength);

        Span<byte> pair = stackalloc byte[Rlp.LengthOfSequence(pairContentLength)];
        RlpWriter writer = new(pair);
        writer.StartSequence(pairContentLength);
        writer.Encode(path);
        writer.StartSequence(accountContentLength);
        writer.Encode(account.Nonce);
        writer.Encode(account.Balance);
        writer.Encode(slimStorageRoot);
        writer.Encode(slimCodeHash);
        // A root writer carries only the root entry index, so a fresh one per pair appends to the same list.
        _builder.BeginRootContainer().WriteRaw(pair);
        return 32 + Rlp.LengthOfSequence(accountContentLength);
    }

    /// <summary>Hands the collected accounts over as a list; the collector must not be used afterwards.</summary>
    public IPathWithAccountList ToPathWithAccountList()
    {
        _builder.BeginRootContainer().Dispose();
        return new RlpPathWithAccountList(_builder.ToRlpItemList());
    }

    public void Dispose() => _builder.Dispose();
}
