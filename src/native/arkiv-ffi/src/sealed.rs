//! Which branch an execution sealed, so a block's state can be read before it commits.
//!
//! The executor seals a block's branch inside `Evm::finish` and parks it in `BlockSeals`, which keeps the branch to
//! itself. A sealed branch stays readable (`Store::seal`), so a block held uncommitted can still report the accounts
//! and entities it wrote: this store forwards every call and remembers, per thread, the last branch sealed through
//! it. Execution runs on the calling thread, so after a block executes that is its branch.

use std::cell::Cell as ThreadCell;

use arkiv_interfaces::store::*;
use arkiv_reth_statemanager::HostStore;

thread_local! {
    static LAST_SEALED: ThreadCell<Option<(BranchId, [u8; 32])>> = const { ThreadCell::new(None) };
}

/// Forget the branch this thread last sealed, before an execution whose seal is wanted.
pub fn clear_last_sealed() {
    LAST_SEALED.set(None);
}

/// The branch this thread last sealed, and its state root.
pub fn last_sealed() -> Option<(BranchId, [u8; 32])> {
    LAST_SEALED.get()
}

/// A store that records each seal on the sealing thread and is otherwise the store it wraps.
#[derive(Debug)]
pub struct SealWatch(pub HostStore);

impl Store for SealWatch {
    fn pricing_snapshot(&self, at: CommitId) -> Result<PricingSnapshot, StoreError> {
        self.0.pricing_snapshot(at)
    }

    fn head(&self) -> CommitId {
        self.0.head()
    }

    fn begin(&self, at: Option<CommitId>) -> Result<BranchId, StoreError> {
        self.0.begin(at)
    }

    fn checkpoint(&self, branch: BranchId) -> Result<(), StoreError> {
        self.0.checkpoint(branch)
    }

    fn rollback(&self, branch: BranchId) -> Result<(), StoreError> {
        self.0.rollback(branch)
    }

    fn seal(&self, branch: BranchId) -> Result<SealedCommit, StoreError> {
        let sealed = self.0.seal(branch)?;
        LAST_SEALED.set(Some((branch, sealed.state_root)));
        Ok(sealed)
    }

    fn discard(&self, branch: BranchId) -> Result<(), StoreError> {
        self.0.discard(branch)
    }

    fn commit(&self, root: BranchId) -> Result<CommitId, StoreError> {
        self.0.commit(root)
    }

    fn branch_info(&self, branch: BranchId) -> Result<BranchInfo, StoreError> {
        self.0.branch_info(branch)
    }

    fn create(&self, branch: BranchId, key: RecordKey, cells: Vec<(CellName, Cell)>, budget: Option<Budget>) -> Result<Metered<RecordKey>, StoreError> {
        self.0.create(branch, key, cells, budget)
    }

    fn get(&self, target: ReadTarget, key: RecordKey, projection: Option<&[CellName]>, budget: Option<Budget>) -> Result<Metered<Option<Record>>, StoreError> {
        self.0.get(target, key, projection, budget)
    }

    fn patch(
        &self,
        branch: BranchId,
        key: RecordKey,
        expected_version: Option<RecordVersion>,
        changes: Vec<(CellName, CellChange)>,
        budget: Option<Budget>,
    ) -> Result<Metered<RecordVersion>, StoreError> {
        self.0.patch(branch, key, expected_version, changes, budget)
    }

    fn delete(&self, branch: BranchId, key: RecordKey, expected_version: Option<RecordVersion>, budget: Option<Budget>) -> Result<Metered<()>, StoreError> {
        self.0.delete(branch, key, expected_version, budget)
    }

    fn query(&self, at: Option<CommitId>, query: &Query, budget: Option<Budget>) -> Result<Metered<QueryResult>, StoreError> {
        self.0.query(at, query, budget)
    }

    fn count(&self, at: Option<CommitId>, filter: &Filter, budget: Option<Budget>) -> Result<Metered<u64>, StoreError> {
        self.0.count(at, filter, budget)
    }

    fn branch_hash(&self, branch: BranchId) -> Result<[u8; 32], StoreError> {
        self.0.branch_hash(branch)
    }

    fn segment_append(&self, branch: BranchId, segment: &str, key: Option<SegmentKey>, row: SegmentRow) -> Result<SegmentOrdinal, StoreError> {
        self.0.segment_append(branch, segment, key, row)
    }

    fn segment_append_many(&self, branch: BranchId, writes: &[(&str, Option<SegmentKey>, SegmentRow)]) -> Result<Vec<SegmentOrdinal>, StoreError> {
        self.0.segment_append_many(branch, writes)
    }

    fn segment_get(&self, segment: &str, address: SegmentAddress) -> Result<Option<SegmentRow>, StoreError> {
        self.0.segment_get(segment, address)
    }

    fn segment_ordinal_of(&self, segment: &str, key: SegmentKey) -> Result<Option<SegmentOrdinal>, StoreError> {
        self.0.segment_ordinal_of(segment, key)
    }

    fn segment_range_of(&self, segment: &str, commit: CommitId) -> Result<core::ops::Range<SegmentOrdinal>, StoreError> {
        self.0.segment_range_of(segment, commit)
    }

    fn segment_rows_of(&self, segment: &str, commit: CommitId) -> Result<Vec<SegmentRow>, StoreError> {
        self.0.segment_rows_of(segment, commit)
    }
}

impl StoreExt for SealWatch {
    fn commit_tagged(&self, root: BranchId, tag: [u8; 32]) -> Result<CommitId, StoreError> {
        self.0.commit_tagged(root, tag)
    }

    fn commit_by_tag(&self, tag: [u8; 32]) -> Result<Option<CommitId>, StoreError> {
        self.0.commit_by_tag(tag)
    }

    fn tag_of(&self, commit: CommitId) -> Result<Option<[u8; 32]>, StoreError> {
        self.0.tag_of(commit)
    }

    fn changes(&self, commit: CommitId) -> Result<Vec<RecordChange>, StoreError> {
        self.0.changes(commit)
    }

    fn apply(&self, branch: BranchId, ops: Vec<WriteOp>, budget: Option<Budget>) -> Result<Metered<Vec<WriteOutcome>>, StoreError> {
        self.0.apply(branch, ops, budget)
    }

    fn commit_hash(&self, commit: CommitId) -> Result<[u8; 32], StoreError> {
        self.0.commit_hash(commit)
    }

    fn retention(&self) -> (CommitId, CommitId) {
        self.0.retention()
    }

    fn get_many(&self, target: ReadTarget, keys: &[RecordKey], projection: Option<&[CellName]>, budget: Option<Budget>) -> Result<Metered<Vec<Option<Record>>>, StoreError> {
        self.0.get_many(target, keys, projection, budget)
    }
}
