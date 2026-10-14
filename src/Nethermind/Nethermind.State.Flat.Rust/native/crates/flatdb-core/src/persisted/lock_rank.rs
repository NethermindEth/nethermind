//! The lock order of the persisted tier: the repository's finality cache, a bucket, then the arena pool, then the blob
//! table. No two buckets nest, and each lock is otherwise a leaf. Debug and test builds check, per thread, that every
//! lock taken ranks above the locks the thread already holds.

use std::ops::{Deref, DerefMut};

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) enum LockRank {
    FinalityCache,
    Bucket,
    ArenaPool,
    BlobTable,
}

/// A lock guard held with its rank.
pub(crate) struct Ranked<G> {
    // Released before the rank is.
    guard: G,
    _held: Held,
}

/// Takes a lock of `rank` with `acquire`.
///
/// # Panics
/// In debug and test builds, when the thread holds a lock of the same or a higher rank.
pub(crate) fn lock<G>(rank: LockRank, acquire: impl FnOnce() -> G) -> Ranked<G> {
    let held = Held::enter(rank);
    Ranked {
        guard: acquire(),
        _held: held,
    }
}

impl<G: Deref> Deref for Ranked<G> {
    type Target = G::Target;

    fn deref(&self) -> &G::Target {
        &self.guard
    }
}

impl<G: DerefMut> DerefMut for Ranked<G> {
    fn deref_mut(&mut self) -> &mut G::Target {
        &mut self.guard
    }
}

#[cfg(any(debug_assertions, test))]
struct Held(LockRank);

#[cfg(any(debug_assertions, test))]
mod checker {
    use std::cell::RefCell;

    use super::{Held, LockRank};

    thread_local! {
        static HELD: RefCell<Vec<LockRank>> = const { RefCell::new(Vec::new()) };
    }

    impl Held {
        pub(super) fn enter(rank: LockRank) -> Self {
            HELD.with_borrow_mut(|held| {
                if let Some(&highest) = held.last() {
                    assert!(
                        rank > highest,
                        "persisted-tier lock {rank:?} taken while holding {highest:?}"
                    );
                }
                held.push(rank);
            });
            Self(rank)
        }
    }

    impl Drop for Held {
        fn drop(&mut self) {
            HELD.with_borrow_mut(|held| {
                let position = held.iter().rposition(|&rank| rank == self.0);
                held.remove(position.expect("a held rank"));
            });
        }
    }
}

#[cfg(not(any(debug_assertions, test)))]
struct Held;

#[cfg(not(any(debug_assertions, test)))]
impl Held {
    fn enter(_rank: LockRank) -> Self {
        Self
    }
}

#[cfg(test)]
mod tests {
    use parking_lot::Mutex;
    use rstest::rstest;

    use super::{LockRank, lock};

    #[rstest]
    #[case(LockRank::Bucket, LockRank::ArenaPool, true)]
    #[case(LockRank::ArenaPool, LockRank::BlobTable, true)]
    #[case(LockRank::Bucket, LockRank::BlobTable, true)]
    #[case(LockRank::BlobTable, LockRank::ArenaPool, false)]
    #[case(LockRank::ArenaPool, LockRank::Bucket, false)]
    #[case(LockRank::Bucket, LockRank::Bucket, false)]
    #[case(LockRank::FinalityCache, LockRank::Bucket, true)]
    #[case(LockRank::Bucket, LockRank::FinalityCache, false)]
    fn locks_nest_only_in_rank_order(#[case] outer: LockRank, #[case] inner: LockRank, #[case] allowed: bool) {
        let (first, second) = (Mutex::new(1), Mutex::new(2));
        let nested = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let held = lock(outer, || first.lock());
            *lock(inner, || second.lock()) + *held
        }));
        assert_eq!(nested.is_ok(), allowed);
        // A refused lock leaves no rank held behind.
        let bucket = lock(LockRank::Bucket, || first.lock());
        drop(lock(LockRank::ArenaPool, || second.lock()));
        drop(bucket);
    }
}
