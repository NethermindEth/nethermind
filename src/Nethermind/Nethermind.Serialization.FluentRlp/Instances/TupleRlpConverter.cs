// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.FluentRlp.Instances;

public abstract class TupleRlpConverter<T1, T2>
{
    public static (T1, T2) Read(ref RlpReader reader, RefRlpReaderFunc<T1> read1, RefRlpReaderFunc<T2> read2)
    {
        T1 _1 = read1(ref reader);
        T2 _2 = read2(ref reader);

        return (_1, _2);
    }

    public static void Write(ref RlpWriter writer, (T1, T2) value, RefRlpWriterAction<T1> write1, RefRlpWriterAction<T2> write2)
    {
        write1(ref writer, value.Item1);
        write2(ref writer, value.Item2);
    }
}

public static class TupleRlpConverterExt
{
    public static (T1, T2) ReadTuple<T1, T2>(
        this ref RlpReader reader,
        RefRlpReaderFunc<T1> read1,
        RefRlpReaderFunc<T2> read2
    ) => TupleRlpConverter<T1, T2>.Read(ref reader, read1, read2);

    public static void Write<T1, T2>(
        this ref RlpWriter writer,
        (T1, T2) value,
        RefRlpWriterAction<T1> write1,
        RefRlpWriterAction<T2> write2
    ) => TupleRlpConverter<T1, T2>.Write(ref writer, value, write1, write2);
}
