// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Experimental.Abi;

public static partial class AbiType
{
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
            TailSize = abi.IsDynamic ? v => offsetsSize + abi.Size(v) : null
        };
    }

    // TODO: Investigate if we can generalize this code to avoid duplication when dealing with tuples of different sizes
    public static Abi<(T1, T2)> Tuple<T1, T2>(Abi<T1> abi1, Abi<T2> abi2)
    {
        int headSize = abi1.HeadSize + abi2.HeadSize;
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
            TailSize = isDynamic ? v => offsetsSize + abi1.Size(v.Item1) + abi2.Size(v.Item2) : null
        };
    }

    public static Abi<(T1, T2, T3)> Tuple<T1, T2, T3>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3)
    {
        int headSize = abi1.HeadSize + abi2.HeadSize + abi3.HeadSize;
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
            TailSize = isDynamic ? v => offsetsSize + abi1.Size(v.Item1) + abi2.Size(v.Item2) + abi3.Size(v.Item3) : null
        };
    }

    public static Abi<(T1, T2, T3, T4)> Tuple<T1, T2, T3, T4>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4)
    {
        int headSize = abi1.HeadSize + abi2.HeadSize + abi3.HeadSize + abi4.HeadSize;
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
            TailSize = isDynamic ? v => offsetsSize + abi1.Size(v.Item1) + abi2.Size(v.Item2) + abi3.Size(v.Item3) + abi4.Size(v.Item4) : null
        };
    }
}
