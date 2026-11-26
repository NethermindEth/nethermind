// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Logging;

public static class Static
{
    public static ILogManager LogManager { get => field ??= LimboLogs.Instance; set; }
}
