# FrameJournalExtractor

This standalone package verifies the pre-commit nested-frame composition of the
accepted Nethermind world-journal transition. It source-admits concrete
standard-mainnet child entry and exit paths, emits a theorem-free Lean frame
transition, and proves it equal to a separately written reference for arbitrary
finite traces.

The generated transition supports parent and child world operations, CALL and
CREATE entry, no-child admission failure, the transaction-wide historical
RIPEMD-160 touch latch, and success/REVERT/exception exit.
Nested checkpoints are LIFO. Success retains account, storage, transient,
warm-access, log, and SELFDESTRUCT effects; rollback restores all seven surfaces
and then replays a latched RIPEMD-160 empty-account touch. Transaction-start
originals, the created-this-transaction set, and the RIPEMD latch survive
restoration.

See [SOURCE_AUDIT.md](SOURCE_AUDIT.md) for exact production flow and
[SCOPE.md](SCOPE.md) for the proof boundary. In particular, the CALL/CREATE and
SELFDESTRUCT dependencies are accepted handwritten leaves, not production
refinement theorems, and this package does not cover gas settlement, code
deposit, final account reaping, other precompile behavior, tries, databases,
transactions, or blocks.

Run from the repository root after the pinned Lean toolchain is installed:

```powershell
$env:ELAN_HOME = 'D:\tmp\formal-lean-toolchain\home'
$env:PATH = "$env:ELAN_HOME\bin;$env:PATH"
tools\Evm\Lean\FrameJournalExtractor\Verify.ps1
```
