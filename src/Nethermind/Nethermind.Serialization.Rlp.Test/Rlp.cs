// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp.Test;

public static class Rlp
{
    public static byte[] Write(RefRlpWriterAction action)
    {
        var lengthWriter = RlpWriter.LengthWriter();
        action(ref lengthWriter);
        var serialized = new byte[lengthWriter.Length];
        var contentWriter = RlpWriter.ContentWriter(serialized);
        action(ref contentWriter);

        return serialized;
    }

    public static T Read<T>(ReadOnlySpan<byte> source, RefRlpReaderFunc<T> func) where T : allows ref struct
    {
        var reader = new RlpReader(source);
        T result = func(ref reader);
        // TODO: We might want to add an option to check for no trailing bytes.
        return result;
    }

    public static void Read(ReadOnlySpan<byte> source, RefRlpReaderAction func)
    {
        Read<object?>(source, (scoped ref RlpReader reader) =>
        {
            func(ref reader);
            return null;
        });
    }
}
