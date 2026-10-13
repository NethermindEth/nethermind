//! Panic and error containment for exports, and the per-store state every guarded call shares.

use std::any::Any;
use std::cell::RefCell;
use std::panic::{self, AssertUnwindSafe};
#[cfg(any(test, feature = "test-api"))]
use std::sync::Arc;
use std::sync::Once;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::Instant;

use flatdb_core::log_ring::{self, LogSink};
use flatdb_core::seam;

use crate::abi::{FdbStatus, FfiError};
use crate::calls::Export;
#[cfg(any(test, feature = "test-api"))]
use crate::faults::{Fault, Faults};

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

/// Runs the body of `export`: a panic or an error becomes a status code, with its message kept for `fdb_last_error` on
/// this thread. A successful call leaves the previous message in place. The call is counted in the seam counters, with
/// its time when [`seam::timing_enabled`].
pub fn ffi_guard(export: Export, body: impl FnOnce() -> Result<(), FfiError>) -> i32 {
    let started = seam::timing_enabled().then(Instant::now);
    let status = match panic::catch_unwind(AssertUnwindSafe(body)) {
        Ok(Ok(())) => FdbStatus::Ok as i32,
        Ok(Err(error)) => {
            set_last_error(error.message);
            error.status as i32
        }
        Err(payload) => {
            set_last_error(format!("panic: {}", panic_message(payload.as_ref())));
            FdbStatus::Panic as i32
        }
    };
    seam::record(export.counter(), started);
    status
}

/// The state of a store every reader, iterator and batch created from it shares: the poison flag and the store's log
/// sink. The count of its clones also tells whether any of them is still open.
#[derive(Default)]
pub struct Poison {
    poisoned: AtomicBool,
    /// Where the calls on the store, and the workers its manager starts, log.
    pub log: LogSink,
    /// The faults tests arm on the store.
    #[cfg(any(test, feature = "test-api"))]
    pub faults: Arc<Faults>,
}

impl Poison {
    pub const MESSAGE: &str = "the flat DB store is poisoned by an earlier panic";

    pub fn new(log: LogSink) -> Self {
        route_panics();
        Self {
            poisoned: AtomicBool::new(false),
            log,
            #[cfg(any(test, feature = "test-api"))]
            faults: Arc::default(),
        }
    }

    /// Runs a call on the store's state inside [`ffi_guard`], logging into the store's sink. A panic poisons the store
    /// and goes on to `ffi_guard`, as the caches it shares may be left half-updated; every later call then fails with
    /// [`FdbStatus::Poisoned`].
    pub fn guard<T>(&self, body: impl FnOnce() -> Result<T, FfiError>) -> Result<T, FfiError> {
        if self.is_set() {
            return Err(FfiError {
                status: FdbStatus::Poisoned,
                message: Self::MESSAGE.to_owned(),
            });
        }

        self.log.scope(|| {
            panic::catch_unwind(AssertUnwindSafe(|| {
                #[cfg(any(test, feature = "test-api"))]
                self.faults.panic_if_armed(Fault::PanicInExport);
                body()
            }))
            .unwrap_or_else(|payload| {
                self.set();
                panic::resume_unwind(payload)
            })
        })
    }

    pub fn set(&self) {
        self.poisoned.store(true, Ordering::Release);
    }

    pub fn is_set(&self) -> bool {
        self.poisoned.load(Ordering::Acquire)
    }
}

/// The `log` target of the panic messages [`route_panics`] logs.
const PANIC_TARGET: &str = "flatdb_ffi::panic";

/// Makes the panic hook log a panic on a thread that logs into a store's sink, which every guarded call on a store and
/// every worker of its manager does, into that sink at debug level, where C# logs it, rather than write it to the
/// process's stderr; a panic anywhere else goes to the hook installed before. The panic itself still becomes the status
/// of the call or the error line of the job.
fn route_panics() {
    static ROUTE: Once = Once::new();
    ROUTE.call_once(|| {
        let previous = panic::take_hook();
        panic::set_hook(Box::new(move |info| {
            if log_ring::current().is_none() {
                return previous(info);
            }
            let thread = thread::current();
            log::debug!(target: PANIC_TARGET, "thread '{}' {info}", thread.name().unwrap_or("<unnamed>"));
        }));
    });
}

fn set_last_error(message: String) {
    LAST_ERROR.with(|last| *last.borrow_mut() = message);
}

/// Copies the last error message of this thread into `buffer` and returns its full length.
pub fn copy_last_error(buffer: &mut [u8]) -> usize {
    LAST_ERROR.with(|last| {
        let message = last.borrow();
        let copied = message.len().min(buffer.len());
        buffer[..copied].copy_from_slice(&message.as_bytes()[..copied]);
        message.len()
    })
}

fn panic_message(payload: &(dyn Any + Send)) -> &str {
    if let Some(message) = payload.downcast_ref::<&str>() {
        message
    } else if let Some(message) = payload.downcast_ref::<String>() {
        message
    } else {
        "unknown panic payload"
    }
}

#[cfg(test)]
mod tests {
    use super::{Poison, copy_last_error, ffi_guard};
    use crate::abi::{FdbStatus, FfiError};
    use crate::calls::Export;

    fn last_error() -> String {
        let mut buffer = [0u8; 64];
        let len = copy_last_error(&mut buffer);
        String::from_utf8(buffer[..len.min(buffer.len())].to_vec()).unwrap()
    }

    #[test]
    fn errors_and_panics_become_statuses_with_a_message() {
        assert_eq!(ffi_guard(Export::Other, || Ok(())), FdbStatus::Ok as i32);

        let status = ffi_guard(Export::Other, || Err(FfiError::invalid_argument("bad column")));
        assert_eq!(status, FdbStatus::InvalidArgument as i32);
        assert_eq!(last_error(), "bad column");

        assert_eq!(ffi_guard(Export::Other, || Ok(())), FdbStatus::Ok as i32);
        assert_eq!(last_error(), "bad column", "success keeps the previous message");

        let status = ffi_guard(Export::Other, || panic!("boom {}", 42));
        assert_eq!(status, FdbStatus::Panic as i32);
        assert_eq!(last_error(), "panic: boom 42");
    }

    #[test]
    fn a_panic_poisons_the_store_for_every_later_call() {
        let poison = Poison::default();
        assert_eq!(
            ffi_guard(Export::Other, || poison.guard(|| Ok(()))),
            FdbStatus::Ok as i32
        );

        let status = ffi_guard(Export::Other, || poison.guard(|| panic!("injected")));
        assert_eq!(
            (status, last_error()),
            (FdbStatus::Panic as i32, "panic: injected".to_owned())
        );

        let mut ran = false;
        let status = ffi_guard(Export::Other, || {
            poison.guard(|| {
                ran = true;
                Ok(())
            })
        });
        assert_eq!((status, ran), (FdbStatus::Poisoned as i32, false));
    }

    #[test]
    fn a_short_buffer_gets_a_prefix_and_the_full_length() {
        ffi_guard(Export::Other, || Err(FfiError::invalid_argument("0123456789")));
        let mut buffer = [0u8; 4];
        assert_eq!(copy_last_error(&mut buffer), 10);
        assert_eq!(&buffer, b"0123");
    }
}
