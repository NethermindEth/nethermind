// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Every changeset row of one covered block, read once and held in memory so that any number of workers can
/// fold any prefix of it without going back to the column.</summary>
internal sealed class BlockChangesets
{
    private BlockChangesets(ulong number, Hash256 hash, byte[][] rows)
    {
        Number = number;
        Hash = hash;
        Rows = rows;
    }

    public ulong Number { get; }

    public Hash256 Hash { get; }

    /// <summary>One packed changeset per transaction, in order.</summary>
    public byte[][] Rows { get; }

    /// <summary>Only when the column holds a row for every one of the block's transactions and the block hash it
    /// recorded is the one asked for.</summary>
    public static bool TryRead(TransactionChangesetStore store, ulong number, Hash256 hash, int transactionCount, [NotNullWhen(true)] out BlockChangesets? changesets)
    {
        changesets = null;
        if (transactionCount <= 0 || transactionCount > ChangesetKeyLayout.MaxTransactionIndex) return false;
        if (!store.TryGetBlockHash(number, out ValueHash256 indexed) || indexed != hash) return false;

        byte[][] rows = new byte[transactionCount][];
        int next = 0;
        using ISortedView view = store.OpenBetween(number, 0, (ushort)transactionCount);
        while (view.MoveNext())
        {
            if (!ChangesetKeyLayout.IsRowKey(view.CurrentKey)) continue;
            if (ChangesetKeyLayout.TransactionIndexOf(view.CurrentKey) != next) return false;

            rows[next++] = view.CurrentValue.ToArray();
        }

        if (next != transactionCount || !Readable(rows)) return false;

        changesets = new BlockChangesets(number, hash, rows);
        return true;
    }

    /// <summary>Every row is read once here, before any of them is used. A block is traced by as many workers as
    /// there are transactions, each folding its own prefix, so a row that only fails when a worker reaches it would
    /// cost a prefix replay per transaction; refusing the block instead costs the one replay the node did before the
    /// index existed.</summary>
    private static bool Readable(byte[][] rows)
    {
        foreach (byte[] row in rows)
        {
            try
            {
                ChangesetCodec.Enumerator entries = ChangesetCodec.Read(row);
                while (entries.MoveNext())
                {
                }
            }
            catch (InvalidDataException)
            {
                Flat.Metrics.UnreadableTransactionChangesetRows++;
                return false;
            }
        }

        return true;
    }

    /// <summary>The whole block folded, what the state looks like once its last transaction has run.</summary>
    public MidBlockOverlay FoldAll()
    {
        MidBlockOverlay overlay = new();
        overlay.Reset(Number);
        for (int i = 0; i < Rows.Length; i++) overlay.Fold((ushort)i, Rows[i]);
        return overlay;
    }
}
