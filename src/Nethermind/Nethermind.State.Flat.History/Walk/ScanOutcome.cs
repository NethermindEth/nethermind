// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History.Walk;

internal enum ScanOutcome : byte
{
    Fits,
    SinglePathOverflow,
    Split,
}
