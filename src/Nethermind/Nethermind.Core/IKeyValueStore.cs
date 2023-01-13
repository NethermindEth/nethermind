// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public interface IKeyValueStore : IReadOnlyKeyValueStore
    {
        new byte[]? this[ReadOnlySpan<byte> key] { get; set; }
    }

    public interface IReadOnlyKeyValueStore
    {
        byte[]? this[ReadOnlySpan<byte> key] { get; }
    }
}
