// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Collections;

public sealed partial class JournalSet<T> where T : notnull, IEquatable<T>
{
    // The set's own Clear already zeroes only the buckets in use while it is sparse.
    public partial void Clear()
    {
        _set.Clear();
        _items.Clear();
    }
}
