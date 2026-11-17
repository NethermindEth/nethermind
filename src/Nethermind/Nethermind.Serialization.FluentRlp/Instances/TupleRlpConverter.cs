// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.FluentRlp.Instances;

/// <summary>Converts a pair as two consecutive items, without a sequence around them.</summary>
public abstract class TupleRlpConverter<T1, T2>
{
    /// <summary>Reads two consecutive items.</summary>
    public static (T1, T2) Read(ref RlpReader reader, RefRlpReaderFunc<T1> read1, RefRlpReaderFunc<T2> read2)
    {
        T1 _1 = read1(ref reader);
        T2 _2 = read2(ref reader);

        return (_1, _2);
    }

    /// <summary>Writes two consecutive items.</summary>
    public static void Write(ref RlpWriter writer, (T1, T2) value, RefRlpWriterAction<T1> write1, RefRlpWriterAction<T2> write2)
    {
        write1(ref writer, value.Item1);
        write2(ref writer, value.Item2);
    }
}

/// <summary>Extension methods for <see cref="TupleRlpConverter{T1, T2}"/>.</summary>
public static class TupleRlpConverterExt
{
    /// <inheritdoc cref="TupleRlpConverter{T1, T2}.Read"/>
    public static (T1, T2) ReadTuple<T1, T2>(
        this ref RlpReader reader,
        RefRlpReaderFunc<T1> read1,
        RefRlpReaderFunc<T2> read2
    ) => TupleRlpConverter<T1, T2>.Read(ref reader, read1, read2);

    /// <inheritdoc cref="TupleRlpConverter{T1, T2}.Write"/>
    public static void Write<T1, T2>(
        this ref RlpWriter writer,
        (T1, T2) value,
        RefRlpWriterAction<T1> write1,
        RefRlpWriterAction<T2> write2
    ) => TupleRlpConverter<T1, T2>.Write(ref writer, value, write1, write2);
}
