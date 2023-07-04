// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Nethermind.Core.Collections;

public class EnhancedSortedSet<T> : SortedSet<T>, IReadOnlySortedSet<T>
{
    public EnhancedSortedSet() { }
    public EnhancedSortedSet(IComparer<T>? comparer) : base(comparer) { }
    public EnhancedSortedSet(IEnumerable<T> collection) : base(collection) { }
    public EnhancedSortedSet(IEnumerable<T> collection, IComparer<T>? comparer) : base(collection, comparer) { }
    protected EnhancedSortedSet(SerializationInfo info, StreamingContext context) : base(info, context) { }
}
