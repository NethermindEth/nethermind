//! Port of `Nethermind.State.Flat/Io/` (`IByteReader`, `SpanByteReader`, `IByteBufferWriter`) and of the read side of
//! `PersistedSnapshots/Storage/ArenaByteReader.cs`: the byte sources the tables are read from and the sinks they are
//! written to.

use std::cell::Cell;

use super::TierResult;

/// Records the bytes a read touched, as `ArenaReservation.TouchRange` feeds the page-residency tracker.
pub trait PageTouch {
    /// `offset` is relative to the start of the bytes the reader covers.
    fn touch_range(&self, offset: usize, length: usize);
}

/// Bounded reads over the bytes of one table: `SpanByteReader` when untracked, `ArenaByteReader` when every read is
/// reported to a [`PageTouch`].
///
/// A read past the end is `None`, as `TryRead` returns `false`. Like `ArenaByteReader`, a tracked reader skips the report
/// of a read that stays within the OS page of the previous one.
pub struct ByteReader<'a> {
    data: &'a [u8],
    tracking: Option<Tracking<'a>>,
}

struct Tracking<'a> {
    touch: &'a dyn PageTouch,
    /// Where `data` starts in its file, for page alignment.
    base_offset: usize,
    page_mask: usize,
    /// The page of the previous report; `usize::MAX` before the first.
    last_page_base: Cell<usize>,
}

impl<'a> ByteReader<'a> {
    pub fn new(data: &'a [u8]) -> Self {
        Self { data, tracking: None }
    }

    /// A reader reporting its reads to `touch`; `data` starts at `base_offset` in a file of `os_page_size` pages.
    pub fn tracked(data: &'a [u8], touch: &'a dyn PageTouch, base_offset: usize, os_page_size: usize) -> Self {
        debug_assert!(os_page_size.is_power_of_two());
        Self {
            data,
            tracking: Some(Tracking {
                touch,
                base_offset,
                page_mask: os_page_size - 1,
                last_page_base: Cell::new(usize::MAX),
            }),
        }
    }

    pub fn len(&self) -> usize {
        self.data.len()
    }

    pub fn is_empty(&self) -> bool {
        self.data.is_empty()
    }

    /// `TryRead`/`PinBuffer`: the `length` bytes at `offset`, or `None` when they are not all there.
    pub fn read(&self, offset: usize, length: usize) -> Option<&'a [u8]> {
        let end = offset.checked_add(length)?;
        let bytes = self.data.get(offset..end)?;
        if let Some(tracking) = &self.tracking {
            tracking.report(offset, length);
        }
        Some(bytes)
    }

    pub fn read_u8(&self, offset: usize) -> Option<u8> {
        self.read(offset, 1).map(|bytes| bytes[0])
    }
}

impl Tracking<'_> {
    fn report(&self, offset: usize, length: usize) {
        if length == 0 {
            return;
        }
        let start = self.base_offset + offset;
        let start_page = start & !self.page_mask;
        let end_page = (start + length - 1) & !self.page_mask;
        if start_page == end_page && start_page == self.last_page_base.get() {
            return;
        }
        self.last_page_base.set(end_page);
        self.touch.touch_range(offset, length);
    }
}

/// `IByteBufferWriter`: where a table is written, an arena reservation or a buffer.
pub trait TableSink {
    /// The bytes written so far, `Written`.
    fn written(&self) -> u64;

    fn write(&mut self, bytes: &[u8]) -> TierResult<()>;
}

impl TableSink for Vec<u8> {
    fn written(&self) -> u64 {
        self.len() as u64
    }

    fn write(&mut self, bytes: &[u8]) -> TierResult<()> {
        self.extend_from_slice(bytes);
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use std::cell::RefCell;

    use super::{ByteReader, PageTouch};

    #[derive(Default)]
    struct Touches(RefCell<Vec<(usize, usize)>>);

    impl PageTouch for Touches {
        fn touch_range(&self, offset: usize, length: usize) {
            self.0.borrow_mut().push((offset, length));
        }
    }

    #[test]
    fn reads_are_bounded_and_repeats_within_a_page_are_reported_once() {
        let data = vec![7u8; 10_000];
        let touches = Touches::default();
        let reader = ByteReader::tracked(&data, &touches, 100, 4096);

        assert_eq!(reader.read(9_990, 10).map(<[u8]>::len), Some(10));
        assert_eq!(reader.read(9_995, 10), None);
        assert_eq!(reader.read(usize::MAX, 2), None);
        assert_eq!(reader.read(0, 4), Some(&data[..4]));
        assert_eq!(reader.read(10, 4), Some(&data[10..14]));
        assert_eq!(reader.read(3_990, 20), Some(&data[3_990..4_010]));
        assert_eq!(reader.read(4_000, 0), Some(&data[4_000..4_000]));

        // 10..14 shares the page of 0..4 (100..104 and 110..114 in the file); 3_990..4_010 crosses a page boundary.
        assert_eq!(*touches.0.borrow(), [(9_990, 10), (0, 4), (3_990, 20)]);
        assert_eq!(ByteReader::new(&data).read_u8(9_999), Some(7));
    }
}
