// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.FFI;

/// <summary>Result of a native call; mirrors <c>nm_status</c> in <c>native/nethermind_ffi.h</c>.</summary>
public enum FfiStatus
{
    Ok = 0,
    NotReady = 1,
    Stopped = 2,
    DecodeError = 3,
    ParentNotFound = 4,
    StateUnavailable = 5,
    InvalidBlock = 6,
    InternalError = 7,
}
