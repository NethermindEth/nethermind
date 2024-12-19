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
}
