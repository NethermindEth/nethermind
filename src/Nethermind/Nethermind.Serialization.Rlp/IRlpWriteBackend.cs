// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Rlp;

public interface IRlpWriteBackend
{
    void WriteByte(byte byteToWrite);

    void Write(scoped ReadOnlySpan<byte> bytesToWrite);

    void WriteZero(int length);
}
