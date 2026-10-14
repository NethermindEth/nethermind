//! Port of `Nethermind.State.Flat/PersistedSnapshots/Storage/PosixReclaim.cs` and of the system calls of
//! `ArenaFile.cs`: the OS page size, read-only shared mappings and their `madvise` hints, `posix_fadvise` and
//! `fallocate(PUNCH_HOLE)`. The only `unsafe` code of the persisted tier.
//!
//! Like C#, every hint is fire-and-forget (its error is not inspected) and every range that may only cover whole pages
//! is aligned inward, so a reclaim never touches a page shared with a neighbouring reservation.

use std::fs::File;
use std::io;
use std::os::fd::AsRawFd;
use std::ptr::{self, NonNull};
use std::sync::OnceLock;

/// `MADV_POPULATE_READ` (Linux ≥ 5.14; older kernels reject it with `EINVAL`, which is ignored).
const MADV_POPULATE_READ: libc::c_int = 22;

/// `Environment.SystemPageSize`: the granularity of `madvise`, `posix_fadvise` and hole punching.
pub fn os_page_size() -> usize {
    static PAGE_SIZE: OnceLock<usize> = OnceLock::new();
    *PAGE_SIZE.get_or_init(|| {
        // SAFETY: sysconf has no preconditions.
        let size = unsafe { libc::sysconf(libc::_SC_PAGESIZE) };
        usize::try_from(size)
            .ok()
            .filter(|size| size.is_power_of_two())
            .expect("a power-of-two OS page size")
    })
}

/// `PageLayout.RoundUpToOsPage`.
pub fn round_up_to_os_page(value: i64) -> i64 {
    let page_size = os_page_size() as i64;
    (value + page_size - 1) & !(page_size - 1)
}

/// `AlignInward`: the whole OS pages inside `[offset, offset + size)`, as a start and a length that is not positive when
/// there are none.
fn align_inward(offset: i64, size: i64) -> (i64, i64) {
    let page_size = os_page_size() as i64;
    let start = (offset + page_size - 1) & !(page_size - 1);
    let end = (offset + size) & !(page_size - 1);
    (start, end - start)
}

/// `PosixReclaim.FadviseDontNeed`: drops the whole pages of the range from the OS file cache.
pub fn fadvise_dont_need(file: &File, offset: i64, size: i64) {
    let (start, length) = align_inward(offset, size);
    if length > 0 {
        // SAFETY: the descriptor is open for the call; the advice reads no memory.
        unsafe { libc::posix_fadvise(file.as_raw_fd(), start, length, libc::POSIX_FADV_DONTNEED) };
    }
}

/// `PosixReclaim.FadviseWillNeed`: starts read-ahead of the range, unaligned so the partial pages at its ends are read
/// too.
pub fn fadvise_will_need(file: &File, offset: i64, size: i64) {
    if size > 0 {
        // SAFETY: as above.
        unsafe { libc::posix_fadvise(file.as_raw_fd(), offset, size, libc::POSIX_FADV_WILLNEED) };
    }
}

/// `PosixReclaim.PunchHoleOutcome`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PunchHoleOutcome {
    /// The range was punched, or held no whole page.
    Done,
    /// The filesystem or kernel never supports hole punching (`EOPNOTSUPP`, `ENOSYS`).
    Unsupported,
    /// Any other error, which a later call may not hit.
    Failed,
}

/// `PosixReclaim.TryPunchHole`: frees the disk blocks of the whole pages of the range, keeping the file length.
/// Punched pages read back as zeros.
pub fn punch_hole(file: &File, offset: i64, size: i64) -> PunchHoleOutcome {
    let (start, length) = align_inward(offset, size);
    if length <= 0 {
        return PunchHoleOutcome::Done;
    }
    // SAFETY: the descriptor is open for the call; fallocate reads no memory.
    let result = unsafe {
        libc::fallocate(
            file.as_raw_fd(),
            libc::FALLOC_FL_PUNCH_HOLE | libc::FALLOC_FL_KEEP_SIZE,
            start,
            length,
        )
    };
    if result == 0 {
        return PunchHoleOutcome::Done;
    }
    match io::Error::last_os_error().raw_os_error() {
        Some(libc::EOPNOTSUPP | libc::ENOSYS) => PunchHoleOutcome::Unsupported,
        _ => PunchHoleOutcome::Failed,
    }
}

/// A `madvise` hint.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Advice {
    Normal,
    Random,
    DontNeed,
    PopulateRead,
}

impl Advice {
    fn value(self) -> libc::c_int {
        match self {
            Self::Normal => libc::MADV_NORMAL,
            Self::Random => libc::MADV_RANDOM,
            Self::DontNeed => libc::MADV_DONTNEED,
            Self::PopulateRead => MADV_POPULATE_READ,
        }
    }
}

/// A read-only shared mapping of `[offset, offset + length)` of a file, unmapped on drop.
///
/// The bytes are those of the page cache, so a slice of them is only sound while nothing writes that range of the
/// file: the tier slices only published reservations, which are never written again (writers append past the
/// frontier, and a hole is only punched into a dead reservation). A page past the end of the file faults (`SIGBUS`), so
/// callers only slice bytes below the file length.
pub struct Mapping {
    /// The page-aligned start of the mapping.
    base: NonNull<u8>,
    mapped_length: usize,
    /// Where the requested offset lies past `base`.
    skew: usize,
    length: usize,
}

// SAFETY: the mapping is plain read-only memory, owned by this value until it is dropped.
unsafe impl Send for Mapping {}
// SAFETY: as above; every access is a read.
unsafe impl Sync for Mapping {}

impl Mapping {
    /// Maps `length` bytes of `file` from `offset`, which need not be page-aligned.
    pub fn map(file: &File, offset: u64, length: usize) -> io::Result<Self> {
        let page_size = os_page_size() as u64;
        let skew = (offset & (page_size - 1)) as usize;
        let mapped_length = skew + length;
        let aligned_offset = libc::off_t::try_from(offset - skew as u64)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "mapping offset out of range"))?;
        // SAFETY: a fresh read-only shared mapping of an open descriptor; the kernel picks the address.
        let address = unsafe {
            libc::mmap(
                ptr::null_mut(),
                mapped_length,
                libc::PROT_READ,
                libc::MAP_SHARED,
                file.as_raw_fd(),
                aligned_offset,
            )
        };
        if address == libc::MAP_FAILED {
            return Err(io::Error::last_os_error());
        }
        Ok(Self {
            base: NonNull::new(address.cast()).expect("mmap never maps address 0"),
            mapped_length,
            skew,
            length,
        })
    }

    pub fn len(&self) -> usize {
        self.length
    }

    pub fn is_empty(&self) -> bool {
        self.length == 0
    }

    /// The `length` bytes at `offset`.
    ///
    /// # Panics
    /// When the range leaves the mapping.
    pub fn slice(&self, offset: usize, length: usize) -> &[u8] {
        assert!(
            offset.checked_add(length).is_some_and(|end| end <= self.length),
            "[{offset}, +{length}) leaves a {}-byte mapping",
            self.length
        );
        // SAFETY: the range lies inside the live mapping, and the type docs restrict slicing to bytes nothing writes.
        unsafe { std::slice::from_raw_parts(self.data().add(offset), length) }
    }

    /// `Volatile.Read` of the byte at `offset`: faults its page in and refreshes its LRU position.
    ///
    /// # Panics
    /// When `offset` leaves the mapping.
    pub fn touch_byte(&self, offset: usize) -> u8 {
        assert!(offset < self.length, "byte {offset} of a {}-byte mapping", self.length);
        // SAFETY: the byte lies inside the live mapping.
        unsafe { ptr::read_volatile(self.data().add(offset)) }
    }

    /// `madvise` over the whole pages of `[offset, offset + length)`, offsets relative to the mapped range.
    pub fn advise_inward(&self, offset: usize, length: usize, advice: Advice) {
        let page_size = os_page_size();
        let start = (self.skew + offset).next_multiple_of(page_size);
        let end = ((self.skew + offset + length) & !(page_size - 1)).min(self.mapped_length);
        if end > start {
            // SAFETY: the page-aligned range lies inside the live mapping; a hint changes no mapped content.
            unsafe { libc::madvise(self.base.as_ptr().add(start).cast(), end - start, advice.value()) };
        }
    }

    /// `madvise` from the first mapped byte over the whole mapped range, as C# passes the view pointer: the kernel
    /// rejects the hint when that byte is not page-aligned.
    pub fn advise_all(&self, advice: Advice) {
        // SAFETY: the range is the live mapping; a hint changes no mapped content.
        unsafe { libc::madvise(self.data().cast(), self.length, advice.value()) };
    }

    fn data(&self) -> *mut u8 {
        // SAFETY: `skew` is inside the mapping.
        unsafe { self.base.as_ptr().add(self.skew) }
    }
}

impl Drop for Mapping {
    fn drop(&mut self) {
        // SAFETY: the mapping is live, and no slice of it outlives `self`.
        unsafe { libc::munmap(self.base.as_ptr().cast(), self.mapped_length) };
    }
}

#[cfg(test)]
mod tests {
    use std::io::Write;

    use super::{Advice, Mapping, PunchHoleOutcome, align_inward, os_page_size, punch_hole, round_up_to_os_page};

    #[test]
    fn ranges_align_inward_and_up_to_whole_pages() {
        let page = os_page_size() as i64;
        assert_eq!(align_inward(0, 3 * page), (0, 3 * page));
        assert_eq!(align_inward(page / 2, 3 * page), (page, 2 * page));
        assert!(align_inward(1, page).1 <= 0, "no whole page");
        assert_eq!(
            (
                round_up_to_os_page(0),
                round_up_to_os_page(1),
                round_up_to_os_page(page)
            ),
            (0, page, page)
        );
    }

    #[test]
    fn mappings_read_from_unaligned_offsets_and_punched_pages_read_zero() {
        let page = os_page_size();
        let mut file = tempfile::tempfile().unwrap();
        let data: Vec<u8> = (0..4 * page).map(|index| (index % 251) as u8 + 1).collect();
        file.write_all(&data).unwrap();

        let mapping = Mapping::map(&file, page as u64 + 3, 2 * page).unwrap();
        assert_eq!(mapping.len(), 2 * page);
        assert_eq!(mapping.slice(0, 5), &data[page + 3..page + 8]);
        assert_eq!(mapping.touch_byte(2 * page - 1), data[3 * page + 2]);
        mapping.advise_all(Advice::Normal);
        mapping.advise_inward(0, 2 * page, Advice::DontNeed);
        assert_eq!(
            mapping.slice(page, 4),
            &data[2 * page + 3..2 * page + 7],
            "DONTNEED keeps file pages"
        );

        match punch_hole(&file, page as i64, page as i64) {
            PunchHoleOutcome::Done => assert!(mapping.slice(0, page - 3).iter().all(|&byte| byte == 0)),
            outcome => eprintln!("skipped the punch: the filesystem reports {outcome:?}"),
        }
        assert_eq!(
            mapping.slice(page - 3, 1),
            &data[2 * page..2 * page + 1],
            "the next page is kept"
        );
    }
}
