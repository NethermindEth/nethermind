// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

public sealed partial class LogEntryDecoder
{
    private static readonly RlpLimit RlpLimit = RlpLimit.For<LogEntry>((int)16.MB, nameof(LogEntry));
}
