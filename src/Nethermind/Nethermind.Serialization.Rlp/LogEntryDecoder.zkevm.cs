// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

// Computed on access, so the guest runs no class constructor for this type.
public sealed partial class LogEntryDecoder
{
    private static RlpLimit RlpLimit => new((int)16.MB, nameof(LogEntry), nameof(LogEntry).AsMemory());
}
