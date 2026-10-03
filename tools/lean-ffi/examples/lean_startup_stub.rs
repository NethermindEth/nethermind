// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Deliberately incompatible library used only by fresh-process managed startup tests.
#[unsafe(no_mangle)]
pub extern "C" fn nlean_abi_version() -> u32 {
    if std::env::var("NETHERMIND_LEAN_STARTUP_PROBE").as_deref() == Ok("abi") {
        3
    } else {
        4
    }
}

/// # Safety
/// The non-null output must hold 32 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_aggregated_vk(out: *mut u8) -> i32 {
    if out.is_null() {
        return 0;
    }
    let key = if std::env::var("NETHERMIND_LEAN_STARTUP_PROBE").as_deref() == Ok("key") {
        [0; 32]
    } else {
        [
            0x23, 0x30, 0x5f, 0x24, 0x92, 0x84, 0x3c, 0x52, 0xdf, 0xc0, 0xcf, 0x62, 0xce, 0x46,
            0x82, 0x7b, 0x77, 0x60, 0x71, 0xfc, 0xc6, 0x48, 0x65, 0x04, 0x78, 0x1a, 0xb8, 0xc8,
            0xcf, 0x8e, 0xd3, 0x87,
        ]
    };
    unsafe {
        std::ptr::copy_nonoverlapping(key.as_ptr(), out, key.len());
    }
    1
}

/// # Safety
/// The non-null output must hold nine u32 values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nlean_limits(out: *mut u32, len: usize) -> i32 {
    if out.is_null() || len != 9 {
        return 0;
    }
    unsafe {
        std::ptr::write_bytes(out, 0, len);
    }
    1
}
