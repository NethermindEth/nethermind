---
paths:
  - "src/Nethermind/Nethermind.Blockchain/**/*.cs"
---

# Nethermind.Blockchain

Block processing, chain management, and validators.

Key classes:
- `BlockTree` — canonical chain management
- `BlockProcessor` / `BlockProcessor.ProcessOne()` — single block processing
- `BranchProcessor` — processes block branches

Block processing is wired via `BlockProcessingModule` in `Nethermind.Init/Modules/`. Don't manually construct `BlockProcessor` — use DI.

For tests, use `TestBlockchain` which provides a fully-wired `BlockTree` and `BlockProcessor`.

## IBlockTree vs IReadOnlyBlockTree

- Inject `IReadOnlyBlockTree` in components that only read the chain (RPC handlers, validators, sync). It has no `SuggestBlock`, `UpdateMainChain`, or insert methods.
- Inject `IBlockTree` only in components that actively modify the chain (block processors, importers, beacon fork-choice).
- `ReadOnlyBlockTree` wraps `IBlockTree` — get it via DI, don't construct it directly.

## Block tree suggestion and insertion options

Use `BlockTreeSuggestOptions` and `BlockTreeInsertHeaderOptions` when calling `SuggestBlock` / `Insert`:

```csharp
// Suggest with full validation (default for new blocks from the network)
blockTree.SuggestBlock(block, BlockTreeSuggestOptions.ShouldProcess);

// Insert a header without processing (e.g. beacon chain headers)
blockTree.Insert(header, BlockTreeInsertHeaderOptions.BeaconHeaderMetadata);
```

Never pass raw `bool` flags by constructing the enum manually — use the named constants.

## Validator hierarchy

Each validator is independent; none extends another:

| Interface | Validates |
|-----------|-----------|
| `IBlockValidator` | Full block (header + body + transactions) |
| `IHeaderValidator` | Block header only (PoW/PoA seal, difficulty) |
| `ITxValidator` | Individual transaction |

Add new validation logic by implementing the appropriate interface and registering it as a composite via `AddComposite<IBlockValidator, CompositeBlockValidator>()` in the DI module. Don't add validation logic directly to `BlockProcessor`.

## Block tree events — subscribe, don't poll

| Event | Fires when |
|-------|-----------|
| `NewBestSuggestedBlock` | A block with a higher total difficulty is suggested |
| `NewHeadBlock` | The canonical head changes |
| `BlockAddedToMain` | A block (and its ancestors) become part of the main chain |
| `OnUpdateMainChain` | A batch of blocks is added to main chain in one operation |

Subscribe to events rather than polling `BlockTree.Head`. Never re-enter `BlockTree` synchronously inside an event handler — schedule work on a background queue.

## Receipts and receipt storage

- `IReceiptStorage` is the writable interface; `IReceiptFinder` is read-only.
- Use `IReceiptFinder` in read-heavy paths (RPC, indexers).
- `ReceiptCanonicalityMonitor` maintains receipt-to-block consistency when the chain reorganizes — don't replicate that logic elsewhere.

## Subdirectories

- `Blocks/` — `IBlockStore` / `BlockStore`: raw block body persistence
- `Headers/` — `IHeaderStore` / `HeaderStore`: raw header persistence
- `Find/` — `IBlockFinder`, `BlockParameter`: flexible block lookup by hash, number, or tag
- `Visitors/` — `IBlockTreeVisitor` / `BlockTreeVisitor`: visitor pattern for chain traversal
- `FullPruning/` — pruning triggers and state cleaner
- `Receipts/` — receipt recovery and storage implementations
