//! Table of the RocksDB C API functions the store calls.
//!
//! The crate does not link RocksDB. The C# side resolves every symbol of [`ROCKS_API_SYMBOLS`] from the single
//! `librocksdb` the `Nethermind.RocksDbBindings` package loaded, and hands the table over, so Rust drives the very
//! instance C# opened and configured. Signatures follow `rocksdb/c.h` as exposed by those bindings.

use std::ffi::{c_char, c_int, c_void};

use super::KvError;
use crate::ABI_VERSION;

macro_rules! opaque_types {
    ($($(#[$doc:meta])* $name:ident;)*) => {
        $(
            $(#[$doc])*
            #[repr(C)]
            pub struct $name {
                _private: [u8; 0],
            }
        )*
    };
}

opaque_types! {
    /// `rocksdb_t`
    RawDb;
    /// `rocksdb_column_family_handle_t`
    RawColumnFamily;
    /// `rocksdb_readoptions_t`
    RawReadOptions;
    /// `rocksdb_writeoptions_t`
    RawWriteOptions;
    /// `rocksdb_flushoptions_t`
    RawFlushOptions;
    /// `rocksdb_snapshot_t`
    RawSnapshot;
    /// `rocksdb_iterator_t`
    RawIterator;
    /// `rocksdb_writebatch_t`
    RawWriteBatch;
    /// `rocksdb_pinnableslice_t`
    RawPinnableSlice;
}

/// Out-parameter RocksDB stores an error message in; the message is freed with `rocksdb_free`.
pub type ErrPtr = *mut *mut c_char;

macro_rules! rocks_api {
    ($($name:ident = $symbol:literal fn($($arg:ident: $ty:ty),* $(,)?) $(-> $ret:ty)?;)*) => {
        /// The RocksDB C API functions, filled by the C# adapter in [`ROCKS_API_SYMBOLS`] order.
        ///
        /// Mirrored on the C# side as a header of two `u32` followed by one pointer per symbol.
        #[repr(C)]
        pub struct RocksApi {
            /// Must equal [`ABI_VERSION`].
            pub abi_version: u32,
            /// Must equal the length of [`ROCKS_API_SYMBOLS`].
            pub fn_count: u32,
            $(
                #[doc = concat!("`", $symbol, "`")]
                pub $name: Option<unsafe extern "C" fn($($arg: $ty),*) $(-> $ret)?>,
            )*
        }

        /// A validated [`RocksApi`]: every function is present.
        #[derive(Clone, Copy)]
        pub struct RocksFns {
            $(
                #[doc = concat!("`", $symbol, "`")]
                pub $name: unsafe extern "C" fn($($arg: $ty),*) $(-> $ret)?,
            )*
        }

        /// The exported symbol of every [`RocksApi`] entry, in table order.
        pub const ROCKS_API_SYMBOLS: &[&str] = &[$($symbol),*];

        impl RocksApi {
            /// Checks the header and that every function is present.
            pub fn validate(&self) -> Result<RocksFns, KvError> {
                if self.abi_version != ABI_VERSION || self.fn_count as usize != ROCKS_API_SYMBOLS.len() {
                    return Err(KvError::InvalidArgument(format!(
                        "RocksApi header is version {} with {} functions, expected version {} with {}",
                        self.abi_version, self.fn_count, ABI_VERSION, ROCKS_API_SYMBOLS.len()
                    )));
                }

                Ok(RocksFns {
                    $(
                        $name: self.$name.ok_or_else(|| {
                            KvError::InvalidArgument(concat!("RocksApi is missing ", $symbol).to_owned())
                        })?,
                    )*
                })
            }

            /// Builds the table by resolving every symbol with `resolve`, which returns null for a missing one.
            ///
            /// # Safety
            /// A non-null address `resolve` returns must be the RocksDB C API function the symbol names.
            pub unsafe fn from_resolver(mut resolve: impl FnMut(&str) -> *const c_void) -> Self {
                Self {
                    abi_version: ABI_VERSION,
                    fn_count: ROCKS_API_SYMBOLS.len() as u32,
                    $(
                        // SAFETY: a nullable function pointer has the layout of a data pointer, and the caller
                        // guarantees the address is this function.
                        $name: unsafe {
                            std::mem::transmute::<*const c_void, Option<unsafe extern "C" fn($($ty),*) $(-> $ret)?>>(
                                resolve($symbol),
                            )
                        },
                    )*
                }
            }
        }
    };
}

rocks_api! {
    get_into_buffer_cf = "rocksdb_get_into_buffer_cf" fn(
        db: *mut RawDb, options: *const RawReadOptions, column_family: *mut RawColumnFamily,
        key: *const c_char, key_len: usize, buffer: *mut c_char, buffer_size: usize,
        value_len: *mut usize, found: *mut u8, errptr: ErrPtr,
    ) -> u8;
    get_pinned_cf = "rocksdb_get_pinned_cf" fn(
        db: *mut RawDb, options: *const RawReadOptions, column_family: *mut RawColumnFamily,
        key: *const c_char, key_len: usize, errptr: ErrPtr,
    ) -> *mut RawPinnableSlice;
    pinnableslice_value = "rocksdb_pinnableslice_value" fn(
        slice: *const RawPinnableSlice, value_len: *mut usize,
    ) -> *const c_char;
    pinnableslice_destroy = "rocksdb_pinnableslice_destroy" fn(slice: *mut RawPinnableSlice);
    get_cf = "rocksdb_get_cf" fn(
        db: *mut RawDb, options: *const RawReadOptions, column_family: *mut RawColumnFamily,
        key: *const c_char, key_len: usize, value_len: *mut usize, errptr: ErrPtr,
    ) -> *mut c_char;
    batched_multi_get_cf = "rocksdb_batched_multi_get_cf" fn(
        db: *mut RawDb, options: *const RawReadOptions, column_family: *mut RawColumnFamily,
        num_keys: usize, keys: *const *const c_char, key_lens: *const usize,
        values: *mut *mut RawPinnableSlice, errs: *mut *mut c_char, sorted_input: bool,
    );
    free = "rocksdb_free" fn(ptr: *mut c_void);

    writebatch_create = "rocksdb_writebatch_create" fn() -> *mut RawWriteBatch;
    writebatch_destroy = "rocksdb_writebatch_destroy" fn(batch: *mut RawWriteBatch);
    writebatch_put_cf = "rocksdb_writebatch_put_cf" fn(
        batch: *mut RawWriteBatch, column_family: *mut RawColumnFamily,
        key: *const c_char, key_len: usize, value: *const c_char, value_len: usize,
    );
    writebatch_delete_cf = "rocksdb_writebatch_delete_cf" fn(
        batch: *mut RawWriteBatch, column_family: *mut RawColumnFamily, key: *const c_char, key_len: usize,
    );
    writebatch_delete_range_cf = "rocksdb_writebatch_delete_range_cf" fn(
        batch: *mut RawWriteBatch, column_family: *mut RawColumnFamily,
        start_key: *const c_char, start_key_len: usize, end_key: *const c_char, end_key_len: usize,
    );
    writebatch_clear = "rocksdb_writebatch_clear" fn(batch: *mut RawWriteBatch);
    writebatch_count = "rocksdb_writebatch_count" fn(batch: *mut RawWriteBatch) -> c_int;
    write = "rocksdb_write" fn(
        db: *mut RawDb, options: *const RawWriteOptions, batch: *mut RawWriteBatch, errptr: ErrPtr,
    );

    writeoptions_create = "rocksdb_writeoptions_create" fn() -> *mut RawWriteOptions;
    writeoptions_destroy = "rocksdb_writeoptions_destroy" fn(options: *mut RawWriteOptions);
    writeoptions_set_sync = "rocksdb_writeoptions_set_sync" fn(options: *mut RawWriteOptions, value: u8);
    writeoptions_disable_wal = "rocksdb_writeoptions_disable_WAL" fn(options: *mut RawWriteOptions, value: c_int);
    writeoptions_set_low_pri = "rocksdb_writeoptions_set_low_pri" fn(options: *mut RawWriteOptions, value: u8);

    readoptions_create = "rocksdb_readoptions_create" fn() -> *mut RawReadOptions;
    readoptions_destroy = "rocksdb_readoptions_destroy" fn(options: *mut RawReadOptions);
    readoptions_set_snapshot = "rocksdb_readoptions_set_snapshot" fn(
        options: *mut RawReadOptions, snapshot: *const RawSnapshot,
    );
    readoptions_set_fill_cache = "rocksdb_readoptions_set_fill_cache" fn(options: *mut RawReadOptions, value: u8);
    readoptions_set_verify_checksums = "rocksdb_readoptions_set_verify_checksums" fn(
        options: *mut RawReadOptions, value: u8,
    );
    readoptions_set_readahead_size = "rocksdb_readoptions_set_readahead_size" fn(
        options: *mut RawReadOptions, value: usize,
    );
    readoptions_set_iterate_lower_bound = "rocksdb_readoptions_set_iterate_lower_bound" fn(
        options: *mut RawReadOptions, key: *const c_char, key_len: usize,
    );
    readoptions_set_iterate_upper_bound = "rocksdb_readoptions_set_iterate_upper_bound" fn(
        options: *mut RawReadOptions, key: *const c_char, key_len: usize,
    );
    readoptions_set_total_order_seek = "rocksdb_readoptions_set_total_order_seek" fn(
        options: *mut RawReadOptions, value: u8,
    );
    readoptions_set_tailing = "rocksdb_readoptions_set_tailing" fn(options: *mut RawReadOptions, value: u8);

    create_snapshot = "rocksdb_create_snapshot" fn(db: *mut RawDb) -> *const RawSnapshot;
    release_snapshot = "rocksdb_release_snapshot" fn(db: *mut RawDb, snapshot: *const RawSnapshot);

    create_iterator_cf = "rocksdb_create_iterator_cf" fn(
        db: *mut RawDb, options: *const RawReadOptions, column_family: *mut RawColumnFamily,
    ) -> *mut RawIterator;
    iter_seek = "rocksdb_iter_seek" fn(iterator: *mut RawIterator, key: *const c_char, key_len: usize);
    iter_seek_for_prev = "rocksdb_iter_seek_for_prev" fn(
        iterator: *mut RawIterator, key: *const c_char, key_len: usize,
    );
    iter_seek_to_first = "rocksdb_iter_seek_to_first" fn(iterator: *mut RawIterator);
    iter_seek_to_last = "rocksdb_iter_seek_to_last" fn(iterator: *mut RawIterator);
    iter_valid = "rocksdb_iter_valid" fn(iterator: *const RawIterator) -> u8;
    iter_next = "rocksdb_iter_next" fn(iterator: *mut RawIterator);
    iter_prev = "rocksdb_iter_prev" fn(iterator: *mut RawIterator);
    iter_key = "rocksdb_iter_key" fn(iterator: *const RawIterator, key_len: *mut usize) -> *const c_char;
    iter_value = "rocksdb_iter_value" fn(iterator: *const RawIterator, value_len: *mut usize) -> *const c_char;
    iter_get_error = "rocksdb_iter_get_error" fn(iterator: *const RawIterator, errptr: ErrPtr);
    iter_destroy = "rocksdb_iter_destroy" fn(iterator: *mut RawIterator);

    flush_wal = "rocksdb_flush_wal" fn(db: *mut RawDb, sync: u8, errptr: ErrPtr);
    flush = "rocksdb_flush" fn(db: *mut RawDb, options: *const RawFlushOptions, errptr: ErrPtr);
    flush_cf = "rocksdb_flush_cf" fn(
        db: *mut RawDb, options: *const RawFlushOptions, column_family: *mut RawColumnFamily, errptr: ErrPtr,
    );
    flushoptions_create = "rocksdb_flushoptions_create" fn() -> *mut RawFlushOptions;
    flushoptions_destroy = "rocksdb_flushoptions_destroy" fn(options: *mut RawFlushOptions);
    flushoptions_set_wait = "rocksdb_flushoptions_set_wait" fn(options: *mut RawFlushOptions, value: u8);

    set_options_cf = "rocksdb_set_options_cf" fn(
        db: *mut RawDb, column_family: *mut RawColumnFamily, count: c_int,
        keys: *const *const c_char, values: *const *const c_char, errptr: ErrPtr,
    );
    property_value_cf = "rocksdb_property_value_cf" fn(
        db: *mut RawDb, column_family: *mut RawColumnFamily, name: *const c_char,
    ) -> *mut c_char;
}

#[cfg(test)]
mod tests {
    use super::{ROCKS_API_SYMBOLS, RocksApi};
    use crate::ABI_VERSION;
    use std::ffi::c_void;

    extern "C" fn placeholder() {}

    #[test]
    fn table_is_a_header_and_one_pointer_per_symbol() {
        assert_eq!(
            size_of::<RocksApi>(),
            8 + ROCKS_API_SYMBOLS.len() * size_of::<*const c_void>()
        );
        assert!(ROCKS_API_SYMBOLS.iter().all(|symbol| symbol.starts_with("rocksdb_")));
    }

    #[test]
    fn validate_reports_the_header_and_the_first_missing_symbol() {
        // SAFETY: no entry is ever called.
        let complete = unsafe { RocksApi::from_resolver(|_| placeholder as *const c_void) };
        assert!(complete.validate().is_ok());

        // SAFETY: as above.
        let missing = unsafe {
            RocksApi::from_resolver(|symbol| {
                if symbol == "rocksdb_flush_cf" {
                    std::ptr::null()
                } else {
                    placeholder as *const c_void
                }
            })
        };
        let error = missing.validate().err().expect("a missing symbol must fail validation");
        assert!(error.to_string().contains("rocksdb_flush_cf"), "{error}");

        // SAFETY: as above.
        let mut wrong_version = unsafe { RocksApi::from_resolver(|_| placeholder as *const c_void) };
        wrong_version.abi_version = ABI_VERSION + 1;
        assert!(wrong_version.validate().is_err());
    }
}
