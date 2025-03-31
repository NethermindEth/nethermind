// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Serialization.Json;

public static class ForcedNumberConversion
{
    public static readonly AsyncLocal<NumberConversion?> ForcedConversion = new();

    public static NumberConversion GetFinalConversion() => ForcedConversion.Value ?? NumberConversion.Hex;
}
