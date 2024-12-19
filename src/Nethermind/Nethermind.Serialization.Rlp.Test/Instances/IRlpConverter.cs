// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp.Test.Instances;

// TODO: Explore variance annotations
public interface IRlpConverter<T>
{
    public static abstract T Read(ref RlpReader reader);
    public static abstract void Write(IRlpWriter writer, T value);
}
