// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Collections;

public sealed partial class JournalCollection<T>
{
    public partial void Clear() => GuestList<T>.Reset(_list);

    private partial void Truncate(int count) => GuestList<T>.Truncate(_list, count);
}
