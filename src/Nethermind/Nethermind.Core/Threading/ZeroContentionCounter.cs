// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

namespace Nethermind.Core.Threading;

public class ZeroContentionCounter
{
    private readonly ThreadLocal<BoxedLong> _threadLocal = new(static () => new BoxedLong(), trackAllValues: true);

    private static readonly Func<ThreadLocal<BoxedLong>, long> _totalDelegate = CreateTotalDelegate();

    public long GetTotalValue()
    {
        return _totalDelegate(_threadLocal);
    }

    private static Func<ThreadLocal<BoxedLong>, long> CreateTotalDelegate()
    {
        FieldInfo linkedSlot = typeof(ThreadLocal<BoxedLong>).GetField("_linkedSlot", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo next = linkedSlot.FieldType.GetField("_next", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo value = next.FieldType.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // The code we are trying to generate:
        //
        //    object? linkedSlot = linkedSlot.GetValue(threadLocal);
        //    if (linkedSlot == null)
        //    {
        //        return 0;
        //    }
        //
        //    long total = 0;
        //    for (linkedSlot = next.GetValue(linkedSlot); linkedSlot != null; linkedSlot = next.GetValue(linkedSlot))
        //    {
        //        total +=  (value.GetValue(linkedSlot) as BoxedLong)!.Value;
        //    }
        //    return total;

        // Parameters
        ParameterExpression threadLocalParam = Expression.Parameter(typeof(ThreadLocal<BoxedLong>), "threadLocal");

        // Fields
        MemberExpression linkedSlotField = Expression.Field(threadLocalParam, linkedSlot);

        // Variables
        ParameterExpression linkedSlotVar = Expression.Variable(linkedSlotField.Type, "linkedSlot");
        ParameterExpression totalVar = Expression.Variable(typeof(long), "total");

        // Assignments
        BinaryExpression assignLinkedSlot = Expression.Assign(linkedSlotVar, linkedSlotField);
        BinaryExpression assignTotal = Expression.Assign(totalVar, Expression.Constant(0L));
        BinaryExpression assignNextSlot = Expression.Assign(linkedSlotVar, Expression.Field(linkedSlotVar, next));

        // Labels
        LabelTarget breakLabel = Expression.Label(typeof(long), "breakLabel");

        ConditionalExpression breakCondition =
            Expression.IfThen(
                Expression.Equal(linkedSlotVar, Expression.Constant(null)),
                Expression.Break(breakLabel, totalVar)
            );

        // Loop body
        BlockExpression loopBody = Expression.Block(
            breakCondition,
            Expression.AddAssign(
                totalVar,
                Expression.Property(
                    Expression.Field(linkedSlotVar, value), typeof(BoxedLong),
                    nameof(BoxedLong.Value)
                )
            ),
            assignNextSlot
        );

        // Loop
        LoopExpression loop = Expression.Loop(loopBody);

        // Block
        BlockExpression block = Expression.Block(
            new[] { linkedSlotVar, totalVar },
            assignLinkedSlot,
            assignTotal,
            breakCondition,
            assignNextSlot,
            loop,
            Expression.Label(breakLabel, totalVar)
        );

        // Lambda
        Expression<Func<ThreadLocal<BoxedLong>, long>> lambda =
            Expression.Lambda<Func<ThreadLocal<BoxedLong>, long>>(block, name: "Get_ThreadLocalValue_TotalValue", new ParameterExpression[] { threadLocalParam });

        return lambda.Compile();
    }

    public void Increment(int value = 1) => _threadLocal.Value!.Increment(value);
    public long ThreadLocalValue => _threadLocal.Value!.Value;

    private class BoxedLong
    {
        private long _value;
        public long Value => _value;
        public void Increment(int value) => _value += value;
    }
}
