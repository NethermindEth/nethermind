// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.RpcTests.Monitor;

internal record BlockInfo(long Number, string Hash) : IFormattable
{
    public override string ToString() => $"{Number} ({Hash})";

    public string ToString(string? format, IFormatProvider? formatProvider) => format?.Equals("#") == true
        ? Number.ToString()
        : ToString();
}
