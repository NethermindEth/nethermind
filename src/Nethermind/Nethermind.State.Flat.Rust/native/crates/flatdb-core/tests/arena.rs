//! The storage of the persisted tier: ports of `StorageLayerTests` (arena cases), `ArenaManagerEvictionQueueTests`,
//! `ArenaManagerForgetOnAdviseTests`, `ArenaMetricsTests` and `ArenaReclaimPunchHoleTests`, and the blob manager.
//!
//! The eviction-queue cases are timing-free: instead of polling the advisor thread they dispose the manager, which
//! dispatches everything still queued before returning. `PersistedSnapshotLoaderTests` is ported with the loader, in
//! `persisted_repository.rs`.

mod common;

use std::collections::BTreeSet;
use std::os::unix::fs::{FileExt, MetadataExt};
use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use common::arena::{config, write};
use flatdb_core::persisted::codec::{CatalogEntry, SnapshotLocation};
use flatdb_core::persisted::io::TableSink;
use flatdb_core::persisted::page_layout::NodeRef;
use flatdb_core::persisted::reader::BlobSource;
use flatdb_core::persisted::storage::arena_file::ArenaFile;
use flatdb_core::persisted::storage::arena_manager::{ArenaConfig, ArenaManager};
use flatdb_core::persisted::storage::arena_reservation::ArenaReservation;
use flatdb_core::persisted::storage::blob_manager::BlobManager;
use flatdb_core::persisted::storage::blob_writer::BlobArenaWriter;
use flatdb_core::persisted::storage::page_tracker::TouchOutcome;
use flatdb_core::persisted::sys::{os_page_size, round_up_to_os_page};
use flatdb_core::snapshot::SnapshotTier;
use flatdb_core::stats::TierCounters;
use flatdb_core::types::StateId;
use rstest::rstest;

fn manager(directory: &Path, config: ArenaConfig) -> (ArenaManager, Arc<TierCounters>) {
    let counters = Arc::new(TierCounters::default());
    let manager = ArenaManager::new(directory, config, counters.clone()).unwrap();
    manager.initialize(&[]).unwrap();
    (manager, counters)
}

fn tracking(pages: i64, arena_file_size_bytes: i64) -> ArenaConfig {
    ArenaConfig {
        page_cache_bytes: pages * os_page_size() as i64,
        ..config(arena_file_size_bytes)
    }
}

fn read_all(reservation: &Arc<ArenaReservation>) -> Vec<u8> {
    let session = reservation.begin_whole_read_session().unwrap();
    let reader = session.reader();
    reader.read(0, reader.len()).unwrap().to_vec()
}

fn files(directory: &Path, prefix: &str) -> Vec<String> {
    let mut names: Vec<String> = std::fs::read_dir(directory)
        .unwrap()
        .map(|entry| entry.unwrap().file_name().into_string().unwrap())
        .filter(|name| name.starts_with(prefix))
        .collect();
    names.sort();
    names
}

fn entry(location: SnapshotLocation) -> CatalogEntry {
    CatalogEntry {
        from: StateId::new(0, [1; 32]),
        to: StateId::new(1, [2; 32]),
        location,
        tier: SnapshotTier::PersistedBase,
    }
}

/// Deterministic noise, so a page holds real disk blocks.
fn noise(length: usize, seed: u64) -> Vec<u8> {
    let mut state = seed | 1;
    (0..length)
        .map(|_| {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state as u8
        })
        .collect()
}

#[test]
fn arena_file_written_through_the_file_reads_through_the_mapping() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("arena.bin");
    let arena = ArenaFile::open(0, &path, 1024 * 1024, false).unwrap();
    let data = noise(1000, 3);
    let file = std::fs::OpenOptions::new().write(true).open(&path).unwrap();
    file.write_all_at(&[1, 2, 3, 4, 5], 0).unwrap();
    file.write_all_at(&data, 5).unwrap();

    assert_eq!(arena.bytes(0, 5), [1, 2, 3, 4, 5]);
    assert_eq!(arena.bytes(5, 1000), data);
    assert_eq!(arena.mapped_size(), 1024 * 1024);
}

#[test]
fn completed_writes_read_back_and_the_next_one_starts_on_the_next_page() {
    let directory = tempfile::tempdir().unwrap();
    let (manager, _) = manager(directory.path(), config(64 * 1024));
    let (location, reservation) = write(&manager, &[1, 2, 3, 4, 5, 6, 7, 8], false);
    assert_eq!(read_all(&reservation), [1, 2, 3, 4, 5, 6, 7, 8]);
    assert_eq!(
        location.size, 8,
        "the size is the bytes written; only the frontier is padded"
    );

    let (next, _next_reservation) = write(&manager, &[4, 5], false);
    assert_eq!(next.arena_id, location.arena_id);
    assert_eq!(next.offset, round_up_to_os_page(location.offset + location.size));
    assert!(manager.open(&SnapshotLocation { arena_id: 99, ..next }).is_err());
}

/// Both pools share the reserve, cancel and return cycle.
#[rstest]
fn a_cancelled_write_returns_the_file_to_its_pool(#[values(false, true)] small: bool) {
    let directory = tempfile::tempdir().unwrap();
    let (manager, _) = manager(directory.path(), config(64 * 1024));
    let (baseline, _baseline_reservation) = write(&manager, &[0xAA], small);
    drop(manager.create_writer(0, small).unwrap());

    let (reused, _reused_reservation) = write(&manager, &[0; 50], small);
    assert_eq!(reused.arena_id, baseline.arena_id);
    assert_eq!(reused.offset, round_up_to_os_page(baseline.offset + baseline.size));
}

/// Regression of C#: every live byte dying while a writer holds the file must neither delete it under the writer nor
/// return a dead file to the pool.
#[rstest]
fn a_file_dying_under_a_writer_survives_until_the_writer_finishes(#[values(false, true)] complete_second_write: bool) {
    let directory = tempfile::tempdir().unwrap();
    let (manager, _) = manager(directory.path(), config(64 * 1024));
    let (baseline, baseline_reservation) = write(&manager, &[1, 2, 3], false);

    let mut writer = manager.create_writer(4, false).unwrap();
    drop(baseline_reservation);
    let second = complete_second_write.then(|| {
        writer.write(&[4, 5, 6, 7]).unwrap();
        let (location, reservation) = writer.complete().unwrap();
        assert_eq!(location.arena_id, baseline.arena_id);
        assert_eq!(read_all(&reservation), [4, 5, 6, 7]);
        reservation
    });
    // Without completion the writer drops here and cancels; no live byte is left, so the file goes.

    let (next, _next_reservation) = write(&manager, &[8, 9], false);
    assert_eq!(next.arena_id == baseline.arena_id, complete_second_write);
    if !complete_second_write {
        assert_eq!(files(directory.path(), "arena_").len(), 1, "the dead file was deleted");
    }
    drop(second);
}

#[test]
fn a_dedicated_file_shrinks_to_its_data() {
    let directory = tempfile::tempdir().unwrap();
    let config = ArenaConfig {
        dedicated_arena_threshold_bytes: 64 * 1024,
        ..config(4096)
    };
    let (manager, _) = manager(directory.path(), config);
    let mut writer = manager.create_writer(256 * 1024, false).unwrap();
    writer.write(&[1, 2, 3, 4, 5, 6, 7, 8]).unwrap();
    let (location, reservation) = writer.complete().unwrap();

    let dedicated = files(directory.path(), "dedicated_");
    assert_eq!(dedicated, ["dedicated_0000.bin"]);
    assert_eq!(
        std::fs::metadata(directory.path().join(&dedicated[0])).unwrap().len(),
        8
    );
    assert_eq!(
        (location.offset, read_all(&reservation)),
        (0, vec![1, 2, 3, 4, 5, 6, 7, 8])
    );
    drop(reservation);
    assert!(
        files(directory.path(), "dedicated_").is_empty(),
        "the released file is deleted"
    );
}

#[test]
fn concurrent_writers_and_the_two_pools_use_separate_files() {
    let directory = tempfile::tempdir().unwrap();
    let (manager, _) = manager(directory.path(), config(64 * 1024));
    let mut first = manager.create_writer(3, false).unwrap();
    let mut second = manager.create_writer(3, false).unwrap();
    first.write(&[1, 2, 3]).unwrap();
    second.write(&[1, 2, 3]).unwrap();
    let (first, _first_reservation) = first.complete().unwrap();
    let (second, _second_reservation) = second.complete().unwrap();
    assert_ne!(first.arena_id, second.arena_id, "a writer holds its file");

    let (small, _small_reservation) = write(&manager, &[1, 2, 3], true);
    let (small_again, _small_again_reservation) = write(&manager, &[1, 2, 3], true);
    let (large, _large_reservation) = write(&manager, &[1, 2, 3], false);
    assert!(small.arena_id != first.arena_id && small.arena_id != second.arena_id);
    assert_eq!(
        small_again.arena_id, small.arena_id,
        "small writes pack into the small pool's file"
    );
    assert!(large.arena_id == first.arena_id || large.arena_id == second.arena_id);
    assert_eq!(files(directory.path(), "small_arena_").len(), 1);
    assert_eq!(files(directory.path(), "arena_").len(), 2, "the prefix is anchored");
}

#[test]
fn a_small_file_kept_on_shutdown_reloads_from_its_catalog_entry() {
    let directory = tempfile::tempdir().unwrap();
    let location = {
        let (first, _) = manager(directory.path(), config(64 * 1024));
        let (location, reservation) = write(&first, &[9, 8, 7, 6, 5], true);
        reservation.persist_on_shutdown();
        location
    };

    let counters = Arc::new(TierCounters::default());
    let second = ArenaManager::new(directory.path(), config(64 * 1024), counters).unwrap();
    assert!(second.initialize(&[entry(location)]).unwrap().is_empty());
    assert_eq!(read_all(&second.open(&location).unwrap()), [9, 8, 7, 6, 5]);
}

#[test]
fn arena_gauges_count_frontiers_not_mapped_sizes() {
    let page_size = os_page_size();
    let directory = tempfile::tempdir().unwrap();
    let (manager, counters) = manager(directory.path(), config((64 * 1024).max(4 * page_size as i64)));
    let gauges = || {
        [
            &counters.arena_file_count,
            &counters.arena_allocated_bytes,
            &counters.arena_reservation_count,
            &counters.arena_reservation_bytes,
            &counters.blob_file_count,
            &counters.blob_allocated_bytes,
        ]
        .map(|gauge| gauge.load(Ordering::Relaxed))
    };
    assert_eq!(gauges(), [0; 6], "no file before the first writer");

    let mut writer = manager.create_writer(page_size as i64, false).unwrap();
    assert_eq!(gauges(), [1, 0, 0, 0, 0, 0], "a new file has no allocated bytes");
    writer.write(&vec![0; page_size]).unwrap();
    let (_, reservation) = writer.complete().unwrap();
    let page = page_size as i64;
    assert_eq!(gauges(), [1, page, 1, page, 0, 0]);

    drop(reservation);
    assert_eq!(gauges(), [0; 6], "the last reservation drops the file");
}

#[test]
fn blob_gauges_count_frontiers_and_leave_the_arena_ones() {
    let directory = tempfile::tempdir().unwrap();
    let counters = Arc::new(TierCounters::default());
    let blobs = BlobManager::new(directory.path(), 64 * 1024, counters.clone()).unwrap();
    let mut writer = blobs.create_writer(1024).unwrap();
    assert_eq!(counters.blob_file_count.load(Ordering::Relaxed), 1);
    assert_eq!(counters.blob_allocated_bytes.load(Ordering::Relaxed), 0);
    writer.writer().unwrap().write_rlp(&[0; 1024]).unwrap();
    writer.complete().unwrap();
    assert_eq!(counters.blob_allocated_bytes.load(Ordering::Relaxed), 1024);
    assert_eq!(counters.arena_file_count.load(Ordering::Relaxed), 0);
    assert_eq!(counters.arena_allocated_bytes.load(Ordering::Relaxed), 0);

    drop(writer);
    blobs.dispose();
    assert_eq!(counters.blob_file_count.load(Ordering::Relaxed), 0);
    assert_eq!(counters.blob_allocated_bytes.load(Ordering::Relaxed), 0);
}

#[test]
fn initialize_reports_missing_arenas_and_never_reuses_their_ids() {
    let directory = tempfile::tempdir().unwrap();
    let write_kept = |manager: &ArenaManager| {
        let (location, reservation) = write(manager, &[0; 4096], false);
        reservation.persist_on_shutdown();
        location
    };
    let live = write_kept(&manager(directory.path(), config(64 * 1024)).0);
    let orphan = SnapshotLocation {
        arena_id: live.arena_id + 1,
        ..live
    };
    let max_id_orphan = SnapshotLocation {
        arena_id: i32::MAX,
        ..live
    };
    let mut entries = vec![entry(live), entry(orphan), entry(max_id_orphan)];
    let missing = BTreeSet::from([orphan.arena_id, i32::MAX]);

    let replacement = {
        let reopened = ArenaManager::new(directory.path(), config(64 * 1024), Arc::default()).unwrap();
        assert_eq!(reopened.initialize(&entries).unwrap(), missing);
        reopened.open(&live).unwrap().persist_on_shutdown();
        write_kept(&reopened)
    };
    assert!(replacement.arena_id > orphan.arena_id);

    entries.push(entry(replacement));
    let restarted = ArenaManager::new(directory.path(), config(64 * 1024), Arc::default()).unwrap();
    assert_eq!(restarted.initialize(&entries).unwrap(), missing);
    assert_eq!(read_all(&restarted.open(&replacement).unwrap()), [0; 4096]);
}

#[test]
fn without_a_tracker_evictions_are_ignored() {
    let directory = tempfile::tempdir().unwrap();
    let (manager, _) = manager(directory.path(), config(64 * 1024));
    assert_eq!(manager.page_tracker().max_capacity(), 0);
    manager.queue_eviction(0, 0);
    manager.forget_tracker_range(0, 0, 1 << 20);
    manager.dispose();
    assert_eq!(
        [
            manager.evictions_queued(),
            manager.evictions_inline_fallback(),
            manager.evictions_dispatched(),
            manager.pages_refreshed()
        ],
        [0; 4]
    );
}

/// `QueueEviction_EnqueuesAndDrainsEventually`, `QueueEviction_SkipsDispatchWhenPageBackInTracker`,
/// `WarmTouch_FiresOnDispatch_WithStaleArenaIdsDoesNotThrow` and `Dispose_DrainsRemainingEntries`: arena 42 has no
/// file, so a dispatch only counts, and its keep-warm touches skip the stale ids the tracker holds.
#[test]
fn queued_evictions_are_dispatched_unless_the_page_was_read_again() {
    let directory = tempfile::tempdir().unwrap();
    let (manager, counters) = manager(directory.path(), tracking(1024, 64 * 1024));
    let tracker = manager.page_tracker();
    tracker.try_touch(777, 0);
    tracker.try_touch(778, 1);
    tracker.try_touch(42, 7);

    for page in 0..16 {
        manager.queue_eviction(42, page);
    }
    manager.dispose();
    assert_eq!(
        (manager.evictions_queued(), manager.evictions_inline_fallback()),
        (16, 0)
    );
    assert_eq!(
        (manager.evictions_dispatched(), manager.evictions_skipped_retouched()),
        (15, 1),
        "(42, 7) is tracked again"
    );
    assert_eq!(counters.page_tracker_evictions_dispatched.load(Ordering::Relaxed), 15);
    assert_eq!(manager.pages_refreshed(), 0, "no tracked page has a file");
    assert_eq!(
        counters.page_tracker_metadata_bytes.load(Ordering::Relaxed),
        0,
        "zeroed on dispose"
    );
}

/// `WarmTouch_FiresOnForgetTrackerRange_WithEmptyTrackerDoesNotThrow`, `WarmTouch_RefreshesRegisteredArenaPage` and
/// `ForgetUntrackedRange_DoesNotWarm`: only pages actually forgotten trigger keep-warm touches, which refresh the
/// tracked pages of registered files.
#[test]
fn forgetting_tracked_pages_refreshes_resident_ones() {
    let page_size = os_page_size() as i64;
    let directory = tempfile::tempdir().unwrap();
    let (manager, _) = manager(directory.path(), tracking(8, 64 * 1024));
    manager.forget_tracker_range(5, 0, 16 * page_size);
    manager.page_tracker().try_touch(9, 0);
    manager.forget_tracker_range(5, 0, 16 * page_size);
    manager.forget_tracker_range(5, 0, 0);
    manager.forget_tracker_range(5, 0, -1);
    assert_eq!(manager.pages_refreshed(), 0, "nothing was forgotten");

    let (location, _reservation) = write(&manager, &vec![0; 2 * page_size as usize], false);
    let first_page = (location.offset / page_size) as u32;
    manager.page_tracker().try_touch(location.arena_id, first_page);
    manager.page_tracker().try_touch(location.arena_id, first_page + 1);
    manager.forget_tracker_range(location.arena_id + 1000, 0, 1000 * page_size);
    assert_eq!(manager.pages_refreshed(), 0, "an untracked range warms nothing");

    manager.forget_tracker_range(location.arena_id, location.offset, page_size);
    assert!(!manager.page_tracker().contains_page(location.arena_id, first_page));
    assert!(manager.pages_refreshed() > 0, "the second page was refreshed");
}

/// `ArenaManagerForgetOnAdviseTests`: whole-range `MADV_DONTNEED` keeps the tracker in step with the page cache.
#[test]
fn advising_or_releasing_a_reservation_forgets_its_whole_pages() {
    let page_size = os_page_size() as i64;
    let directory = tempfile::tempdir().unwrap();
    let (manager, _) = manager(directory.path(), tracking(1024, 1 << 20));
    let tracker = manager.page_tracker();
    let pages = |location: SnapshotLocation, count: u32| {
        let first = (location.offset / page_size) as u32;
        (first..first + count).map(move |page| (location.arena_id, page))
    };

    let (advised, advised_reservation) = write(&manager, &vec![0; 10 * page_size as usize], false);
    pages(advised, 10)
        .for_each(|(arena_id, page)| assert_eq!(tracker.try_touch(arena_id, page), TouchOutcome::Inserted));
    advised_reservation.advise_dont_need();
    assert!(pages(advised, 10).all(|(arena_id, page)| !tracker.contains_page(arena_id, page)));

    // [page / 2, page / 2 + 3 pages) covers pages 1 and 2 wholly; 0 and 3 only partly, 4 not at all.
    for page in 0..5 {
        tracker.try_touch(7, page);
    }
    manager.forget_tracker_range(7, page_size / 2, 3 * page_size);
    assert_eq!(
        (0..5).map(|page| tracker.contains_page(7, page)).collect::<Vec<_>>(),
        [true, false, false, true, true]
    );

    let (released, released_reservation) = write(&manager, &vec![0; 4 * page_size as usize], false);
    pages(released, 4).for_each(|(arena_id, page)| {
        tracker.try_touch(arena_id, page);
    });
    drop(released_reservation);
    assert!(pages(released, 4).all(|(arena_id, page)| !tracker.contains_page(arena_id, page)));
}

/// `ReservationCleanup_PunchesHole_ForDeadRange_WhenEnabled`: a released reservation's disk blocks are freed, but
/// only when configured; a filesystem without hole punching skips the case.
#[rstest]
fn a_released_reservation_punches_its_blocks_when_enabled(#[values(false, true)] punch_hole_on_reclaim: bool) {
    let page_size = os_page_size();
    let directory = tempfile::tempdir().unwrap();
    let config = ArenaConfig {
        punch_hole_on_reclaim,
        ..config(8 << 20)
    };
    let (manager, _) = manager(directory.path(), config);
    // The second reservation keeps the file alive, so the release of the first reclaims rather than deletes.
    let (first, first_reservation) = write(&manager, &noise(64 * page_size, 1), false);
    let (second, second_reservation) = write(&manager, &noise(page_size, 2), false);
    assert_eq!(first.arena_id, second.arena_id);

    let path = directory.path().join(&files(directory.path(), "arena_")[0]);
    std::fs::File::open(&path).unwrap().sync_all().unwrap();
    let blocks = || std::fs::metadata(&path).unwrap().blocks();
    let before = blocks();
    assert!(before > 0);

    drop(first_reservation);
    if punch_hole_on_reclaim && !manager.punch_hole_supported() {
        eprintln!("skipped: the filesystem of the temporary directory does not support fallocate punch-hole");
        return;
    }
    let after = blocks();
    if punch_hole_on_reclaim {
        assert!(after < before, "{after} blocks after, {before} before");
    } else {
        assert_eq!(after, before);
    }
    assert_eq!(read_all(&second_reservation), noise(page_size, 2));
}

#[test]
fn a_reader_keeps_reading_while_its_neighbours_are_reclaimed() {
    let page_size = os_page_size();
    let directory = tempfile::tempdir().unwrap();
    let config = ArenaConfig {
        punch_hole_on_reclaim: true,
        ..tracking(64, 1 << 20)
    };
    let (manager, _) = manager(directory.path(), config);
    let neighbours: Vec<_> = (0..4)
        .map(|seed| write(&manager, &noise(3 * page_size, seed), false))
        .collect();
    let (_, kept) = write(&manager, &noise(5 * page_size, 9), false);
    let more: Vec<_> = (10..14)
        .map(|seed| write(&manager, &noise(3 * page_size, seed), false))
        .collect();

    let done = AtomicBool::new(false);
    std::thread::scope(|scope| {
        let reader = scope.spawn(|| {
            let expected = noise(5 * page_size, 9);
            let mut reads = 0;
            while !done.load(Ordering::Acquire) || reads == 0 {
                assert_eq!(kept.reader().read(0, expected.len()), Some(&expected[..]));
                assert_eq!(read_all(&kept), expected);
                reads += 1;
            }
        });
        drop(neighbours);
        drop(more);
        done.store(true, Ordering::Release);
        reader.join().unwrap();
    });
}

#[test]
fn blob_writers_append_page_aligned_runs_that_read_back() {
    let directory = tempfile::tempdir().unwrap();
    let blobs = BlobManager::new(directory.path(), 1 << 20, Arc::default()).unwrap();
    blobs.initialize().unwrap();

    let mut writer = blobs.create_writer(10_000).unwrap();
    assert!(writer.fsync().is_err(), "fsync needs a completed write");
    let items = [noise(3000, 1), noise(2000, 2), noise(100, 3)];
    let refs: Vec<NodeRef> = items
        .iter()
        .map(|item| writer.writer().unwrap().write_rlp(item).unwrap())
        .collect();
    assert_eq!(
        refs.iter().map(|node_ref| node_ref.rlp_data_offset).collect::<Vec<_>>(),
        [0, 4096, 6096],
        "2000 bytes would cross the first page"
    );
    writer.complete().unwrap();
    writer.fsync().unwrap();
    assert!(writer.writer().is_err() && writer.complete().is_err());

    // A second writer completing on the same file moves the frontier, not the first writer's end.
    let mut second = blobs.create_writer(10).unwrap();
    assert_eq!((second.blob_arena_id(), second.start_offset()), (0, 6196));
    second.writer().unwrap().write_rlp(&noise(100, 4)).unwrap();
    second.complete().unwrap();
    assert_eq!((writer.written(), second.written()), (6196, 6296));
    drop(second);

    for (item, node_ref) in items.iter().zip(&refs) {
        let mut buffer = vec![0; item.len()];
        let read = blobs
            .random_read(node_ref.blob_arena_id, node_ref.rlp_data_offset, &mut buffer)
            .unwrap();
        assert_eq!((read, &buffer), (item.len(), item));
    }
    let mut past_end = [0; 8];
    assert_eq!(
        blobs.random_read(0, 6290, &mut past_end).unwrap(),
        6,
        "a short read at the end"
    );
    assert!(blobs.random_read(1, 0, &mut past_end).is_err(), "no file 1");
    drop(writer);

    // A cancelled writer leaves the frontier and returns the file.
    let cancelled = blobs.create_writer(10).unwrap();
    assert_eq!((cancelled.blob_arena_id(), cancelled.start_offset()), (0, 6296));
    drop(cancelled);
    assert_eq!(blobs.create_writer(10).unwrap().start_offset(), 6296);
}

/// The pool is enumerated as .NET's `HashSet<ushort>`, files without room for an estimate leave it, a restart
/// registers the files on disk, and the sweep deletes the ones no snapshot leased.
#[test]
fn blob_files_are_picked_in_pool_order_reloaded_and_swept() {
    let directory = tempfile::tempdir().unwrap();
    let blobs = BlobManager::new(directory.path(), 64 * 1024, Arc::default()).unwrap();
    let complete = |mut writer: BlobArenaWriter| {
        writer.writer().unwrap().write_rlp(&[0xC0; 100]).unwrap();
        writer.complete().unwrap();
        writer.blob_arena_id()
    };
    let first = blobs.create_writer(1000).unwrap();
    let second = blobs.create_writer(1000).unwrap();
    assert_eq!((complete(second), complete(first)), (1, 0));
    assert_eq!(
        complete(blobs.create_writer(1000).unwrap()),
        1,
        "file 1 returned to the pool first"
    );
    assert_eq!(complete(blobs.create_writer(64 * 1024).unwrap()), 2, "no file has room");
    assert_eq!(
        complete(blobs.create_writer(1000).unwrap()),
        2,
        "files 0 and 1 left the pool"
    );

    let kept = blobs.try_lease_file(0).unwrap();
    kept.persist_on_shutdown();
    blobs.dispose();
    drop(kept);
    std::fs::write(directory.path().join("blob_0007.bin"), [1, 2, 3]).unwrap();
    std::fs::write(directory.path().join("blob_7.bin"), [4]).unwrap();
    assert_eq!(
        files(directory.path(), "blob_"),
        ["blob_0000.bin", "blob_0007.bin", "blob_7.bin"]
    );

    let reopened = BlobManager::new(directory.path(), 64 * 1024, Arc::default()).unwrap();
    reopened.initialize().unwrap();
    let leased = reopened.try_lease_file(0).unwrap();
    assert_eq!(leased.frontier(), 100);
    reopened.sweep_unreferenced();
    // Both names parse to id 7: the one registered first is replaced but, as in C#, stays on disk.
    let remaining = files(directory.path(), "blob_");
    assert!(
        remaining == ["blob_0000.bin", "blob_0007.bin"] || remaining == ["blob_0000.bin", "blob_7.bin"],
        "{remaining:?}"
    );
    assert!(reopened.try_lease_file(7).is_none());
    assert_eq!(
        (
            complete(reopened.create_writer(1000).unwrap()),
            complete(reopened.create_writer(64 * 1024).unwrap())
        ),
        (0, 8),
        "file 0 has room; new ids continue past the highest seen"
    );
    drop(leased);
}

/// `BlobFrontierReset_TruncatesFile_ForOrphanedRange`: the release of the last snapshot lease truncates the file for
/// reuse, unless it is kept for the next session.
#[rstest]
fn the_last_lease_released_resets_an_orphaned_blob_file(#[values(false, true)] preserved: bool) {
    let directory = tempfile::tempdir().unwrap();
    let counters = Arc::new(TierCounters::default());
    let blobs = BlobManager::new(directory.path(), 8 << 20, counters.clone()).unwrap();
    let mut writer = blobs.create_writer(64 * 4096).unwrap();
    for seed in 0..64 {
        writer.writer().unwrap().write_rlp(&noise(4096, seed)).unwrap();
    }
    writer.complete().unwrap();
    let id = writer.blob_arena_id();
    let snapshot_lease = blobs.try_lease_file(id).unwrap();
    drop(writer);

    let path = directory.path().join("blob_0000.bin");
    assert_eq!(std::fs::metadata(&path).unwrap().len(), 64 * 4096);
    if preserved {
        snapshot_lease.persist_on_shutdown();
    }
    blobs.release_lease(snapshot_lease);

    let expected = if preserved { 64 * 4096 } else { 0 };
    assert_eq!(blobs.try_lease_file(id).unwrap().frontier(), expected as i64);
    assert_eq!(std::fs::metadata(&path).unwrap().len(), expected);
    assert_eq!(counters.blob_allocated_bytes.load(Ordering::Relaxed), expected as i64);
    let reused = blobs.create_writer(10).unwrap();
    assert_eq!((reused.blob_arena_id(), reused.start_offset()), (id, expected as i64));
}
