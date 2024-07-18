// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.FastBlocks;

internal enum FastBlockStatus : byte
{
    Pending = 0,
    Sent = 1,
    Inserted = 2,
}
