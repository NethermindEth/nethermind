// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.FFI;

/// <summary>Kind of tx pool event passed to the native callback; mirrors <c>nm_tx_event</c> in <c>native/nethermind_ffi.h</c>.</summary>
public enum FfiTxEvent
{
    Pending = 0,
    Removed = 1,
    Evicted = 2,
}
