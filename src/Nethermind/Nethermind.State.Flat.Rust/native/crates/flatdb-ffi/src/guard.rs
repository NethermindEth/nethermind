//! Panic and error containment for exports.

use std::any::Any;
use std::cell::RefCell;
use std::panic::{self, AssertUnwindSafe};
use std::sync::atomic::{AtomicBool, Ordering};

use crate::abi::{FdbStatus, FfiError};

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

/// Runs the body of an export: a panic or an error becomes a status code, with its message kept for
/// `fdb_last_error` on this thread. A successful call leaves the previous message in place.
pub fn ffi_guard(body: impl FnOnce() -> Result<(), FfiError>) -> i32 {
    match panic::catch_unwind(AssertUnwindSafe(body)) {
        Ok(Ok(())) => FdbStatus::Ok as i32,
        Ok(Err(error)) => {
            set_last_error(error.message);
            error.status as i32
        }
        Err(payload) => {
            set_last_error(format!("panic: {}", panic_message(payload.as_ref())));
            FdbStatus::Panic as i32
        }
    }
}

/// The poison flag of a store, shared with every reader, iterator and batch created from it, whose count of clones also
/// tells whether any of them is still open.
#[derive(Default)]
pub struct Poison(AtomicBool);

impl Poison {
    /// Runs a call on the store's state inside [`ffi_guard`]. A panic poisons the store and goes on to `ffi_guard`, as
    /// the caches it shares may be left half-updated; every later call then fails with [`FdbStatus::Poisoned`].
    pub fn guard<T>(&self, body: impl FnOnce() -> Result<T, FfiError>) -> Result<T, FfiError> {
        if self.0.load(Ordering::Acquire) {
            return Err(FfiError {
                status: FdbStatus::Poisoned,
                message: "the flat DB store is poisoned by an earlier panic".to_owned(),
            });
        }

        panic::catch_unwind(AssertUnwindSafe(body)).unwrap_or_else(|payload| {
            self.0.store(true, Ordering::Release);
            panic::resume_unwind(payload)
        })
    }
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

    fn last_error() -> String {
        let mut buffer = [0u8; 64];
        let len = copy_last_error(&mut buffer);
        String::from_utf8(buffer[..len.min(buffer.len())].to_vec()).unwrap()
    }

    #[test]
    fn errors_and_panics_become_statuses_with_a_message() {
        assert_eq!(ffi_guard(|| Ok(())), FdbStatus::Ok as i32);

        let status = ffi_guard(|| Err(FfiError::invalid_argument("bad column")));
        assert_eq!(status, FdbStatus::InvalidArgument as i32);
        assert_eq!(last_error(), "bad column");

        assert_eq!(ffi_guard(|| Ok(())), FdbStatus::Ok as i32);
        assert_eq!(last_error(), "bad column", "success keeps the previous message");

        let status = ffi_guard(|| panic!("boom {}", 42));
        assert_eq!(status, FdbStatus::Panic as i32);
        assert_eq!(last_error(), "panic: boom 42");
    }

    #[test]
    fn a_panic_poisons_the_store_for_every_later_call() {
        let poison = Poison::default();
        assert_eq!(ffi_guard(|| poison.guard(|| Ok(()))), FdbStatus::Ok as i32);

        let status = ffi_guard(|| poison.guard(|| panic!("injected")));
        assert_eq!(
            (status, last_error()),
            (FdbStatus::Panic as i32, "panic: injected".to_owned())
        );

        let mut ran = false;
        let status = ffi_guard(|| {
            poison.guard(|| {
                ran = true;
                Ok(())
            })
        });
        assert_eq!((status, ran), (FdbStatus::Poisoned as i32, false));
    }

    #[test]
    fn a_short_buffer_gets_a_prefix_and_the_full_length() {
        ffi_guard(|| Err(FfiError::invalid_argument("0123456789")));
        let mut buffer = [0u8; 4];
        assert_eq!(copy_last_error(&mut buffer), 10);
        assert_eq!(&buffer, b"0123");
    }
}
