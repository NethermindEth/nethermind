// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

public sealed partial class JournalCollection<T>
{
    public partial void Clear() => _list.Clear();

    private partial void Truncate(int count) => CollectionsMarshal.SetCount(_list, count);
}
