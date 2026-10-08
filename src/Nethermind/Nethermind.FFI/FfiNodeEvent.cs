// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.FFI;

/// <summary>Kind of node status event passed to the native callback; mirrors <c>nm_node_event</c> in <c>native/nethermind_ffi.h</c>.</summary>
public enum FfiNodeEvent
{
    NewHead = 0,
    Finalized = 1,
    Synced = 2,
    Syncing = 3,
}
