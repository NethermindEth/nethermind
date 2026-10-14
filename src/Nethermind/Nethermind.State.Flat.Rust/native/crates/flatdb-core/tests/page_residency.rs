//! Port of `PageResidencyTrackerTests`: the 8-way clock of the page residency tracker, and the tracking of the reads
//! of an arena reservation's reader. The C# reader cases run over a stub manager; here they run over a real one, whose
//! advisor receives the evictions.
//!
//! `GcMemoryPressure_AccountsForMetadataAndResidentPages` is not ported: Rust has no GC memory-pressure API. The
//! metadata and resident-byte accounting it checks is covered by `resident_bytes_follow_inserts_evictions_and_forgets`.

mod common;

use std::collections::HashSet;
use std::sync::Arc;

use common::arena::{config, write};
use flatdb_core::persisted::storage::arena_manager::ArenaManager;
use flatdb_core::persisted::storage::page_tracker::{PageResidencyTracker, TouchOutcome};
use flatdb_core::persisted::sys::os_page_size;
use flatdb_core::stats::TierCounters;
use rstest::rstest;

/// The ways of a set: a tracker of this capacity has one set, so every key lands in it and the clock is determined.
const WAYS: u32 = 8;

fn one_set() -> PageResidencyTracker {
    PageResidencyTracker::new(WAYS as usize)
}

/// Touches the page and returns what it evicted.
fn evicted(tracker: &PageResidencyTracker, arena_id: i32, page: u32) -> Option<(i32, u32)> {
    match tracker.try_touch(arena_id, page) {
        TouchOutcome::Evicted(arena_id, page) => Some((arena_id, page)),
        TouchOutcome::Hit | TouchOutcome::Inserted => None,
    }
}

#[test]
fn touches_report_hits_inserts_and_the_clock_victim() {
    let tracker = one_set();
    assert_eq!(tracker.try_touch(0, 0), TouchOutcome::Inserted);
    for _ in 0..1000 {
        assert_eq!(
            tracker.try_touch(0, 0),
            TouchOutcome::Hit,
            "repeated touches never evict"
        );
    }
    assert_eq!(tracker.count(), 1);
    for page in 1..WAYS {
        assert_eq!(tracker.try_touch(0, page), TouchOutcome::Inserted);
    }

    // Every way has REF set: the clock clears all 8, wraps to way 0 and evicts the first key.
    assert_eq!(tracker.try_touch(0, WAYS), TouchOutcome::Evicted(0, 0));
    assert!(!tracker.contains_page(0, 0) && tracker.contains_page(0, WAYS));
    assert_eq!(tracker.count(), WAYS as usize);
}

#[test]
fn a_referenced_page_gets_a_second_chance() {
    // After the priming eviction way 0 holds (0, 8) with REF and ways 1..7 hold (0, 1..7) without; the hand is at way 1.
    // Re-touching (0, 3) arms its REF, so three streaming keys evict ways 1, 2 and then 4.
    let tracker = one_set();
    let mut evictions = Vec::new();
    for page in 0..=WAYS {
        evictions.extend(evicted(&tracker, 0, page));
    }
    assert_eq!(evictions, [(0, 0)]);
    assert_eq!(evicted(&tracker, 0, 3), None, "a re-touch is a hit");
    for page in WAYS + 1..WAYS + 4 {
        evictions.extend(evicted(&tracker, 0, page));
    }
    assert_eq!(evictions, [(0, 0), (0, 1), (0, 2), (0, 4)]);
    assert!(tracker.contains_page(0, 3));
}

#[test]
fn a_miss_on_a_full_set_evicts_exactly_one_page() {
    let tracker = one_set();
    for page in 0..WAYS {
        tracker.try_touch(0, page);
    }
    // Re-touching every other page makes the clock clear REF bits on its way to a victim.
    for page in (0..WAYS).step_by(2) {
        tracker.try_touch(0, page);
    }
    assert!(evicted(&tracker, 0, WAYS).is_some());
    assert_eq!(tracker.count(), WAYS as usize);
}

#[rstest]
#[case(0, 0)]
#[case(1, 8)]
#[case(8, 8)]
#[case(9, 16)]
#[case(24, 32)]
fn capacity_rounds_up_to_a_power_of_two_number_of_sets(#[case] requested: usize, #[case] expected: usize) {
    assert_eq!(PageResidencyTracker::new(requested).max_capacity(), expected);
}

#[test]
fn a_disabled_tracker_tracks_nothing() {
    let tracker = PageResidencyTracker::new(0);
    assert_eq!(tracker.try_touch(1, 1), TouchOutcome::Hit);
    assert_eq!(tracker.try_touch(2, 2), TouchOutcome::Hit);
    assert_eq!(
        (tracker.count(), tracker.contains_page(1, 1), tracker.forget(1, 1)),
        (0, false, false)
    );
    assert_eq!(tracker.try_pick_resident_page(), None);
    assert_eq!((tracker.metadata_bytes(), tracker.resident_bytes()), (0, 0));
    assert_eq!(PageResidencyTracker::from_byte_budget(0).max_capacity(), 0);
    assert_eq!(PageResidencyTracker::from_byte_budget(-1).max_capacity(), 0);
}

#[test]
fn forget_removes_only_a_tracked_page() {
    let tracker = one_set();
    tracker.try_touch(5, 3);
    assert!(tracker.forget(5, 3));
    assert!(!tracker.contains_page(5, 3));
    assert_eq!(tracker.count(), 0);

    tracker.try_touch(5, 3);
    assert!(!tracker.forget(5, 4), "never tracked");
    assert!(tracker.contains_page(5, 3));

    tracker.try_touch(5, 3);
    assert!(tracker.forget(5, 3), "a hit re-armed REF, the exchange retries past it");
    assert!(!tracker.contains_page(5, 3));
}

#[test]
fn resident_page_picks_return_only_tracked_pages() {
    let tracker = one_set();
    assert_eq!(tracker.try_pick_resident_page(), None, "empty");
    tracker.try_touch(5, 3);
    tracker.forget(5, 3);
    assert_eq!(tracker.try_pick_resident_page(), None, "forgotten");

    let inserted: HashSet<(i32, u32)> = (0..WAYS).map(|page| (7, page)).collect();
    for &(arena_id, page) in &inserted {
        tracker.try_touch(arena_id, page);
    }
    for _ in 0..100 {
        assert!(inserted.contains(&tracker.try_pick_resident_page().unwrap()));
    }
}

#[test]
fn resident_bytes_follow_inserts_evictions_and_forgets() {
    let page_size = os_page_size() as i64;
    let tracker = one_set();
    assert!(tracker.metadata_bytes() > 0);
    assert_eq!(tracker.resident_bytes(), 0);
    tracker.try_touch(0, 0);
    tracker.try_touch(0, 0);
    assert_eq!(tracker.resident_bytes(), page_size, "a hit adds nothing");
    for page in 1..WAYS {
        tracker.try_touch(0, page);
    }
    assert_eq!(tracker.resident_bytes(), i64::from(WAYS) * page_size);
    for page in WAYS..4 * WAYS {
        tracker.try_touch(0, page);
    }
    assert_eq!(
        tracker.resident_bytes(),
        i64::from(WAYS) * page_size,
        "an eviction swaps one page for another"
    );

    let present = (0..4 * WAYS)
        .rev()
        .find(|&page| tracker.contains_page(0, page))
        .unwrap();
    assert!(tracker.forget(0, present));
    assert_eq!(tracker.resident_bytes(), i64::from(WAYS - 1) * page_size);
    assert_eq!(tracker.try_touch(0, present), TouchOutcome::Inserted);
    assert_eq!(tracker.resident_bytes(), i64::from(WAYS) * page_size);
}

/// A manager whose tracker has `tracked_pages` slots.
fn tracking_manager(directory: &tempfile::TempDir, tracked_pages: i64) -> ArenaManager {
    let mut config = config(64 * 1024);
    config.page_cache_bytes = tracked_pages * os_page_size() as i64;
    let manager = ArenaManager::new(directory.path(), config, Arc::new(TierCounters::default())).unwrap();
    manager.initialize(&[]).unwrap();
    manager
}

#[test]
fn reader_reads_track_every_spanned_page_once_per_page() {
    let page_size = os_page_size();
    let directory = tempfile::tempdir().unwrap();
    let manager = tracking_manager(&directory, 1024);
    let (location, reservation) = write(&manager, &vec![7; 3 * page_size], false);
    let tracker = manager.page_tracker();
    let first_page = (location.offset as usize / page_size) as u32;
    let reader = reservation.reader();

    assert!(reader.read(page_size - 8, 16).is_some(), "straddles pages 0 and 1");
    assert!(tracker.contains_page(location.arena_id, first_page));
    assert!(tracker.contains_page(location.arena_id, first_page + 1));
    assert_eq!(tracker.count(), 2);

    // Reads staying on the page of the previous one skip the tracker.
    tracker.forget(location.arena_id, first_page + 1);
    for offset in page_size + 1..page_size + 100 {
        assert!(reader.read(offset, 1).is_some());
    }
    assert_eq!(tracker.count(), 1);
    assert!(reader.read(2 * page_size, 1).is_some());
    assert!(tracker.contains_page(location.arena_id, first_page + 2));

    let pinned = reservation.reader();
    assert_eq!(
        pinned.read(0, 2 * page_size + 1).map(<[u8]>::len),
        Some(2 * page_size + 1)
    );
    assert_eq!(tracker.count(), 3, "one read over three pages");
}

#[test]
fn reader_evictions_reach_the_advisor() {
    let page_size = os_page_size();
    let directory = tempfile::tempdir().unwrap();
    let manager = tracking_manager(&directory, i64::from(WAYS));
    // Separate pools, so two arena files.
    let (first, first_reservation) = write(&manager, &vec![1; WAYS as usize * page_size], false);
    let (second, second_reservation) = write(&manager, &vec![2; page_size], true);
    assert_ne!(first.arena_id, second.arena_id);

    let reader = first_reservation.reader();
    for page in 0..WAYS as usize {
        assert!(reader.read(page * page_size, 1).is_some());
    }
    assert_eq!(manager.evictions_queued() + manager.evictions_inline_fallback(), 0);

    assert!(
        second_reservation.reader().read(0, 1).is_some(),
        "the 9th page evicts the clock victim"
    );
    assert!(
        !manager
            .page_tracker()
            .contains_page(first.arena_id, (first.offset as usize / page_size) as u32)
    );
    assert!(
        manager
            .page_tracker()
            .contains_page(second.arena_id, (second.offset as usize / page_size) as u32)
    );
    manager.dispose();
    assert_eq!(manager.evictions_queued() + manager.evictions_inline_fallback(), 1);
    assert_eq!(
        manager.evictions_dispatched() + manager.evictions_skipped_retouched(),
        1
    );
}

#[test]
fn reader_of_an_untracked_manager_reads() {
    let directory = tempfile::tempdir().unwrap();
    let manager = tracking_manager(&directory, 0);
    let (_, reservation) = write(&manager, &[9; 64], false);
    let reader = reservation.reader();
    assert_eq!(reader.read(4, 8), Some(&[9u8; 8][..]));
    assert_eq!(reader.read(0, 16).map(<[u8]>::len), Some(16));
    assert_eq!(manager.page_tracker().count(), 0);
}

/// `TouchRangePopulate` tracks every page of its range, queueing the clock victims of a full tracker, and adds nothing
/// for pages already tracked; `AdviseDontNeed` and the demotion's `AdviseAndFadviseDontNeed` then forget the
/// footprint's pages while its bytes stay readable.
#[rstest]
fn populate_tracks_the_range_and_advising_forgets_it_keeping_the_bytes(
    #[values(1, 3)] pages: usize,
    #[values(false, true)] demotion: bool,
) {
    let page_size = os_page_size();
    let directory = tempfile::tempdir().unwrap();
    let manager = tracking_manager(&directory, i64::from(WAYS));
    let tracker = manager.page_tracker();
    let queued = || manager.evictions_queued() + manager.evictions_inline_fallback();
    let (filler, filler_reservation) = write(&manager, &vec![1; WAYS as usize * page_size], false);
    let data: Vec<u8> = (0..pages * page_size).map(|index| (index % 251) as u8).collect();
    let (location, reservation) = write(&manager, &data, false);
    assert_eq!(filler.arena_id, location.arena_id);
    let first_page = (location.offset as usize / page_size) as u32;
    let tracked_pages = || {
        (first_page..first_page + pages as u32)
            .filter(|&page| tracker.contains_page(location.arena_id, page))
            .count()
    };

    filler_reservation.touch_range_populate(0, filler.size);
    assert_eq!(
        (tracker.count(), queued()),
        (WAYS as usize, 0),
        "the filler fills the one set"
    );

    reservation.touch_range_populate(0, data.len() as i64);
    assert_eq!(tracked_pages(), pages);
    assert_eq!(
        (tracker.count(), queued()),
        (WAYS as usize, pages as i64),
        "every new page evicts one"
    );

    reservation.touch_range_populate(page_size as i64 / 2, (data.len() - page_size / 2) as i64);
    assert_eq!(
        (tracker.count(), queued()),
        (WAYS as usize, pages as i64),
        "tracked pages add nothing"
    );

    if demotion {
        reservation.advise_and_fadvise_dont_need();
    } else {
        reservation.advise_dont_need();
    }
    assert_eq!(tracked_pages(), 0);
    assert_eq!(tracker.count(), WAYS as usize - pages);
    assert_eq!(reservation.bytes(), &data[..], "the advised range reads back from disk");
}
