// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Keccak-256, with an AVX-512 kernel hashing eight messages of the same padded length at once.

use sha3::{Digest, Keccak256};

const RATE: usize = 136;
const LANES: usize = 8;

pub fn keccak(data: &[u8]) -> [u8; 32] {
    Keccak256::digest(data).into()
}

/// Hashes every message into the hash at the same index.
pub fn keccak_many(messages: &[&[u8]], hashes: &mut [[u8; 32]]) {
    #[cfg(target_arch = "x86_64")]
    if messages.len() >= 2 && std::arch::is_x86_feature_detected!("avx512f") {
        let blocks = |index: &usize| messages[*index].len() / RATE + 1;
        let mut order: Vec<usize> = (0..messages.len()).collect();
        order.sort_unstable_by_key(blocks);
        for same_length in order.chunk_by(|left, right| blocks(left) == blocks(right)) {
            for group in same_length.chunks(LANES) {
                if group.len() == 1 {
                    hashes[group[0]] = keccak(messages[group[0]]);
                    continue;
                }
                let mut group_hashes = [[0u8; 32]; LANES];
                // SAFETY: AVX-512F support was checked above.
                unsafe {
                    avx512::keccak_x8(
                        group.iter().map(|&index| messages[index]),
                        blocks(&group[0]),
                        &mut group_hashes,
                    )
                };
                for (lane, &index) in group.iter().enumerate() {
                    hashes[index] = group_hashes[lane];
                }
            }
        }
        return;
    }

    for (message, hash) in messages.iter().zip(hashes.iter_mut()) {
        *hash = keccak(message);
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

    /// Hashes up to eight messages that all pad to `blocks` rate blocks; lanes past the messages are left unused.
    #[target_feature(enable = "avx512f")]
    pub unsafe fn keccak_x8<'a>(
        messages: impl Iterator<Item = &'a [u8]>,
        blocks: usize,
        hashes: &mut [[u8; 32]; LANES],
    ) {
        // Row `block * 17 + lane` holds that lane of every message, so each row loads straight into a vector.
        let padded_length = blocks * RATE;
        let mut rows = vec![[0u64; LANES]; padded_length / 8];
        let mut padded = vec![0u8; padded_length];
        for (message_index, message) in messages.enumerate() {
            padded.fill(0);
            padded[..message.len()].copy_from_slice(message);
            padded[message.len()] |= 0x01;
            padded[padded_length - 1] |= 0x80;
            for (row, word) in rows.iter_mut().zip(padded.chunks_exact(8)) {
                row[message_index] = u64::from_le_bytes(word.try_into().unwrap());
            }
        }

        let mut a = [_mm512_setzero_si512(); 25];
        let mut b = [_mm512_setzero_si512(); 25];
        let mut c = [_mm512_setzero_si512(); 5];
        for block in rows.chunks_exact(RATE / 8) {
            for (lane, row) in block.iter().enumerate() {
                a[lane] = _mm512_xor_si512(a[lane], _mm512_loadu_si512(row.as_ptr().cast()));
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
        }

        for lane in 0..4 {
            let mut words = [0u64; LANES];
            _mm512_storeu_si512(words.as_mut_ptr().cast(), a[lane]);
            for (hash, word) in hashes.iter_mut().zip(words) {
                hash[lane * 8..lane * 8 + 8].copy_from_slice(&word.to_le_bytes());
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
        keccak_many(&slices, &mut hashes);
        for (message, hash) in slices.iter().zip(&hashes) {
            assert_eq!(*hash, keccak(message), "length {}", message.len());
        }
    }
}
