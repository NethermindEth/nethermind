// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp.Test;

public static class Rlp
{
    public static byte[] Write(Action<IRlpWriter> action)
    {
        var lengthWriter = new RlpLengthWriter();
        action(lengthWriter);
        var serialized = new byte[lengthWriter.Length];
        var contentWriter = new RlpContentWriter(serialized);
        action(contentWriter);

        return serialized;
    }

    public static T Read<T>(ReadOnlySpan<byte> source, RefRlpReaderFunc<T> func)
    {
        var reader = new RlpReader(source);
        // TODO: We might want to add an option to check for no trailing bytes.
        return func(ref reader);
    }

    public static void Read(ReadOnlySpan<byte> source, RefRlpReaderAction func)
    {
        Read<object?>(source, (ref RlpReader reader) =>
        {
            func(ref reader);
            return null;
        });
    }
}
