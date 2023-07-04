// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Receipts;

public enum ReceiptsRecoveryResult
{
    Success,
    Fail,
    Skipped,
    NeedReinsert,
}
