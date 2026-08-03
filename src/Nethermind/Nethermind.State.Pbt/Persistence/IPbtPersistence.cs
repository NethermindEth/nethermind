// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Durable atomic storage for one canonical EIP-8297 state.</summary>
public interface IPbtPersistence
{
    IReader CreateReader();

    IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot, WriteFlags flags);

    void Flush();

    public interface IReader : IDisposable
    {
        StateId CurrentState { get; }
        ValueHash256 CurrentRoot { get; }

        ValueHash256? GetLeaf(PbtFullKey key);
        IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves();
        IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix);

        byte[]? GetNode(PbtFullKey locator);
        IEnumerable<KeyValuePair<PbtFullKey, byte[]>> EnumerateNodes();

        ulong GetCodeReference(in ValueHash256 codeHash);
    }

    public interface IWriteBatch : IDisposable
    {
        void SetLeaf(PbtFullKey key, ValueHash256? value);
        void SetNode(PbtFullKey locator, ReadOnlySpan<byte> encoding);
        void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount);
    }
}
