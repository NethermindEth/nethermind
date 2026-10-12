//! Port of `Nethermind.State.Flat/Persistence/CachedReaderPersistence.cs` and `RefCountingPersistenceReader.cs`: shares
//! one reader between callers to save the cost of a RocksDB snapshot per reader. A committed batch drops the shared
//! reader, and so does a timer every five seconds, so an idle cache does not hold back compaction.

use std::sync::{Arc, Weak};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use parking_lot::{Condvar, Mutex};

use crate::kv::{ReadFlags, WriteFlags};
use crate::types::{Account, Address, Hash256, StateId, TreePath, U256};

use super::{Persistence, PersistenceReader, PersistenceResult, PersistenceWriteBatch, ReaderFlags};

/// How often the shared reader is dropped (the `PeriodicTimer` of `CachedReaderPersistence`).
pub const CLEAR_PERIOD: Duration = Duration::from_secs(5);

/// The decorator. Dropping it stops its timer.
pub struct CachedReaderPersistence<P: Persistence> {
    state: Arc<CacheState<P>>,
    timer: Option<(Arc<TimerStop>, JoinHandle<()>)>,
}

struct CacheState<P: Persistence> {
    inner: P,
    cached: Mutex<Option<Arc<P::Reader>>>,
}

#[derive(Default)]
struct TimerStop {
    stopped: Mutex<bool>,
    signal: Condvar,
}

impl<P: Persistence> CacheState<P> {
    fn clear_reader_cache(&self) {
        let cached = self.cached.lock().take();
        drop(cached);
    }
}

impl<P: Persistence> CachedReaderPersistence<P> {
    /// Wraps `inner` and primes the shared reader.
    pub fn new(inner: P) -> PersistenceResult<Self> {
        let state = Arc::new(CacheState {
            inner,
            cached: Mutex::new(None),
        });
        let stop = Arc::new(TimerStop::default());
        let timer = {
            let (state, stop): (Weak<CacheState<P>>, Arc<TimerStop>) = (Arc::downgrade(&state), stop.clone());
            thread::Builder::new()
                .name("fdb-reader-cache".to_owned())
                .spawn(move || run_timer(&state, &stop))
                .expect("spawning the reader cache timer")
        };

        let persistence = Self {
            state,
            timer: Some((stop, timer)),
        };
        drop(persistence.create_reader(ReaderFlags::NONE)?);
        Ok(persistence)
    }

    pub fn inner(&self) -> &P {
        &self.state.inner
    }

    /// Drops the shared reader, as the timer does.
    pub fn clear_reader_cache(&self) {
        self.state.clear_reader_cache();
    }
}

fn run_timer<P: Persistence>(state: &Weak<CacheState<P>>, stop: &TimerStop) {
    let mut stopped = stop.stopped.lock();
    // Checked before every wait too, as the stop can come before the thread first takes the lock.
    while !*stopped {
        let timed_out = stop.signal.wait_for(&mut stopped, CLEAR_PERIOD).timed_out();
        if timed_out && !*stopped {
            let Some(state) = state.upgrade() else { return };
            state.clear_reader_cache();
        }
    }
}

impl<P: Persistence> Drop for CachedReaderPersistence<P> {
    fn drop(&mut self) {
        if let Some((stop, timer)) = self.timer.take() {
            *stop.stopped.lock() = true;
            stop.signal.notify_all();
            let _ = timer.join();
        }
        self.state.clear_reader_cache();
    }
}

impl<P: Persistence> Persistence for CachedReaderPersistence<P> {
    type Reader = CachedReader<P::Reader>;
    type WriteBatch = ClearCacheOnCommit<P>;

    /// The shared reader is created flagless, so flagged requests go to the inner persistence directly.
    fn create_reader(&self, flags: ReaderFlags) -> PersistenceResult<CachedReader<P::Reader>> {
        if flags.intersects(ReaderFlags::SYNC | ReaderFlags::FULL_SCAN) {
            return Ok(CachedReader::Own(self.state.inner.create_reader(flags)?));
        }

        let mut cached = self.state.cached.lock();
        if let Some(reader) = &*cached {
            return Ok(CachedReader::Shared(reader.clone()));
        }

        let reader = Arc::new(self.state.inner.create_reader(ReaderFlags::NONE)?);
        *cached = Some(reader.clone());
        Ok(CachedReader::Shared(reader))
    }

    fn create_write_batch(
        &self,
        from: &StateId,
        to: &StateId,
        flags: WriteFlags,
    ) -> PersistenceResult<ClearCacheOnCommit<P>> {
        Ok(ClearCacheOnCommit {
            inner: self.state.inner.create_write_batch(from, to, flags)?,
            state: self.state.clone(),
        })
    }

    fn flush(&self) -> PersistenceResult<()> {
        self.state.inner.flush()
    }

    fn clear(&self) -> PersistenceResult<()> {
        self.state.clear_reader_cache();
        self.state.inner.clear()
    }
}

/// A reader of [`CachedReaderPersistence`]: the shared one, released with its last holder, or one of its own.
pub enum CachedReader<R> {
    Shared(Arc<R>),
    Own(R),
}

impl<R: PersistenceReader> CachedReader<R> {
    fn reader(&self) -> &R {
        match self {
            Self::Shared(reader) => reader,
            Self::Own(reader) => reader,
        }
    }
}

impl<R: PersistenceReader> PersistenceReader for CachedReader<R> {
    type Iter = R::Iter;

    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>> {
        self.reader().get_account(address)
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>> {
        self.reader().try_get_slot(address, slot)
    }

    fn current_state(&self) -> StateId {
        self.reader().current_state()
    }

    fn load_state_rlp<T>(
        &self,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        self.reader().load_state_rlp(path, flags, read)
    }

    fn load_storage_rlp<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        self.reader().load_storage_rlp(address_hash, path, flags, read)
    }

    fn get_account_raw(&self, address_hash: &Hash256) -> PersistenceResult<Option<Vec<u8>>> {
        self.reader().get_account_raw(address_hash)
    }

    fn try_get_storage_raw(&self, address_hash: &Hash256, slot_hash: &Hash256) -> PersistenceResult<Option<U256>> {
        self.reader().try_get_storage_raw(address_hash, slot_hash)
    }

    fn create_account_iterator(&self, start_key: &Hash256, end_key: &Hash256) -> PersistenceResult<R::Iter> {
        self.reader().create_account_iterator(start_key, end_key)
    }

    fn create_storage_iterator(
        &self,
        account_key: &Hash256,
        start_slot_key: &Hash256,
        end_slot_key: &Hash256,
    ) -> PersistenceResult<R::Iter> {
        self.reader()
            .create_storage_iterator(account_key, start_slot_key, end_slot_key)
    }

    fn is_preimage_mode(&self) -> bool {
        self.reader().is_preimage_mode()
    }
}

/// A batch that drops the shared reader once committed, so the next reader sees it.
pub struct ClearCacheOnCommit<P: Persistence> {
    inner: P::WriteBatch,
    state: Arc<CacheState<P>>,
}

impl<P: Persistence> PersistenceWriteBatch for ClearCacheOnCommit<P> {
    fn self_destruct(&mut self, address: &Address) -> PersistenceResult<()> {
        self.inner.self_destruct(address)
    }

    fn set_account(&mut self, address: &Address, account: Option<&Account>) -> PersistenceResult<()> {
        self.inner.set_account(address, account)
    }

    fn set_storage(&mut self, address: &Address, slot: &U256, value: Option<&U256>) -> PersistenceResult<()> {
        self.inner.set_storage(address, slot, value)
    }

    fn set_state_trie_node(&mut self, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
        self.inner.set_state_trie_node(path, rlp)
    }

    fn set_storage_trie_node(&mut self, address_hash: &Hash256, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
        self.inner.set_storage_trie_node(address_hash, path, rlp)
    }

    fn set_storage_raw_encoded(
        &mut self,
        address_hash: &Hash256,
        slot_hash: &Hash256,
        rlp_value: &[u8],
    ) -> PersistenceResult<()> {
        self.inner.set_storage_raw_encoded(address_hash, slot_hash, rlp_value)
    }

    fn set_account_raw(&mut self, address_hash: &Hash256, account: &Account) -> PersistenceResult<()> {
        self.inner.set_account_raw(address_hash, account)
    }

    fn delete_account_range(&mut self, from_path: &Hash256, to_path: &Hash256) -> PersistenceResult<()> {
        self.inner.delete_account_range(from_path, to_path)
    }

    fn delete_storage_range(
        &mut self,
        address_hash: &Hash256,
        from_path: &Hash256,
        to_path: &Hash256,
    ) -> PersistenceResult<()> {
        self.inner.delete_storage_range(address_hash, from_path, to_path)
    }

    fn delete_state_trie_node_range(&mut self, from: &Hash256, to: &Hash256) -> PersistenceResult<()> {
        self.inner.delete_state_trie_node_range(from, to)
    }

    fn delete_storage_trie_node_range(
        &mut self,
        address_hash: &Hash256,
        from: &Hash256,
        to: &Hash256,
    ) -> PersistenceResult<()> {
        self.inner.delete_storage_trie_node_range(address_hash, from, to)
    }

    fn commit(self) -> PersistenceResult<()> {
        self.inner.commit()?;
        self.state.clear_reader_cache();
        Ok(())
    }
}
