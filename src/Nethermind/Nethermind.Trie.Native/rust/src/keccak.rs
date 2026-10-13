// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Keccak-256, with an AVX-512 kernel hashing eight messages at once.

use sha3::{Digest, Keccak256};

const RATE: usize = 136;
const LANES: usize = 8;

pub fn keccak(data: &[u8]) -> [u8; 32] {
    Keccak256::digest(data).into()
}

/// Hashes `message(index)` into `hashes[index]` for every index below `hashes.len()`.
pub fn keccak_many<'a>(message: impl Fn(usize) -> &'a [u8], hashes: &mut [[u8; 32]]) {
    #[cfg(target_arch = "x86_64")]
    if hashes.len() >= 2 && std::arch::is_x86_feature_detected!("avx512f") {
        // Sorted by length, each group of eight runs only as many blocks as its longest message.
        let mut order: Vec<u32> = (0..hashes.len() as u32).collect();
        order.sort_unstable_by_key(|&index| message(index as usize).len());
        for group in order.chunks(LANES) {
            if group.len() == 1 {
                hashes[group[0] as usize] = keccak(message(group[0] as usize));
                continue;
            }
            let mut group_hashes = [[0u8; 32]; LANES];
            // SAFETY: AVX-512F support was checked above.
            unsafe {
                avx512::keccak_x8(
                    group.len(),
                    |lane| message(group[lane] as usize),
                    &mut group_hashes,
                )
            };
            for (lane, &index) in group.iter().enumerate() {
                hashes[index as usize] = group_hashes[lane];
            }
        }
        return;
    }

    for (index, hash) in hashes.iter_mut().enumerate() {
        *hash = keccak(message(index));
    }
}

#[cfg(target_arch = "x86_64")]
mod avx512 {
    use std::arch::x86_64::*;

    use super::{LANES, RATE};

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

    /// Rho rotation of lane `x + 5y`.
    const RHO: [i64; 25] = [
        0, 1, 62, 28, 27, 36, 44, 6, 55, 20, 3, 10, 43, 25, 39, 41, 45, 15, 21, 8, 18, 2, 61, 56,
        14,
    ];

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

    #[test]
    fn keccak_many_matches_keccak() {
        // Lengths around every padding boundary, so groups mix full and partial batches of one block count.
        let messages: Vec<Vec<u8>> = (0..700usize)
            .step_by(3)
            .chain([0, 135, 136, 271, 272, 543, 544])
            .map(|length| {
                (0..length)
                    .map(|index| (index * 31 + length) as u8)
                    .collect()
            })
            .collect();
        let slices: Vec<&[u8]> = messages.iter().map(Vec::as_slice).collect();
        let mut hashes = vec![[0u8; 32]; slices.len()];
        keccak_many(|index| slices[index], &mut hashes);
        for (message, hash) in slices.iter().zip(&hashes) {
            assert_eq!(*hash, keccak(message), "length {}", message.len());
        }
    }
}
