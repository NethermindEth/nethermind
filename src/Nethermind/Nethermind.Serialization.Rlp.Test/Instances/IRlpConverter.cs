// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp.Test.Instances;

public interface IRlpConverter<in T>
{
    public static abstract void Write(IRlpWriter writer, T value);
}
