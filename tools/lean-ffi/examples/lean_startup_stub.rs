// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Deliberately incompatible library used only by fresh-process managed startup tests.
#[unsafe(no_mangle)]
pub extern "C" fn nlean_abi_version() -> u32 {
    if std::env::var("NETHERMIND_LEAN_STARTUP_PROBE").as_deref() == Ok("abi") {
        5
    } else {
        6
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
            0x93, 0x70, 0xd7, 0x60, 0xab, 0xb5, 0x5f, 0xdf, 0x02, 0xac, 0xc7, 0xe8, 0xd4, 0x06,
            0x88, 0xc4, 0x25, 0x81, 0x5c, 0x3d, 0x25, 0xa2, 0xae, 0xa3, 0xc0, 0x30, 0xb2, 0xae,
            0x1a, 0xb5, 0x1a, 0xce,
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
