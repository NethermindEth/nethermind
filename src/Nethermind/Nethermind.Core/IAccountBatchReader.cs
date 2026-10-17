// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

public interface IAccountBatchReader
{
    void GetAccounts(ReadOnlySpan<Address> addresses, Span<Account?> accounts);
}
