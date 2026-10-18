// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

use std::arch::x86_64::*;

use super::{RATE, ROUND_CONSTANTS};

const LANES: usize = 4;

macro_rules! rotate {
    ($value:expr, $shift:literal) => {
        _mm256_or_si256(
            _mm256_slli_epi64::<$shift>($value),
            _mm256_srli_epi64::<{ 64 - $shift }>($value),
        )
    };
}

/// Hashes the first `lanes` messages, each absorbing its own number of blocks.
///
/// # Safety
/// The caller must ensure AVX2 support and `lanes <= 4`. The message callback must
/// return the same slice for each invocation with a given lane.
#[target_feature(enable = "avx2")]
pub(super) unsafe fn keccak_x4<'a>(
    lanes: usize,
    message: impl Fn(usize) -> &'a [u8],
    hashes: &mut [[u8; 32]; LANES],
) {
    debug_assert!(lanes <= LANES);
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

    let mut a = [_mm256_setzero_si256(); 25];
    for block in 0..block_count {
        let addresses: [i64; LANES] = std::array::from_fn(|lane| {
            let block_start = match lane < lanes {
                true if block < last_blocks[lane] => message(lane)[block * RATE..].as_ptr(),
                true if block == last_blocks[lane] => final_blocks[lane].as_ptr(),
                _ => zero_block.as_ptr(),
            };
            block_start as i64
        });
        // SAFETY: Every address identifies a complete rate block in a borrowed
        // message or a live stack buffer; each gather reads one of its 17 words.
        let mut word_addresses = _mm256_loadu_si256(addresses.as_ptr().cast());
        for word in a.iter_mut().take(RATE / 8) {
            *word = _mm256_xor_si256(
                *word,
                _mm256_i64gather_epi64::<1>(std::ptr::null(), word_addresses),
            );
            word_addresses = _mm256_add_epi64(word_addresses, _mm256_set1_epi64x(8));
        }

        let [mut a0, mut a1, mut a2, mut a3, mut a4, mut a5, mut a6, mut a7, mut a8, mut a9, mut a10, mut a11, mut a12, mut a13, mut a14, mut a15, mut a16, mut a17, mut a18, mut a19, mut a20, mut a21, mut a22, mut a23, mut a24] =
            a;
        for round_constant in ROUND_CONSTANTS {
            // Theta: column parities C[x] = A[x,0] ^ A[x,1] ^ A[x,2] ^ A[x,3] ^ A[x,4].
            let c0 = _mm256_xor_si256(
                _mm256_xor_si256(_mm256_xor_si256(_mm256_xor_si256(a0, a5), a10), a15),
                a20,
            );
            let c1 = _mm256_xor_si256(
                _mm256_xor_si256(_mm256_xor_si256(_mm256_xor_si256(a1, a6), a11), a16),
                a21,
            );
            let c2 = _mm256_xor_si256(
                _mm256_xor_si256(_mm256_xor_si256(_mm256_xor_si256(a2, a7), a12), a17),
                a22,
            );
            let c3 = _mm256_xor_si256(
                _mm256_xor_si256(_mm256_xor_si256(_mm256_xor_si256(a3, a8), a13), a18),
                a23,
            );
            let c4 = _mm256_xor_si256(
                _mm256_xor_si256(_mm256_xor_si256(_mm256_xor_si256(a4, a9), a14), a19),
                a24,
            );

            let d0 = _mm256_xor_si256(c4, rotate!(c1, 1));
            let d1 = _mm256_xor_si256(c0, rotate!(c2, 1));
            let d2 = _mm256_xor_si256(c1, rotate!(c3, 1));
            let d3 = _mm256_xor_si256(c2, rotate!(c4, 1));
            let d4 = _mm256_xor_si256(c3, rotate!(c0, 1));
            // Form one rho/pi row at a time to shorten temporary lifetimes before chi.
            let b0 = _mm256_xor_si256(a0, d0);
            let b1 = rotate!(_mm256_xor_si256(a6, d1), 44);
            let b2 = rotate!(_mm256_xor_si256(a12, d2), 43);
            let b3 = rotate!(_mm256_xor_si256(a18, d3), 21);
            let b4 = rotate!(_mm256_xor_si256(a24, d4), 14);
            let n0 = _mm256_xor_si256(b0, _mm256_andnot_si256(b1, b2));
            let n1 = _mm256_xor_si256(b1, _mm256_andnot_si256(b2, b3));
            let n2 = _mm256_xor_si256(b2, _mm256_andnot_si256(b3, b4));
            let n3 = _mm256_xor_si256(b3, _mm256_andnot_si256(b4, b0));
            let n4 = _mm256_xor_si256(b4, _mm256_andnot_si256(b0, b1));
            let b5 = rotate!(_mm256_xor_si256(a3, d3), 28);
            let b6 = rotate!(_mm256_xor_si256(a9, d4), 20);
            let b7 = rotate!(_mm256_xor_si256(a10, d0), 3);
            let b8 = rotate!(_mm256_xor_si256(a16, d1), 45);
            let b9 = rotate!(_mm256_xor_si256(a22, d2), 61);
            let n5 = _mm256_xor_si256(b5, _mm256_andnot_si256(b6, b7));
            let n6 = _mm256_xor_si256(b6, _mm256_andnot_si256(b7, b8));
            let n7 = _mm256_xor_si256(b7, _mm256_andnot_si256(b8, b9));
            let n8 = _mm256_xor_si256(b8, _mm256_andnot_si256(b9, b5));
            let n9 = _mm256_xor_si256(b9, _mm256_andnot_si256(b5, b6));
            let b10 = rotate!(_mm256_xor_si256(a1, d1), 1);
            let b11 = rotate!(_mm256_xor_si256(a7, d2), 6);
            let b12 = rotate!(_mm256_xor_si256(a13, d3), 25);
            let b13 = rotate!(_mm256_xor_si256(a19, d4), 8);
            let b14 = rotate!(_mm256_xor_si256(a20, d0), 18);
            let n10 = _mm256_xor_si256(b10, _mm256_andnot_si256(b11, b12));
            let n11 = _mm256_xor_si256(b11, _mm256_andnot_si256(b12, b13));
            let n12 = _mm256_xor_si256(b12, _mm256_andnot_si256(b13, b14));
            let n13 = _mm256_xor_si256(b13, _mm256_andnot_si256(b14, b10));
            let n14 = _mm256_xor_si256(b14, _mm256_andnot_si256(b10, b11));
            let b15 = rotate!(_mm256_xor_si256(a4, d4), 27);
            let b16 = rotate!(_mm256_xor_si256(a5, d0), 36);
            let b17 = rotate!(_mm256_xor_si256(a11, d1), 10);
            let b18 = rotate!(_mm256_xor_si256(a17, d2), 15);
            let b19 = rotate!(_mm256_xor_si256(a23, d3), 56);
            let n15 = _mm256_xor_si256(b15, _mm256_andnot_si256(b16, b17));
            let n16 = _mm256_xor_si256(b16, _mm256_andnot_si256(b17, b18));
            let n17 = _mm256_xor_si256(b17, _mm256_andnot_si256(b18, b19));
            let n18 = _mm256_xor_si256(b18, _mm256_andnot_si256(b19, b15));
            let n19 = _mm256_xor_si256(b19, _mm256_andnot_si256(b15, b16));
            let b20 = rotate!(_mm256_xor_si256(a2, d2), 62);
            let b21 = rotate!(_mm256_xor_si256(a8, d3), 55);
            let b22 = rotate!(_mm256_xor_si256(a14, d4), 39);
            let b23 = rotate!(_mm256_xor_si256(a15, d0), 41);
            let b24 = rotate!(_mm256_xor_si256(a21, d1), 2);
            let n20 = _mm256_xor_si256(b20, _mm256_andnot_si256(b21, b22));
            let n21 = _mm256_xor_si256(b21, _mm256_andnot_si256(b22, b23));
            let n22 = _mm256_xor_si256(b22, _mm256_andnot_si256(b23, b24));
            let n23 = _mm256_xor_si256(b23, _mm256_andnot_si256(b24, b20));
            let n24 = _mm256_xor_si256(b24, _mm256_andnot_si256(b20, b21));
            a0 = _mm256_xor_si256(n0, _mm256_set1_epi64x(round_constant as i64));
            a1 = n1;
            a2 = n2;
            a3 = n3;
            a4 = n4;
            a5 = n5;
            a6 = n6;
            a7 = n7;
            a8 = n8;
            a9 = n9;
            a10 = n10;
            a11 = n11;
            a12 = n12;
            a13 = n13;
            a14 = n14;
            a15 = n15;
            a16 = n16;
            a17 = n17;
            a18 = n18;
            a19 = n19;
            a20 = n20;
            a21 = n21;
            a22 = n22;
            a23 = n23;
            a24 = n24;
        }
        a = [
            a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18,
            a19, a20, a21, a22, a23, a24,
        ];

        if last_blocks[..lanes].contains(&block) {
            for word_index in 0..4 {
                let mut words = [0u64; LANES];
                // SAFETY: `words` has exactly one vector's writable storage.
                _mm256_storeu_si256(words.as_mut_ptr().cast(), a[word_index]);
                for lane in (0..lanes).filter(|&lane| last_blocks[lane] == block) {
                    hashes[lane][word_index * 8..word_index * 8 + 8]
                        .copy_from_slice(&words[lane].to_le_bytes());
                }
            }
        }
    }
}
