// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;

namespace Nethermind.Core.BlockAccessLists;

// Stateful forward-only view over a BlockAccessList for per-index validation.
// The caller invokes GetChangesAtIndex with monotonically non-decreasing indices;
// cursors advance instead of rescanning each list from position 0, turning an
// otherwise-quadratic validation loop into a single linear pass.
//
// Safe across BlockAccessList.Seal() calls and across growth of the generated
// BAL between calls: cursors are keyed by AccountChanges / SlotChanges reference,
// not by array index, and new accounts / slots are picked up lazily by walking
// _bal.AccountChanges on each call.
public sealed class BalIndexChangeIterator(BlockAccessList bal)
{
    private readonly BlockAccessList _bal = bal;
    private readonly Dictionary<AccountChanges, AccountCursor> _accountCursors = [];
    private ushort _lastIndex;

    public IEnumerable<ChangeAtIndex> GetChangesAtIndex(ushort index)
    {
        Debug.Assert(index >= _lastIndex, "BalIndexChangeIterator requires monotonic index");
        _lastIndex = index;

        foreach (AccountChanges ac in _bal.AccountChanges)
        {
            if (!_accountCursors.TryGetValue(ac, out AccountCursor? cursor))
            {
                cursor = new AccountCursor();
                _accountCursors[ac] = cursor;
            }
            yield return cursor.ChangeAtIndex(ac, index);
        }
    }

    private sealed class AccountCursor
    {
        private int _balancePos;
        private int _noncePos;
        private int _codePos;
        private Dictionary<SlotChanges, int>? _slotPositions;

        public ChangeAtIndex ChangeAtIndex(AccountChanges ac, ushort index)
        {
            BalanceChange? balance = Advance(ac.BalanceChanges, ref _balancePos, index);
            NonceChange? nonce = Advance(ac.NonceChanges, ref _noncePos, index);
            CodeChange? code = Advance(ac.CodeChanges, ref _codePos, index);

            bool isPostExecutionSystemContract =
                ac.Address == Eip7002Constants.WithdrawalRequestPredeployAddress ||
                ac.Address == Eip7251Constants.ConsolidationRequestPredeployAddress;

            return new ChangeAtIndex(
                ac.Address,
                balance,
                nonce,
                code,
                SlotChangesAtIndex(ac, index),
                isPostExecutionSystemContract ? 0 : ac.StorageReads.Count);
        }

        private static T? Advance<T>(IReadOnlyList<T> list, ref int pos, ushort index)
            where T : struct, IIndexedChange
        {
            while (pos < list.Count && list[pos].BlockAccessIndex < index) pos++;
            return pos < list.Count && list[pos].BlockAccessIndex == index ? list[pos] : null;
        }

        private IEnumerable<SlotChanges> SlotChangesAtIndex(AccountChanges ac, ushort index)
        {
            _slotPositions ??= [];
            foreach (SlotChanges slot in ac.StorageChanges)
            {
                _slotPositions.TryGetValue(slot, out int pos);
                List<StorageChange> changes = slot.Changes;
                while (pos < changes.Count && changes[pos].BlockAccessIndex < index) pos++;
                _slotPositions[slot] = pos;
                if (pos < changes.Count && changes[pos].BlockAccessIndex == index)
                {
                    yield return new SlotChanges(slot.Slot, [changes[pos]]);
                }
            }
        }
    }
}
