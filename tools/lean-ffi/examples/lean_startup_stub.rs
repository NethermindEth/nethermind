// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Deliberately incompatible library used only by fresh-process managed startup tests.
#[unsafe(no_mangle)]
pub extern "C" fn nlean_abi_version() -> u32 {
    if std::env::var("NETHERMIND_LEAN_STARTUP_PROBE").as_deref() == Ok("abi") {
        7
    } else {
        8
    }
}

/// # Safety
/// The non-null output must hold 32 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_aggregated_vk(out: *mut u8) -> i32 {
    if out.is_null() {
        return 0;
    }
    let mode = std::env::var("NETHERMIND_LEAN_STARTUP_PROBE");
    let key = if mode.as_deref() == Ok("key") {
        [0; 32]
    } else if mode.as_deref() == Ok("old-key") {
        [
            0x00, 0xf2, 0x4b, 0xbd, 0xf4, 0x4a, 0x31, 0x13, 0x9c, 0x97, 0x52, 0x23, 0xb7, 0x75,
            0x10, 0xcd, 0x93, 0xa9, 0x05, 0x66, 0x4d, 0xa3, 0xc4, 0xa5, 0x2b, 0x5e, 0xba, 0x4e,
            0x9b, 0x07, 0xde, 0x9e,
        ]
    } else {
        rec_aggregation::eip8288_mixed::mixed_guest_key()
    };
    unsafe {
        std::ptr::copy_nonoverlapping(key.as_ptr(), out, key.len());
    }
    1
}

/// # Safety
/// The non-null output must hold six u32 values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_limits(out: *mut u32, len: usize) -> i32 {
    if out.is_null() || len != 6 {
        return 0;
    }
    unsafe {
        std::ptr::write_bytes(out, 0, len);
    }
    1
}
