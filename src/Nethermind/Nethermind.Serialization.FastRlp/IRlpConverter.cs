// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.FastRlp;

public interface IRlpConverter<T> where T : allows ref struct
{
    public static abstract T Read(ref RlpReader reader);
    public static abstract void Write(ref RlpWriter writer, T value);
}
