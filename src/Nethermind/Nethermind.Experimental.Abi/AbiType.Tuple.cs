// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Experimental.Abi;

public static partial class AbiType
{
    public static Abi<T> Tuple<T>(Abi<T> abi) => new()
    {
        Name = $"({abi})",
        IsDynamic = abi.IsDynamic,
        Read = (ref BinarySpanReader r) =>
        {
            return r.Scoped(abi, static (Abi<T> abi, ref BinarySpanReader r) =>
            {
                T arg;
                if (abi.IsDynamic)
                {
                    (arg, int read) = r.ReadOffset(abi, static (Abi<T> abi, ref BinarySpanReader r) => abi.Read(ref r));
                    r.Advance(read);
                }
                else
                {
                    arg = abi.Read(ref r);
                }

                return arg;
            });
        },
        Write = (ref BinarySpanWriter w, T v) =>
        {
            w.Scoped((v, abi), static ((T, Abi<T>) ctx, ref BinarySpanWriter w) =>
            {
                var (v, abi) = ctx;

                if (abi.IsDynamic)
                {
                    int offset = w.Advance(32);
                    w.WriteOffset(offset, (v, abi), static ((T, Abi<T>) ctx, ref BinarySpanWriter w) =>
                    {
                        var (v, abi) = ctx;
                        abi.Write(ref w, v);
                    });
                }
                else
                {
                    abi.Write(ref w, v);
                }
            });
        },
        Size = v => abi.Size(v)
    };

    // TODO: Investigate if we can generalize this code to avoid duplication when dealing with tuples of different sizes
    public static Abi<(T1, T2)> Tuple<T1, T2>(Abi<T1> abi1, Abi<T2> abi2) => new()
    {
        Name = $"({abi1.Name},{abi2.Name})",
        IsDynamic = abi1.IsDynamic || abi2.IsDynamic,
        Read = (ref BinarySpanReader r) =>
        {
            return r.Scoped((abi1, abi2), static ((Abi<T1>, Abi<T2>) ctx, ref BinarySpanReader r) =>
            {
                var (abi1, abi2) = ctx;

                T1 arg1;
                if (abi1.IsDynamic)
                {
                    (arg1, _) = r.ReadOffset(abi1, static (Abi<T1> abi, ref BinarySpanReader r) => abi.Read(ref r));
                }
                else
                {
                    arg1 = abi1.Read(ref r);
                }

                T2 arg2;
                if (abi2.IsDynamic)
                {
                    (arg2, int read) = r.ReadOffset(abi2, static (Abi<T2> abi, ref BinarySpanReader r) => abi.Read(ref r));
                    r.Advance(read);
                }
                else
                {
                    arg2 = abi2.Read(ref r);
                }

                return (arg1, arg2);
            });
        },
        Write = (ref BinarySpanWriter w, (T1, T2) v) =>
        {
            w.Scoped((v, abi1, abi2), static (((T1, T2), Abi<T1>, Abi<T2>) ctx, ref BinarySpanWriter w) =>
            {
                var ((v1, v2), abi1, abi2) = ctx;

                Span<int> offsets = stackalloc int[2];
                if (abi1.IsDynamic)
                {
                    offsets[0] = w.Advance(32);
                }
                else
                {
                    abi1.Write(ref w, v1);
                }

                if (abi2.IsDynamic)
                {
                    offsets[1] = w.Advance(32);
                }
                else
                {
                    abi2.Write(ref w, v2);
                }

                if (abi1.IsDynamic)
                {
                    w.WriteOffset(offsets[0], (v1, abi1), static ((T1, Abi<T1>) ctx, ref BinarySpanWriter w) =>
                    {
                        var (v, abi) = ctx;
                        abi.Write(ref w, v);
                    });
                }
                if (abi2.IsDynamic)
                {
                    w.WriteOffset(offsets[1], (v2, abi2), static ((T2, Abi<T2>) ctx, ref BinarySpanWriter w) =>
                    {
                        var (v, abi) = ctx;
                        abi.Write(ref w, v);
                    });
                }
            });
        },
        Size = v => abi1.Size(v.Item1) + abi2.Size(v.Item2)
    };

    public static Abi<(T1, T2, T3)> Tuple<T1, T2, T3>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3) => new()
    {
        Name = $"({abi1.Name},{abi2.Name},{abi3.Name})",
        IsDynamic = abi1.IsDynamic || abi2.IsDynamic || abi3.IsDynamic,
        Read = (ref BinarySpanReader r) =>
        {
            return r.Scoped((abi1, abi2, abi3), static ((Abi<T1>, Abi<T2>, Abi<T3>) ctx, ref BinarySpanReader r) =>
            {
                var (abi1, abi2, abi3) = ctx;

                T1 arg1;
                if (abi1.IsDynamic)
                {
                    (arg1, _) = r.ReadOffset(abi1, static (Abi<T1> abi, ref BinarySpanReader r) => abi.Read(ref r));
                }
                else
                {
                    arg1 = abi1.Read(ref r);
                }

                T2 arg2;
                if (abi2.IsDynamic)
                {
                    (arg2, _) = r.ReadOffset(abi2, static (Abi<T2> abi, ref BinarySpanReader r) => abi.Read(ref r));
                }
                else
                {
                    arg2 = abi2.Read(ref r);
                }

                T3 arg3;
                if (abi3.IsDynamic)
                {
                    (arg3, int read) = r.ReadOffset(abi3, static (Abi<T3> abi, ref BinarySpanReader r) => abi.Read(ref r));
                    r.Advance(read);
                }
                else
                {
                    arg3 = abi3.Read(ref r);
                }

                return (arg1, arg2, arg3);
            });
        },
        Write = (ref BinarySpanWriter w, (T1, T2, T3) v) =>
        {
            w.Scoped((v, abi1, abi2, abi3), static (((T1, T2, T3), Abi<T1>, Abi<T2>, Abi<T3>) ctx, ref BinarySpanWriter w) =>
            {
                var ((v1, v2, v3), abi1, abi2, abi3) = ctx;

                Span<int> offsets = stackalloc int[3];
                if (abi1.IsDynamic)
                {
                    offsets[0] = w.Advance(32);
                }
                else
                {
                    abi1.Write(ref w, v1);
                }

                if (abi2.IsDynamic)
                {
                    offsets[1] = w.Advance(32);
                }
                else
                {
                    abi2.Write(ref w, v2);
                }

                if (abi3.IsDynamic)
                {
                    offsets[2] = w.Advance(32);
                }
                else
                {
                    abi3.Write(ref w, v3);
                }

                if (abi1.IsDynamic)
                {
                    w.WriteOffset(offsets[0], (v1, abi1), static ((T1, Abi<T1>) ctx, ref BinarySpanWriter w) =>
                    {
                        var (v, abi) = ctx;
                        abi.Write(ref w, v);
                    });
                }
                if (abi2.IsDynamic)
                {
                    w.WriteOffset(offsets[1], (v2, abi2), static ((T2, Abi<T2>) ctx, ref BinarySpanWriter w) =>
                    {
                        var (v, abi) = ctx;
                        abi.Write(ref w, v);
                    });
                }
                if (abi3.IsDynamic)
                {
                    w.WriteOffset(offsets[2], (v3, abi3), static ((T3, Abi<T3>) ctx, ref BinarySpanWriter w) =>
                    {
                        var (v, abi) = ctx;
                        abi.Write(ref w, v);
                    });
                }
            });
        },
        Size = v => abi1.Size(v.Item1) + abi2.Size(v.Item2) + abi3.Size(v.Item3)
    };

    public static Abi<(T1, T2, T3, T4)> Tuple<T1, T2, T3, T4>(Abi<T1> abi1, Abi<T2> abi2, Abi<T3> abi3, Abi<T4> abi4) => new()
    {
        Name = $"({abi1.Name},{abi2.Name},{abi3.Name},{abi4.Name})",
        IsDynamic = abi1.IsDynamic || abi2.IsDynamic || abi3.IsDynamic || abi4.IsDynamic,
        Read = (ref BinarySpanReader r) =>
        {
            return r.Scoped((abi1, abi2, abi3, abi4), static ((Abi<T1>, Abi<T2>, Abi<T3>, Abi<T4>) ctx, ref BinarySpanReader r) =>
            {
                var (abi1, abi2, abi3, abi4) = ctx;

                T1 arg1;
                if (abi1.IsDynamic)
                {
                    (arg1, _) = r.ReadOffset(abi1, static (Abi<T1> abi, ref BinarySpanReader r) => abi.Read(ref r));
                }
                else
                {
                    arg1 = abi1.Read(ref r);
                }

                T2 arg2;
                if (abi2.IsDynamic)
                {
                    (arg2, _) = r.ReadOffset(abi2, static (Abi<T2> abi, ref BinarySpanReader r) => abi.Read(ref r));
                }
                else
                {
                    arg2 = abi2.Read(ref r);
                }

                T3 arg3;
                if (abi3.IsDynamic)
                {
                    (arg3, _) = r.ReadOffset(abi3, static (Abi<T3> abi, ref BinarySpanReader r) => abi.Read(ref r));
                }
                else
                {
                    arg3 = abi3.Read(ref r);
                }

                T4 arg4;
                if (abi4.IsDynamic)
                {
                    (arg4, int read) = r.ReadOffset(abi4, static (Abi<T4> abi, ref BinarySpanReader r) => abi.Read(ref r));
                    r.Advance(read);
                }
                else
                {
                    arg4 = abi4.Read(ref r);
                }

                return (arg1, arg2, arg3, arg4);
            });
        },
        Write = (ref BinarySpanWriter w, (T1, T2, T3, T4) v) =>
        {
            w.Scoped((v, abi1, abi2, abi3, abi4), static (((T1, T2, T3, T4), Abi<T1>, Abi<T2>, Abi<T3>, Abi<T4>) ctx, ref BinarySpanWriter w) =>
            {
                var ((v1, v2, v3, v4), abi1, abi2, abi3, abi4) = ctx;

                Span<int> offsets = stackalloc int[4];
                if (abi1.IsDynamic)
                {
                    offsets[0] = w.Advance(32);
                }
                else
                {
                    abi1.Write(ref w, v1);
                }

                if (abi2.IsDynamic)
                {
                    offsets[1] = w.Advance(32);
                }
                else
                {
                    abi2.Write(ref w, v2);
                }

                if (abi3.IsDynamic)
                {
                    offsets[2] = w.Advance(32);
                }
                else
                {
                    abi3.Write(ref w, v3);
                }
                if (abi4.IsDynamic)
                {
                    offsets[3] = w.Advance(32);
                }
                else
                {
                    abi4.Write(ref w, v4);
                }

                if (abi1.IsDynamic)
                {
                    w.WriteOffset(offsets[0], (v1, abi1), static ((T1, Abi<T1>) ctx, ref BinarySpanWriter w) =>
                    {
                        var (v, abi) = ctx;
                        abi.Write(ref w, v);
                    });
                }
                if (abi2.IsDynamic)
                {
                    w.WriteOffset(offsets[1], (v2, abi2), static ((T2, Abi<T2>) ctx, ref BinarySpanWriter w) =>
                    {
                        var (v, abi) = ctx;
                        abi.Write(ref w, v);
                    });
                }
                if (abi3.IsDynamic)
                {
                    w.WriteOffset(offsets[2], (v3, abi3), static ((T3, Abi<T3>) ctx, ref BinarySpanWriter w) =>
                    {
                        var (v, abi) = ctx;
                        abi.Write(ref w, v);
                    });
                }
                if (abi4.IsDynamic)
                {
                    w.WriteOffset(offsets[3], (v4, abi4), static ((T4, Abi<T4>) ctx, ref BinarySpanWriter w) =>
                    {
                        var (v, abi) = ctx;
                        abi.Write(ref w, v);
                    });
                }
            });
        },
        Size = v => abi1.Size(v.Item1) + abi2.Size(v.Item2) + abi3.Size(v.Item3) + abi4.Size(v.Item4)
    };
}
