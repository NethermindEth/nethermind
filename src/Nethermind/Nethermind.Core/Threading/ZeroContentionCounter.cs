// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Threading;

namespace Nethermind.Core.Threading;
public class ZeroContentionCounter
{
    private ThreadLocal<BoxedLong> _threadLocal = new(() => new BoxedLong(), trackAllValues: true);

    private static FieldInfo _linkedSlot = typeof(ThreadLocal<BoxedLong>).GetField("_linkedSlot", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static FieldInfo _next = _linkedSlot.FieldType.GetField("_next", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static FieldInfo _value = _next.FieldType.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public long GetTotalValue()
    {
        object? linkedSlot = _linkedSlot.GetValue(_threadLocal);
        if (linkedSlot == null)
        {
            return 0;
        }

        long total = 0;
        for (linkedSlot = _next.GetValue(linkedSlot); linkedSlot != null; linkedSlot = _next.GetValue(linkedSlot))
        {
            total += (_value.GetValue(linkedSlot) as BoxedLong)!.Value;
        }
        return total;
    }

    public void Increment(int value = 1) => _threadLocal.Value!.Increment(value);
    public long ThreadLocalValue => _threadLocal.Value!.Value;

    private class BoxedLong
    {
        private long _value;
        public ref long Value => ref _value;
        public void Increment(int value) => _value += value;
    }
}
