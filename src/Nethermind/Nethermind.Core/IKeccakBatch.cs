// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public interface IKeccakBatch : IDisposable {
        byte[]? this[ValueKeccak key]
        {
            get => Get(key);
            set => Set(key, value);
        }

        byte[]? Get(ValueKeccak key, ReadFlags flags = ReadFlags.None);
        void Set(ValueKeccak key, byte[]? value, WriteFlags flags = WriteFlags.None);
    }
}
