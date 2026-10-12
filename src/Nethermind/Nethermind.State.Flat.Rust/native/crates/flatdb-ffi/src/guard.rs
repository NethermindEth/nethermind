//! Panic and error containment for exports.

use std::any::Any;
use std::cell::RefCell;
use std::panic::{self, AssertUnwindSafe};

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
    use super::{copy_last_error, ffi_guard};
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
    fn a_short_buffer_gets_a_prefix_and_the_full_length() {
        ffi_guard(|| Err(FfiError::invalid_argument("0123456789")));
        let mut buffer = [0u8; 4];
        assert_eq!(copy_last_error(&mut buffer), 10);
        assert_eq!(&buffer, b"0123");
    }
}
