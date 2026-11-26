// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public readonly ref partial struct TransactionSubstate
{
    private static bool DecodesRevertReason => false;
}
