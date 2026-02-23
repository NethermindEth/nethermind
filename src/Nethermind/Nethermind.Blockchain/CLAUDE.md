# Nethermind.Blockchain

Block processing, chain management, and validators.

Key classes:
- `BlockTree` — canonical chain management
- `BlockProcessor` / `BlockProcessor.ProcessOne()` — single block processing
- `BranchProcessor` — processes block branches

Block processing is wired via `BlockProcessingModule` in `Nethermind.Init/Modules/`. Don't manually construct `BlockProcessor` — use DI.

For tests, use `TestBlockchain` which provides a fully-wired `BlockTree` and `BlockProcessor`.
