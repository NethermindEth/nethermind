// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using LeafEnumerator = System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<byte[],byte[]>>;

namespace Nethermind.Core.Verkle;

public class LeafIterator
{
    public LeafIterator(LeafEnumerator enumerator, int priority)
    {
        Enumerator = enumerator;
        Priority = priority;
    }
    public readonly LeafEnumerator Enumerator;
    public readonly int Priority;
}
