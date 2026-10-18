// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Keccak-256, batching equal padded block counts with AVX2 and AVX-512 kernels.

use sha3::{Digest, Keccak256};

#[cfg(target_arch = "x86_64")]
const RATE: usize = 136;
#[cfg(target_arch = "x86_64")]
const AVX2_LANES: usize = 4;
#[cfg(target_arch = "x86_64")]
const AVX512_LANES: usize = 8;
#[cfg(target_arch = "x86_64")]
const MAX_PADDED_CLASS: usize = 4;
#[cfg(target_arch = "x86_64")]
const MAX_PADDED_LENGTH: usize = MAX_PADDED_CLASS * RATE;

#[cfg(target_arch = "x86_64")]
#[path = "keccak/avx2.rs"]
mod avx2;

#[cfg(target_arch = "x86_64")]
const ROUND_CONSTANTS: [u64; 24] = [
    0x0000000000000001,
    0x0000000000008082,
    0x800000000000808a,
    0x8000000080008000,
    0x000000000000808b,
    0x0000000080000001,
    0x8000000080008081,
    0x8000000000008009,
    0x000000000000008a,
    0x0000000000000088,
    0x0000000080008009,
    0x000000008000000a,
    0x000000008000808b,
    0x800000000000008b,
    0x8000000000008089,
    0x8000000000008003,
    0x8000000000008002,
    0x8000000000000080,
    0x000000000000800a,
    0x800000008000000a,
    0x8000000080008081,
    0x8000000000008080,
    0x0000000080000001,
    0x8000000080008008,
];

#[cfg(target_arch = "x86_64")]
/// Rho rotation of lane `x + 5y`.
const RHO: [i64; 25] = [
    0, 1, 62, 28, 27, 36, 44, 6, 55, 20, 3, 10, 43, 25, 39, 41, 45, 15, 21, 8, 18, 2, 61, 56, 14,
];

pub fn keccak(data: &[u8]) -> [u8; 32] {
    Keccak256::digest(data).into()
}

/// Hashes `message(index)` into `hashes[index]` for every index below `hashes.len()`.
pub fn keccak_many<'a>(message: impl Fn(usize) -> &'a [u8], hashes: &mut [[u8; 32]]) {
    #[cfg(target_arch = "x86_64")]
    if hashes.len() >= 2 && std::arch::is_x86_feature_detected!("avx2") {
        keccak_many_batched(
            message,
            hashes,
            std::arch::is_x86_feature_detected!("avx512f"),
        );
        return;
    }

    for (index, hash) in hashes.iter_mut().enumerate() {
        *hash = keccak(message(index));
    }
}

#[cfg(target_arch = "x86_64")]
fn keccak_many_batched<'a>(
    message: impl Fn(usize) -> &'a [u8],
    hashes: &mut [[u8; 32]],
    has_avx512: bool,
) {
    let kernel_width = if has_avx512 { AVX512_LANES } else { AVX2_LANES };
    let mut by_class = [[0usize; AVX512_LANES]; MAX_PADDED_CLASS];
    let mut class_count = [0usize; MAX_PADDED_CLASS];
    for index in 0..hashes.len() {
        let length = message(index).len();
        if length >= MAX_PADDED_LENGTH {
            hashes[index] = keccak(message(index));
            continue;
        }

        let class = length / RATE;
        by_class[class][class_count[class]] = index;
        class_count[class] += 1;
        if class_count[class] == kernel_width {
            hash_group(
                &message,
                &by_class[class][..kernel_width],
                hashes,
                has_avx512,
            );
            class_count[class] = 0;
        }
    }

    for class in 0..MAX_PADDED_CLASS {
        hash_group(
            &message,
            &by_class[class][..class_count[class]],
            hashes,
            has_avx512,
        );
    }
}

#[cfg(target_arch = "x86_64")]
fn hash_group<'a>(
    message: &impl Fn(usize) -> &'a [u8],
    group: &[usize],
    hashes: &mut [[u8; 32]],
    has_avx512: bool,
) {
    if group.len() < 2 {
        for &index in group {
            hashes[index] = keccak(message(index));
        }
        return;
    }

    let messages: [&[u8]; AVX512_LANES] = std::array::from_fn(|lane| {
        if lane < group.len() {
            message(group[lane])
        } else {
            &[]
        }
    });
    let mut group_hashes = [[0u8; 32]; AVX512_LANES];
    // SAFETY: the caller checked AVX2; AVX-512 is used only when separately detected.
    // Groups fit the kernel, and captured slices remain valid and identical across every callback.
    unsafe {
        if has_avx512 && group.len() > AVX2_LANES {
            avx512::keccak_x8(group.len(), |lane| messages[lane], &mut group_hashes);
        } else {
            avx2::keccak_x4(
                group.len(),
                |lane| messages[lane],
                (&mut group_hashes[..AVX2_LANES]).try_into().unwrap(),
            );
        }
    }
    for (lane, &index) in group.iter().enumerate() {
        hashes[index] = group_hashes[lane];
    }
}

#[cfg(target_arch = "x86_64")]
mod avx512 {
    use std::arch::x86_64::*;

    use super::{AVX512_LANES as LANES, RATE, RHO, ROUND_CONSTANTS};

    const XOR3: i32 = 0x96;
    /// `a ^ (!b & c)`
    const CHI: i32 = 0xd2;

    /// Hashes the messages of the first `lanes` lanes, each lane absorbing its own number of blocks.
    #[target_feature(enable = "avx512f")]
    pub unsafe fn keccak_x8<'a>(
        lanes: usize,
        message: impl Fn(usize) -> &'a [u8],
        hashes: &mut [[u8; 32]; LANES],
    ) {
        let lengths: [usize; LANES] =
            std::array::from_fn(|lane| if lane < lanes { message(lane).len() } else { 0 });
        let last_blocks = lengths.map(|length| length / RATE);
        let block_count = last_blocks.iter().max().unwrap() + 1;

        let zero_block = [0u8; RATE];
        let mut final_blocks = [[0u8; RATE]; LANES];
        for (lane, final_block) in final_blocks.iter_mut().enumerate().take(lanes) {
            let tail = &message(lane)[last_blocks[lane] * RATE..];
            final_block[..tail.len()].copy_from_slice(tail);
            final_block[tail.len()] |= 0x01;
            final_block[RATE - 1] |= 0x80;
        }

        let mut a = [_mm512_setzero_si512(); 25];
        let mut b = [_mm512_setzero_si512(); 25];
        let mut c = [_mm512_setzero_si512(); 5];
        for block in 0..block_count {
            // A lane past its last block absorbs zeros, which no longer affects its hash.
            let addresses: [i64; LANES] = std::array::from_fn(|lane| {
                let block_start = match lane < lanes {
                    true if block < last_blocks[lane] => message(lane)[block * RATE..].as_ptr(),
                    true if block == last_blocks[lane] => final_blocks[lane].as_ptr(),
                    _ => zero_block.as_ptr(),
                };
                block_start as i64
            });
            let mut word_addresses = _mm512_loadu_si512(addresses.as_ptr().cast());
            for word in a.iter_mut().take(RATE / 8) {
                *word = _mm512_xor_si512(
                    *word,
                    _mm512_i64gather_epi64::<1>(word_addresses, std::ptr::null()),
                );
                word_addresses = _mm512_add_epi64(word_addresses, _mm512_set1_epi64(8));
            }

            for round_constant in ROUND_CONSTANTS {
                for x in 0..5 {
                    c[x] = _mm512_ternarylogic_epi64::<XOR3>(
                        _mm512_ternarylogic_epi64::<XOR3>(a[x], a[x + 5], a[x + 10]),
                        a[x + 15],
                        a[x + 20],
                    );
                }
                for x in 0..5 {
                    let rotated = _mm512_rol_epi64::<1>(c[(x + 1) % 5]);
                    for y in 0..5 {
                        a[x + 5 * y] = _mm512_ternarylogic_epi64::<XOR3>(
                            a[x + 5 * y],
                            c[(x + 4) % 5],
                            rotated,
                        );
                    }
                }
                for x in 0..5 {
                    for y in 0..5 {
                        b[y + 5 * ((2 * x + 3 * y) % 5)] =
                            _mm512_rolv_epi64(a[x + 5 * y], _mm512_set1_epi64(RHO[x + 5 * y]));
                    }
                }
                for y in 0..5 {
                    for x in 0..5 {
                        a[x + 5 * y] = _mm512_ternarylogic_epi64::<CHI>(
                            b[x + 5 * y],
                            b[(x + 1) % 5 + 5 * y],
                            b[(x + 2) % 5 + 5 * y],
                        );
                    }
                }
                a[0] = _mm512_xor_si512(a[0], _mm512_set1_epi64(round_constant as i64));
            }

            if last_blocks[..lanes].contains(&block) {
                for word_index in 0..4 {
                    let mut words = [0u64; LANES];
                    _mm512_storeu_si512(words.as_mut_ptr().cast(), a[word_index]);
                    for lane in (0..lanes).filter(|&lane| last_blocks[lane] == block) {
                        hashes[lane][word_index * 8..word_index * 8 + 8]
                            .copy_from_slice(&words[lane].to_le_bytes());
                    }
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn messages(lengths: impl IntoIterator<Item = usize>) -> Vec<Vec<u8>> {
        lengths
            .into_iter()
            .map(|length| {
                (0..length)
                    .map(|index| (index * 31 + length) as u8)
                    .collect()
            })
            .collect()
    }

    fn assert_hashes(messages: &[Vec<u8>], hashes: &[[u8; 32]]) {
        for (index, (message, hash)) in messages.iter().zip(hashes).enumerate() {
            assert_eq!(
                *hash,
                keccak(message),
                "message {index}, length {}",
                message.len()
            );
        }
    }

    #[test]
    fn keccak_many_preserves_order_across_padding_classes_and_tails() {
        let lengths = [
            543, 0, 136, 544, 135, 272, 271, 408, 407, 1, 545, 4096, 65536,
        ];
        for count in 0..=33 {
            let messages = messages((0..count).map(|index| lengths[(index * 5) % lengths.len()]));
            let mut hashes = vec![[0xa5; 32]; count];
            keccak_many(|index| &messages[index], &mut hashes);
            assert_hashes(&messages, &hashes);
        }
    }

    #[cfg(target_arch = "x86_64")]
    fn assert_batched(has_avx512: bool) {
        // Exercise every tail length separately in each padded class, then mix the classes.
        for count in 0..=33 {
            for class in 0..MAX_PADDED_CLASS {
                let messages = messages((0..count).map(|index| class * RATE + index % RATE));
                let mut hashes = vec![[0xa5; 32]; count];
                keccak_many_batched(|index| &messages[index], &mut hashes, has_avx512);
                assert_hashes(&messages, &hashes);
            }
        }
        let mut messages = messages((0..=MAX_PADDED_LENGTH + 1).rev().chain([4096, 65536]));
        messages.rotate_left(73);
        let mut hashes = vec![[0xa5; 32]; messages.len()];
        keccak_many_batched(|index| &messages[index], &mut hashes, has_avx512);
        assert_hashes(&messages, &hashes);
    }

    #[cfg(target_arch = "x86_64")]
    #[test]
    fn avx2_buckets_match_scalar() {
        if !std::arch::is_x86_feature_detected!("avx2") {
            eprintln!("AVX2 unavailable: SIMD assertions not executed");
            return;
        }
        assert_batched(false);
    }

    #[cfg(target_arch = "x86_64")]
    #[test]
    fn avx512_buckets_and_narrow_tails_match_scalar() {
        if !std::arch::is_x86_feature_detected!("avx2")
            || !std::arch::is_x86_feature_detected!("avx512f")
        {
            eprintln!("AVX2/AVX-512 unavailable: SIMD assertions not executed");
            return;
        }
        assert_batched(true);
    }

    #[cfg(target_arch = "x86_64")]
    #[test]
    fn simd_kernels_match_scalar_for_mixed_lengths() {
        let messages = messages([0, 135, 136, 271, 272, 543, 544, 65536]);
        if std::arch::is_x86_feature_detected!("avx2") {
            for count in 0..=AVX2_LANES {
                let mut hashes = [[0xa5; 32]; AVX2_LANES];
                // SAFETY: AVX2 is available, and count is at most the kernel's width.
                unsafe { avx2::keccak_x4(count, |index| &messages[index], &mut hashes) };
                assert_hashes(&messages[..count], &hashes[..count]);
                assert!(hashes[count..].iter().all(|hash| *hash == [0xa5; 32]));
            }
        } else {
            eprintln!("AVX2 unavailable: direct kernel assertions not executed");
        }
        if std::arch::is_x86_feature_detected!("avx512f") {
            for count in 0..=AVX512_LANES {
                let mut hashes = [[0xa5; 32]; AVX512_LANES];
                // SAFETY: AVX-512F is available, and count is at most the kernel's width.
                unsafe { avx512::keccak_x8(count, |index| &messages[index], &mut hashes) };
                assert_hashes(&messages[..count], &hashes[..count]);
                assert!(hashes[count..].iter().all(|hash| *hash == [0xa5; 32]));
            }
        } else {
            eprintln!("AVX-512 unavailable: direct kernel assertions not executed");
        }
    }

    #[test]
    fn keccak_many_matches_keccak() {
        // Lengths around every padding boundary, so groups mix full and partial batches of one block count.
        let messages = messages(
            (0..700usize)
                .step_by(3)
                .chain([0, 135, 136, 271, 272, 543, 544]),
        );
        let slices: Vec<&[u8]> = messages.iter().map(Vec::as_slice).collect();
        let mut hashes = vec![[0u8; 32]; slices.len()];
        keccak_many(|index| slices[index], &mut hashes);
        for (message, hash) in slices.iter().zip(&hashes) {
            assert_eq!(*hash, keccak(message), "length {}", message.len());
        }
    }
}
