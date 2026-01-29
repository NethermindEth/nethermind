// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Xdc;

public record struct MigrationResult(int StateNodesCopied, int CodeEntriesCopied)
{
    public override string ToString() => $"State nodes copied: {StateNodesCopied}, Code entries copied: {CodeEntriesCopied}";
}
