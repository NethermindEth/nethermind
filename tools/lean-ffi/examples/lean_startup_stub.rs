// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Deliberately incompatible library used only by fresh-process managed startup tests.
#[unsafe(no_mangle)]
pub extern "C" fn nlean_abi_version() -> u32 {
    if std::env::var("NETHERMIND_LEAN_STARTUP_PROBE").as_deref() == Ok("abi") {
        8
    } else {
        9
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
            0x6b, 0x99, 0xf3, 0x87, 0x2b, 0x5d, 0xc2, 0x34, 0x7c, 0xa1, 0x3a, 0x34, 0x8f, 0x1f,
            0x29, 0x71, 0x92, 0xfa, 0xb7, 0x5a, 0x92, 0xd3, 0x62, 0x69, 0x75, 0x44, 0x74, 0x5f,
            0x4a, 0x98, 0x11, 0x66,
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
