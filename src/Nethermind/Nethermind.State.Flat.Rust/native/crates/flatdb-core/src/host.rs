//! The calls the persistence manager makes into the C# host: the finality boundary and canonical roots of
//! `Nethermind.Core/IStateHeaderProvider.cs`, and `IStatePersistenceBarrier.FlushDeferred`
//! (`Nethermind.Core/IStatePersistenceBarrier.cs`).
//!
//! They run on the thread persisting state, the persistence worker or the caller of a flush, with the persistence lock
//! held, so a host callback must never call back into the manager.

use thiserror::Error;

use crate::repository::StateHeaderProvider;
use crate::types::Hash256;

/// Failure of a host callback.
#[derive(Debug, Error, Clone, PartialEq, Eq)]
pub enum HostError {
    /// The callback threw; the host reports the exception itself.
    #[error("the host callback failed with status {0}")]
    Failed(i32),
    /// The callback threw `OperationCanceledException`, which stops the persistence worker.
    #[error("the host callback was cancelled")]
    Cancelled,
}

/// The host as the persistence manager sees it.
pub trait Host: StateHeaderProvider {
    /// Port of `IStatePersistenceBarrier.FlushDeferred`: makes the deferred block data of every block up to the one
    /// about to be persisted durable. A failure aborts that persist.
    fn flush_deferred(&self) -> Result<(), HostError>;
}

/// The [`HostCallbacks::flush_deferred`] status of success.
pub const HOST_STATUS_OK: i32 = 0;
/// The [`HostCallbacks::flush_deferred`] status of a cancelled flush, `FdbStatus.Cancelled`.
pub const HOST_STATUS_CANCELLED: i32 = 3;

/// The host as C function pointers over an opaque context, `FdbHost` of the C ABI.
///
/// The callbacks must not unwind and must not call into the library.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct HostCallbacks {
    /// Passed back to every callback.
    pub ctx: usize,
    /// `FinalizedBlockNumber`.
    pub finalized_block_number: extern "C" fn(ctx: usize) -> u64,
    /// `GetFinalizedHeader(blockNumber)?.StateRoot`: writes the root and returns 1, or returns 0 when there is none.
    pub finalized_state_root: extern "C" fn(ctx: usize, block_number: u64, root: &mut Hash256) -> u8,
    /// `FlushDeferred`: [`HOST_STATUS_OK`], [`HOST_STATUS_CANCELLED`], or any other status for a failure.
    pub flush_deferred: extern "C" fn(ctx: usize) -> i32,
}

impl StateHeaderProvider for HostCallbacks {
    fn finalized_block_number(&self) -> u64 {
        (self.finalized_block_number)(self.ctx)
    }

    fn finalized_state_root(&self, block_number: u64) -> Option<Hash256> {
        let mut root = [0u8; 32];
        ((self.finalized_state_root)(self.ctx, block_number, &mut root) != 0).then_some(root)
    }
}

impl Host for HostCallbacks {
    fn flush_deferred(&self) -> Result<(), HostError> {
        match (self.flush_deferred)(self.ctx) {
            HOST_STATUS_OK => Ok(()),
            HOST_STATUS_CANCELLED => Err(HostError::Cancelled),
            status => Err(HostError::Failed(status)),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{HOST_STATUS_CANCELLED, HOST_STATUS_OK, Host, HostCallbacks, HostError};
    use crate::repository::StateHeaderProvider;
    use crate::types::Hash256;

    // The context is the finalized block; roots exist at even blocks; the flush status is the context minus 100.
    extern "C" fn finalized_block_number(ctx: usize) -> u64 {
        ctx as u64
    }

    extern "C" fn finalized_state_root(ctx: usize, block_number: u64, root: &mut Hash256) -> u8 {
        if block_number > ctx as u64 || block_number % 2 == 1 {
            return 0;
        }
        *root = [block_number as u8; 32];
        1
    }

    extern "C" fn flush_deferred(ctx: usize) -> i32 {
        ctx as i32 - 100
    }

    fn callbacks(ctx: usize) -> HostCallbacks {
        HostCallbacks {
            ctx,
            finalized_block_number,
            finalized_state_root,
            flush_deferred,
        }
    }

    #[test]
    fn callbacks_map_to_the_host_trait() {
        let host = callbacks(100 + HOST_STATUS_OK as usize);
        assert_eq!(host.finalized_block_number(), 100);
        assert_eq!(host.finalized_state_root(4), Some([4; 32]));
        assert_eq!(host.finalized_state_root(5), None, "no root at an odd block");
        assert_eq!(
            host.finalized_state_root(102),
            None,
            "no root above the finalized block"
        );
        assert_eq!(host.flush_deferred(), Ok(()));
        assert_eq!(
            callbacks(100 + HOST_STATUS_CANCELLED as usize).flush_deferred(),
            Err(HostError::Cancelled)
        );
        assert_eq!(callbacks(107).flush_deferred(), Err(HostError::Failed(7)));
    }
}
