// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

public class LogIndexStateException(string message, ReadOnlySpan<byte> key = default) : Exception(message)
{
    public byte[]? Key { get; } = key.Length == 0 ? null : key.ToArray();

    public override string Message => Key is null
        ? base.Message
        : $"{base.Message} (Key: {Convert.ToHexString(Key)})";
}
