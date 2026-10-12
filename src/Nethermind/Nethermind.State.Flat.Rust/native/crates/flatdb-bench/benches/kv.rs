//! Read and write paths of the key-value layer, over the in-memory backend.

use std::hint::black_box;

use criterion::{BatchSize, Criterion, criterion_group, criterion_main};
use flatdb_core::kv::mem::MemKv;
use flatdb_core::kv::{Column, KvStore, WriteFlags};

const KEYS: u32 = 10_000;

fn filled() -> MemKv {
    let kv = MemKv::new();
    let mut batch = kv.write_batch().unwrap();
    for i in 0..KEYS {
        batch
            .put(Column::Account, &i.to_be_bytes(), &[0xAB; 70], WriteFlags::NONE)
            .unwrap();
    }
    batch.commit().unwrap();
    kv
}

fn kv(c: &mut Criterion) {
    let kv = filled();
    let mut buffer = [0u8; 128];
    let mut key = 0u32;
    c.bench_function("mem_get_into", |b| {
        b.iter(|| {
            key = (key + 7919) % KEYS;
            black_box(kv.get(Column::Account, &key.to_be_bytes(), &mut buffer).unwrap())
        })
    });

    c.bench_function("mem_batch_1k_puts", |b| {
        b.iter_batched(
            MemKv::new,
            |kv| {
                let mut batch = kv.write_batch().unwrap();
                for i in 0..1_000u32 {
                    batch
                        .put(Column::Storage, &i.to_be_bytes(), &[1; 32], WriteFlags::DISABLE_WAL)
                        .unwrap();
                }
                batch.commit().unwrap();
            },
            BatchSize::SmallInput,
        )
    });
}

criterion_group!(benches, kv);
criterion_main!(benches);
