//! Port of the parallel loops of the persisted tier: `ParallelUnbalancedWork.For` (conversions), `Parallel.For` (the
//! loader) and `Parallel.ForEach` (the compactor's buckets and the bloom rebuild).

use std::panic;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};

use crate::log_ring;

/// Runs `work` on every item with up to `threads` threads, the calling one included, each taking the next unclaimed
/// item, so items of uneven cost balance out. The helper threads log into the sink current on the calling thread.
///
/// As the C# loops, no new item starts once one has failed or `cancel` is set; the first error is returned after every
/// started item finished, and the first panic is resumed then. A cancelled loop returns `Ok`: the caller tells it apart
/// by the flag, as after any other cancellable step.
pub fn for_each_unbalanced<T: Sync, E: Send>(
    items: &[T],
    threads: usize,
    cancel: &AtomicBool,
    work: impl Fn(&T) -> Result<(), E> + Sync,
) -> Result<(), E> {
    let next = AtomicUsize::new(0);
    let stop = AtomicBool::new(false);
    let first_error = Mutex::new(None);
    let run = || {
        while !stop.load(Ordering::Relaxed) && !cancel.load(Ordering::Relaxed) {
            let Some(item) = items.get(next.fetch_add(1, Ordering::Relaxed)) else {
                return;
            };
            if let Err(error) = work(item) {
                stop.store(true, Ordering::Relaxed);
                first_error
                    .lock()
                    .unwrap_or_else(|poison| poison.into_inner())
                    .get_or_insert(error);
            }
        }
    };

    let guarded = || {
        panic::catch_unwind(panic::AssertUnwindSafe(run))
            .inspect_err(|_| stop.store(true, Ordering::Relaxed))
            .err()
    };

    let helpers = threads.min(items.len()).saturating_sub(1);
    let log_sink = log_ring::current();
    let panicked = std::thread::scope(|scope| {
        let handles: Vec<_> = (0..helpers)
            .map(|_| {
                scope.spawn(|| {
                    log_ring::enter(log_sink);
                    guarded()
                })
            })
            .collect();
        let mine = guarded();
        handles
            .into_iter()
            .map(|handle| handle.join().expect("panics are caught inside the thread"))
            .fold(mine, Option::or)
    });
    if let Some(payload) = panicked {
        panic::resume_unwind(payload);
    }
    first_error
        .into_inner()
        .unwrap_or_else(|poison| poison.into_inner())
        .map_or(Ok(()), Err)
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
    use std::sync::{Barrier, Mutex};

    use log::LevelFilter;
    use rstest::rstest;

    use super::for_each_unbalanced;
    use crate::log_ring::{self, LogSink};

    #[rstest]
    fn every_item_runs_once_on_up_to_the_requested_threads(#[values(0, 1, 4, 64)] threads: usize) {
        let items: Vec<usize> = (0..100).collect();
        let seen = Mutex::new(Vec::new());
        let thread_ids = Mutex::new(std::collections::HashSet::new());
        let result: Result<(), ()> = for_each_unbalanced(&items, threads, &AtomicBool::new(false), |item| {
            seen.lock().unwrap().push(*item);
            thread_ids.lock().unwrap().insert(std::thread::current().id());
            Ok(())
        });

        assert_eq!(result, Ok(()));
        let mut seen = seen.into_inner().unwrap();
        seen.sort_unstable();
        assert_eq!(seen, items);
        assert!(thread_ids.into_inner().unwrap().len() <= threads.max(1));
    }

    #[test]
    fn every_thread_logs_into_the_callers_sink() {
        let threads = 4;
        let items: Vec<usize> = (0..threads).collect();
        // Each thread waits here with its item, so every thread runs exactly one.
        let all_started = Barrier::new(threads);
        let sinks = Mutex::new(Vec::new());
        let sink = LogSink::new(LevelFilter::Off);

        let result: Result<(), ()> = sink.scope(|| {
            for_each_unbalanced(&items, threads, &AtomicBool::new(false), |_| {
                all_started.wait();
                sinks.lock().unwrap().push(log_ring::current());
                Ok(())
            })
        });

        assert_eq!(result, Ok(()));
        assert_eq!(sinks.into_inner().unwrap(), vec![Some(sink.id()); threads]);
    }

    #[test]
    fn an_error_or_the_cancel_flag_stops_new_items() {
        let items: Vec<usize> = (0..1000).collect();
        let started = AtomicUsize::new(0);
        let result = for_each_unbalanced(&items, 1, &AtomicBool::new(false), |item| {
            started.fetch_add(1, Ordering::Relaxed);
            if *item == 10 {
                Err(format!("item {item}"))
            } else {
                Ok(())
            }
        });
        assert_eq!(
            (result, started.load(Ordering::Relaxed)),
            (Err("item 10".to_owned()), 11)
        );

        let cancel = AtomicBool::new(false);
        let started = AtomicUsize::new(0);
        let result: Result<(), ()> = for_each_unbalanced(&items, 1, &cancel, |item| {
            started.fetch_add(1, Ordering::Relaxed);
            cancel.store(*item == 5, Ordering::Relaxed);
            Ok(())
        });
        assert_eq!((result, started.load(Ordering::Relaxed)), (Ok(()), 6));
    }

    #[test]
    fn a_panic_is_resumed_after_every_thread_joined() {
        let items: Vec<usize> = (0..64).collect();
        let finished = AtomicUsize::new(0);
        let outcome = std::panic::catch_unwind(|| {
            let _: Result<(), ()> = for_each_unbalanced(&items, 4, &AtomicBool::new(false), |item| {
                assert_ne!(*item, 3, "boom");
                finished.fetch_add(1, Ordering::Relaxed);
                Ok(())
            });
        });
        let message = outcome.unwrap_err().downcast::<String>().unwrap();
        assert!(message.contains("boom"), "{message}");
        assert!(finished.load(Ordering::Relaxed) < items.len());
    }
}
