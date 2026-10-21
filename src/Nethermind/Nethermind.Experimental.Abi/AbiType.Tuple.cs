// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Experimental.Abi;

public static partial class AbiType
{
    public static Abi<ValueTuple> Tuple() => new()
    {
        Name = "()",
        Read = (ref BinarySpanReader _) => default,
        Write = (ref BinarySpanWriter _, ValueTuple _) => { },
        HeadSize = 0,
    };

    public static Abi<T> Tuple<T>(Abi<T> abi)
    {
        int headSize = abi.HeadSize;
        int offsetsSize = abi.IsDynamic ? 32 : 0;

        return new()
        {
            Name = $"({abi})",
            Read = (ref BinarySpanReader r) =>
            {
                return r.Scoped((abi, headSize), static ((Abi<T>, int) ctx, ref BinarySpanReader r) =>
                {
                    (Abi<T> abi, int headSize) = ctx;

                    int next = headSize;
                    T arg = abi.IsDynamic ? r.ReadOffset(abi.Read, ref next) : abi.Read(ref r);
                    r.Advance(next - headSize);

                    return arg;
                });
            },
            Write = (ref BinarySpanWriter w, T v) =>
            {
                w.Scoped((v, abi, headSize), static ((T, Abi<T>, int) ctx, ref BinarySpanWriter w) =>
                {
                    (T v, Abi<T> abi, int headSize) = ctx;

                    int next = headSize;
                    if (abi.IsDynamic) w.WriteOffset(abi.Write, v, ref next);
                    else abi.Write(ref w, v);
                    w.Advance(next - headSize);
                });
            },
            HeadSize = abi.IsDynamic ? 32 : headSize,
            TailSize = abi.IsDynamic ? v => checked(offsetsSize + abi.Size(v)) : null
        };
    }

    // TODO: Investigate if we can generalize this code to avoid duplication when dealing with tuples of different sizes
    public static Abi<(T1, T2)> Tuple<T1, T2>(Abi<T1> abi1, Abi<T2> abi2)
    {
        int headSize = checked(abi1.HeadSize + abi2.HeadSize);
        int offsetsSize = (abi1.IsDynamic ? 32 : 0) + (abi2.IsDynamic ? 32 : 0);
        bool isDynamic = abi1.IsDynamic || abi2.IsDynamic;

        return new()
        {
            Name = $"({abi1.Name},{abi2.Name})",
            Read = (ref BinarySpanReader r) =>
            {
                return r.Scoped((abi1, abi2, headSize), static ((Abi<T1>, Abi<T2>, int) ctx, ref BinarySpanReader r) =>
                {
                    (Abi<T1> abi1, Abi<T2> abi2, int headSize) = ctx;

                    int next = headSize;
                    T1 arg1 = abi1.IsDynamic ? r.ReadOffset(abi1.Read, ref next) : abi1.Read(ref r);
                    T2 arg2 = abi2.IsDynamic ? r.ReadOffset(abi2.Read, ref next) : abi2.Read(ref r);
                    r.Advance(next - headSize);

                    return (arg1, arg2);
                });
            },
            Write = (ref BinarySpanWriter w, (T1, T2) v) =>
            {
                w.Scoped((v, abi1, abi2, headSize), static (((T1, T2), Abi<T1>, Abi<T2>, int) ctx, ref BinarySpanWriter w) =>
                {
                    ((T1 v1, T2 v2), Abi<T1> abi1, Abi<T2> abi2, int headSize) = ctx;

                    int next = headSize;
                    if (abi1.IsDynamic) w.WriteOffset(abi1.Write, v1, ref next);
                    else abi1.Write(ref w, v1);
                    if (abi2.IsDynamic) w.WriteOffset(abi2.Write, v2, ref next);
                    else abi2.Write(ref w, v2);
                    w.Advance(next - headSize);
                });
            },
            HeadSize = isDynamic ? 32 : headSize,
            TailSize = isDynamic ? v => checked(offsetsSize + abi1.Size(v.Item1) + abi2.Size(v.Item2)) : null
        };
    }

    public static Abi<(T1, T2, T3)> Tuple<T1, T2, T3>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3)
    {
        int headSize = checked(abi1.HeadSize + abi2.HeadSize + abi3.HeadSize);
        int offsetsSize = (abi1.IsDynamic ? 32 : 0) + (abi2.IsDynamic ? 32 : 0) + (abi3.IsDynamic ? 32 : 0);
        bool isDynamic = abi1.IsDynamic || abi2.IsDynamic || abi3.IsDynamic;

        return new()
        {
            Name = $"({abi1.Name},{abi2.Name},{abi3.Name})",
            Read = (ref BinarySpanReader r) =>
            {
                return r.Scoped((abi1, abi2, abi3, headSize), static ((Abi<T1>, Abi<T2>, Abi<T3>, int) ctx, ref BinarySpanReader r) =>
                {
                    (Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, int headSize) = ctx;

                    int next = headSize;
                    T1 arg1 = abi1.IsDynamic ? r.ReadOffset(abi1.Read, ref next) : abi1.Read(ref r);
                    T2 arg2 = abi2.IsDynamic ? r.ReadOffset(abi2.Read, ref next) : abi2.Read(ref r);
                    T3 arg3 = abi3.IsDynamic ? r.ReadOffset(abi3.Read, ref next) : abi3.Read(ref r);
                    r.Advance(next - headSize);

                    return (arg1, arg2, arg3);
                });
            },
            Write = (ref BinarySpanWriter w, (T1, T2, T3) v) =>
            {
                w.Scoped((v, abi1, abi2, abi3, headSize), static (((T1, T2, T3), Abi<T1>, Abi<T2>, Abi<T3>, int) ctx, ref BinarySpanWriter w) =>
                {
                    ((T1 v1, T2 v2, T3 v3), Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, int headSize) = ctx;

                    int next = headSize;
                    if (abi1.IsDynamic) w.WriteOffset(abi1.Write, v1, ref next);
                    else abi1.Write(ref w, v1);
                    if (abi2.IsDynamic) w.WriteOffset(abi2.Write, v2, ref next);
                    else abi2.Write(ref w, v2);
                    if (abi3.IsDynamic) w.WriteOffset(abi3.Write, v3, ref next);
                    else abi3.Write(ref w, v3);
                    w.Advance(next - headSize);
                });
            },
            HeadSize = isDynamic ? 32 : headSize,
            TailSize = isDynamic ? v => checked(offsetsSize + abi1.Size(v.Item1) + abi2.Size(v.Item2) + abi3.Size(v.Item3)) : null
        };
    }

    public static Abi<(T1, T2, T3, T4)> Tuple<T1, T2, T3, T4>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4)
    {
        int headSize = checked(abi1.HeadSize + abi2.HeadSize + abi3.HeadSize + abi4.HeadSize);
        int offsetsSize = (abi1.IsDynamic ? 32 : 0) + (abi2.IsDynamic ? 32 : 0) + (abi3.IsDynamic ? 32 : 0) + (abi4.IsDynamic ? 32 : 0);
        bool isDynamic = abi1.IsDynamic || abi2.IsDynamic || abi3.IsDynamic || abi4.IsDynamic;

        return new()
        {
            Name = $"({abi1.Name},{abi2.Name},{abi3.Name},{abi4.Name})",
            Read = (ref BinarySpanReader r) =>
            {
                return r.Scoped((abi1, abi2, abi3, abi4, headSize), static ((Abi<T1>, Abi<T2>, Abi<T3>, Abi<T4>, int) ctx, ref BinarySpanReader r) =>
                {
                    (Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, int headSize) = ctx;

                    int next = headSize;
                    T1 arg1 = abi1.IsDynamic ? r.ReadOffset(abi1.Read, ref next) : abi1.Read(ref r);
                    T2 arg2 = abi2.IsDynamic ? r.ReadOffset(abi2.Read, ref next) : abi2.Read(ref r);
                    T3 arg3 = abi3.IsDynamic ? r.ReadOffset(abi3.Read, ref next) : abi3.Read(ref r);
                    T4 arg4 = abi4.IsDynamic ? r.ReadOffset(abi4.Read, ref next) : abi4.Read(ref r);
                    r.Advance(next - headSize);

                    return (arg1, arg2, arg3, arg4);
                });
            },
            Write = (ref BinarySpanWriter w, (T1, T2, T3, T4) v) =>
            {
                w.Scoped((v, abi1, abi2, abi3, abi4, headSize), static (((T1, T2, T3, T4), Abi<T1>, Abi<T2>, Abi<T3>, Abi<T4>, int) ctx, ref BinarySpanWriter w) =>
                {
                    ((T1 v1, T2 v2, T3 v3, T4 v4), Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, int headSize) = ctx;

                    int next = headSize;
                    if (abi1.IsDynamic) w.WriteOffset(abi1.Write, v1, ref next);
                    else abi1.Write(ref w, v1);
                    if (abi2.IsDynamic) w.WriteOffset(abi2.Write, v2, ref next);
                    else abi2.Write(ref w, v2);
                    if (abi3.IsDynamic) w.WriteOffset(abi3.Write, v3, ref next);
                    else abi3.Write(ref w, v3);
                    if (abi4.IsDynamic) w.WriteOffset(abi4.Write, v4, ref next);
                    else abi4.Write(ref w, v4);
                    w.Advance(next - headSize);
                });
            },
            HeadSize = isDynamic ? 32 : headSize,
            TailSize = isDynamic ? v => checked(offsetsSize + abi1.Size(v.Item1) + abi2.Size(v.Item2) + abi3.Size(v.Item3) + abi4.Size(v.Item4)) : null
        };
    }

    public static Abi<(T1, T2, T3, T4, T5)> Tuple<T1, T2, T3, T4, T5>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5)
    {
        int headSize = checked(abi1.HeadSize + abi2.HeadSize + abi3.HeadSize + abi4.HeadSize + abi5.HeadSize);
        int offsetsSize = (abi1.IsDynamic ? 32 : 0) + (abi2.IsDynamic ? 32 : 0) + (abi3.IsDynamic ? 32 : 0) + (abi4.IsDynamic ? 32 : 0) + (abi5.IsDynamic ? 32 : 0);
        bool isDynamic = abi1.IsDynamic || abi2.IsDynamic || abi3.IsDynamic || abi4.IsDynamic || abi5.IsDynamic;

        return new()
        {
            Name = $"({abi1.Name},{abi2.Name},{abi3.Name},{abi4.Name},{abi5.Name})",
            Read = (ref BinarySpanReader r) =>
            {
                return r.Scoped((abi1, abi2, abi3, abi4, abi5, headSize), static ((Abi<T1>, Abi<T2>, Abi<T3>, Abi<T4>, Abi<T5>, int) ctx, ref BinarySpanReader r) =>
                {
                    (Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5, int headSize) = ctx;

                    int next = headSize;
                    T1 arg1 = abi1.IsDynamic ? r.ReadOffset(abi1.Read, ref next) : abi1.Read(ref r);
                    T2 arg2 = abi2.IsDynamic ? r.ReadOffset(abi2.Read, ref next) : abi2.Read(ref r);
                    T3 arg3 = abi3.IsDynamic ? r.ReadOffset(abi3.Read, ref next) : abi3.Read(ref r);
                    T4 arg4 = abi4.IsDynamic ? r.ReadOffset(abi4.Read, ref next) : abi4.Read(ref r);
                    T5 arg5 = abi5.IsDynamic ? r.ReadOffset(abi5.Read, ref next) : abi5.Read(ref r);
                    r.Advance(next - headSize);

                    return (arg1, arg2, arg3, arg4, arg5);
                });
            },
            Write = (ref BinarySpanWriter w, (T1, T2, T3, T4, T5) v) =>
            {
                w.Scoped((v, abi1, abi2, abi3, abi4, abi5, headSize), static (((T1, T2, T3, T4, T5), Abi<T1>, Abi<T2>, Abi<T3>, Abi<T4>, Abi<T5>, int) ctx, ref BinarySpanWriter w) =>
                {
                    ((T1 v1, T2 v2, T3 v3, T4 v4, T5 v5), Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5, int headSize) = ctx;

                    int next = headSize;
                    if (abi1.IsDynamic) w.WriteOffset(abi1.Write, v1, ref next);
                    else abi1.Write(ref w, v1);
                    if (abi2.IsDynamic) w.WriteOffset(abi2.Write, v2, ref next);
                    else abi2.Write(ref w, v2);
                    if (abi3.IsDynamic) w.WriteOffset(abi3.Write, v3, ref next);
                    else abi3.Write(ref w, v3);
                    if (abi4.IsDynamic) w.WriteOffset(abi4.Write, v4, ref next);
                    else abi4.Write(ref w, v4);
                    if (abi5.IsDynamic) w.WriteOffset(abi5.Write, v5, ref next);
                    else abi5.Write(ref w, v5);
                    w.Advance(next - headSize);
                });
            },
            HeadSize = isDynamic ? 32 : headSize,
            TailSize = isDynamic ? v => checked(offsetsSize + abi1.Size(v.Item1) + abi2.Size(v.Item2) + abi3.Size(v.Item3) + abi4.Size(v.Item4) + abi5.Size(v.Item5)) : null
        };
    }

    public static Abi<(T1, T2, T3, T4, T5, T6)> Tuple<T1, T2, T3, T4, T5, T6>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5, Abi<T6> abi6)
    {
        int headSize = checked(abi1.HeadSize + abi2.HeadSize + abi3.HeadSize + abi4.HeadSize + abi5.HeadSize + abi6.HeadSize);
        int offsetsSize = (abi1.IsDynamic ? 32 : 0) + (abi2.IsDynamic ? 32 : 0) + (abi3.IsDynamic ? 32 : 0) + (abi4.IsDynamic ? 32 : 0) + (abi5.IsDynamic ? 32 : 0) + (abi6.IsDynamic ? 32 : 0);
        bool isDynamic = abi1.IsDynamic || abi2.IsDynamic || abi3.IsDynamic || abi4.IsDynamic || abi5.IsDynamic || abi6.IsDynamic;

        return new()
        {
            Name = $"({abi1.Name},{abi2.Name},{abi3.Name},{abi4.Name},{abi5.Name},{abi6.Name})",
            Read = (ref BinarySpanReader r) =>
            {
                return r.Scoped((abi1, abi2, abi3, abi4, abi5, abi6, headSize), static ((Abi<T1>, Abi<T2>, Abi<T3>, Abi<T4>, Abi<T5>, Abi<T6>, int) ctx, ref BinarySpanReader r) =>
                {
                    (Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5, Abi<T6> abi6, int headSize) = ctx;

                    int next = headSize;
                    T1 arg1 = abi1.IsDynamic ? r.ReadOffset(abi1.Read, ref next) : abi1.Read(ref r);
                    T2 arg2 = abi2.IsDynamic ? r.ReadOffset(abi2.Read, ref next) : abi2.Read(ref r);
                    T3 arg3 = abi3.IsDynamic ? r.ReadOffset(abi3.Read, ref next) : abi3.Read(ref r);
                    T4 arg4 = abi4.IsDynamic ? r.ReadOffset(abi4.Read, ref next) : abi4.Read(ref r);
                    T5 arg5 = abi5.IsDynamic ? r.ReadOffset(abi5.Read, ref next) : abi5.Read(ref r);
                    T6 arg6 = abi6.IsDynamic ? r.ReadOffset(abi6.Read, ref next) : abi6.Read(ref r);
                    r.Advance(next - headSize);

                    return (arg1, arg2, arg3, arg4, arg5, arg6);
                });
            },
            Write = (ref BinarySpanWriter w, (T1, T2, T3, T4, T5, T6) v) =>
            {
                w.Scoped((v, abi1, abi2, abi3, abi4, abi5, abi6, headSize), static (((T1, T2, T3, T4, T5, T6), Abi<T1>, Abi<T2>, Abi<T3>, Abi<T4>, Abi<T5>, Abi<T6>, int) ctx, ref BinarySpanWriter w) =>
                {
                    ((T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6), Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5, Abi<T6> abi6, int headSize) = ctx;

                    int next = headSize;
                    if (abi1.IsDynamic) w.WriteOffset(abi1.Write, v1, ref next);
                    else abi1.Write(ref w, v1);
                    if (abi2.IsDynamic) w.WriteOffset(abi2.Write, v2, ref next);
                    else abi2.Write(ref w, v2);
                    if (abi3.IsDynamic) w.WriteOffset(abi3.Write, v3, ref next);
                    else abi3.Write(ref w, v3);
                    if (abi4.IsDynamic) w.WriteOffset(abi4.Write, v4, ref next);
                    else abi4.Write(ref w, v4);
                    if (abi5.IsDynamic) w.WriteOffset(abi5.Write, v5, ref next);
                    else abi5.Write(ref w, v5);
                    if (abi6.IsDynamic) w.WriteOffset(abi6.Write, v6, ref next);
                    else abi6.Write(ref w, v6);
                    w.Advance(next - headSize);
                });
            },
            HeadSize = isDynamic ? 32 : headSize,
            TailSize = isDynamic ? v => checked(offsetsSize + abi1.Size(v.Item1) + abi2.Size(v.Item2) + abi3.Size(v.Item3) + abi4.Size(v.Item4) + abi5.Size(v.Item5) + abi6.Size(v.Item6)) : null
        };
    }

    public static Abi<(T1, T2, T3, T4, T5, T6, T7)> Tuple<T1, T2, T3, T4, T5, T6, T7>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5, Abi<T6> abi6, Abi<T7> abi7)
    {
        int headSize = checked(abi1.HeadSize + abi2.HeadSize + abi3.HeadSize + abi4.HeadSize + abi5.HeadSize + abi6.HeadSize + abi7.HeadSize);
        int offsetsSize = (abi1.IsDynamic ? 32 : 0) + (abi2.IsDynamic ? 32 : 0) + (abi3.IsDynamic ? 32 : 0) + (abi4.IsDynamic ? 32 : 0) + (abi5.IsDynamic ? 32 : 0) + (abi6.IsDynamic ? 32 : 0) + (abi7.IsDynamic ? 32 : 0);
        bool isDynamic = abi1.IsDynamic || abi2.IsDynamic || abi3.IsDynamic || abi4.IsDynamic || abi5.IsDynamic || abi6.IsDynamic || abi7.IsDynamic;

        return new()
        {
            Name = $"({abi1.Name},{abi2.Name},{abi3.Name},{abi4.Name},{abi5.Name},{abi6.Name},{abi7.Name})",
            Read = (ref BinarySpanReader r) =>
            {
                return r.Scoped((abi1, abi2, abi3, abi4, abi5, abi6, abi7, headSize), static ((Abi<T1>, Abi<T2>, Abi<T3>, Abi<T4>, Abi<T5>, Abi<T6>, Abi<T7>, int) ctx, ref BinarySpanReader r) =>
                {
                    (Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5, Abi<T6> abi6, Abi<T7> abi7, int headSize) = ctx;

                    int next = headSize;
                    T1 arg1 = abi1.IsDynamic ? r.ReadOffset(abi1.Read, ref next) : abi1.Read(ref r);
                    T2 arg2 = abi2.IsDynamic ? r.ReadOffset(abi2.Read, ref next) : abi2.Read(ref r);
                    T3 arg3 = abi3.IsDynamic ? r.ReadOffset(abi3.Read, ref next) : abi3.Read(ref r);
                    T4 arg4 = abi4.IsDynamic ? r.ReadOffset(abi4.Read, ref next) : abi4.Read(ref r);
                    T5 arg5 = abi5.IsDynamic ? r.ReadOffset(abi5.Read, ref next) : abi5.Read(ref r);
                    T6 arg6 = abi6.IsDynamic ? r.ReadOffset(abi6.Read, ref next) : abi6.Read(ref r);
                    T7 arg7 = abi7.IsDynamic ? r.ReadOffset(abi7.Read, ref next) : abi7.Read(ref r);
                    r.Advance(next - headSize);

                    return (arg1, arg2, arg3, arg4, arg5, arg6, arg7);
                });
            },
            Write = (ref BinarySpanWriter w, (T1, T2, T3, T4, T5, T6, T7) v) =>
            {
                w.Scoped((v, abi1, abi2, abi3, abi4, abi5, abi6, abi7, headSize), static (((T1, T2, T3, T4, T5, T6, T7), Abi<T1>, Abi<T2>, Abi<T3>, Abi<T4>, Abi<T5>, Abi<T6>, Abi<T7>, int) ctx, ref BinarySpanWriter w) =>
                {
                    ((T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6, T7 v7), Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4, Abi<T5> abi5, Abi<T6> abi6, Abi<T7> abi7, int headSize) = ctx;

                    int next = headSize;
                    if (abi1.IsDynamic) w.WriteOffset(abi1.Write, v1, ref next);
                    else abi1.Write(ref w, v1);
                    if (abi2.IsDynamic) w.WriteOffset(abi2.Write, v2, ref next);
                    else abi2.Write(ref w, v2);
                    if (abi3.IsDynamic) w.WriteOffset(abi3.Write, v3, ref next);
                    else abi3.Write(ref w, v3);
                    if (abi4.IsDynamic) w.WriteOffset(abi4.Write, v4, ref next);
                    else abi4.Write(ref w, v4);
                    if (abi5.IsDynamic) w.WriteOffset(abi5.Write, v5, ref next);
                    else abi5.Write(ref w, v5);
                    if (abi6.IsDynamic) w.WriteOffset(abi6.Write, v6, ref next);
                    else abi6.Write(ref w, v6);
                    if (abi7.IsDynamic) w.WriteOffset(abi7.Write, v7, ref next);
                    else abi7.Write(ref w, v7);
                    w.Advance(next - headSize);
                });
            },
            HeadSize = isDynamic ? 32 : headSize,
            TailSize = isDynamic ? v => checked(offsetsSize + abi1.Size(v.Item1) + abi2.Size(v.Item2) + abi3.Size(v.Item3) + abi4.Size(v.Item4) + abi5.Size(v.Item5) + abi6.Size(v.Item6) + abi7.Size(v.Item7)) : null
        };
    }
}
