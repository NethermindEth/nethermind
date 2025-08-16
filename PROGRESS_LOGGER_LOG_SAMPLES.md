# ProgressLogger Log Samples

This document provides examples of the improved logging output for various operations after implementing ProgressLogger improvements.

## Full Pruning Log Samples

### Starting Full Pruning
```
2024-01-15 10:30:15.123 | INFO  | Full Pruning Started on root hash 0x1234567890abcdef...: do not close the node until finished or progress will be lost.
2024-01-15 10:30:15.124 | INFO  | Estimated 10,000,000 nodes to process during full pruning
2024-01-15 10:30:15.125 | INFO  | Full Pruning Progress: 0 / 10,000,000 (0.0%) | Speed: 0 nodes/sec | [████████████████████] 0%
```

### During Full Pruning (Progress Updates)
```
2024-01-15 10:30:45.456 | INFO  | Full Pruning Progress: 1,000,000 / 10,000,000 (10.0%) | Speed: 33,333 nodes/sec | [██░░░░░░░░░░░░░░░░░░] 10%
2024-01-15 10:31:15.789 | INFO  | Full Pruning Progress: 2,000,000 / 10,000,000 (20.0%) | Speed: 33,333 nodes/sec | [████░░░░░░░░░░░░░░░░] 20%
2024-01-15 10:31:46.012 | INFO  | Full Pruning Progress: 3,000,000 / 10,000,000 (30.0%) | Speed: 33,333 nodes/sec | [██████░░░░░░░░░░░░░░] 30%
2024-01-15 10:32:16.345 | INFO  | Full Pruning Progress: 4,000,000 / 10,000,000 (40.0%) | Speed: 33,333 nodes/sec | [████████░░░░░░░░░░░░] 40%
2024-01-15 10:32:46.678 | INFO  | Full Pruning Progress: 5,000,000 / 10,000,000 (50.0%) | Speed: 33,333 nodes/sec | [██████████░░░░░░░░░░] 50%
2024-01-15 10:33:17.001 | INFO  | Full Pruning Progress: 6,000,000 / 10,000,000 (60.0%) | Speed: 33,333 nodes/sec | [████████████░░░░░░░░] 60%
2024-01-15 10:33:47.334 | INFO  | Full Pruning Progress: 7,000,000 / 10,000,000 (70.0%) | Speed: 33,333 nodes/sec | [██████████████░░░░░░] 70%
2024-01-15 10:34:17.667 | INFO  | Full Pruning Progress: 8,000,000 / 10,000,000 (80.0%) | Speed: 33,333 nodes/sec | [████████████████░░░░] 80%
2024-01-15 10:34:48.000 | INFO  | Full Pruning Progress: 9,000,000 / 10,000,000 (90.0%) | Speed: 33,333 nodes/sec | [██████████████████░░] 90%
```

### Completed Full Pruning
```
2024-01-15 10:35:18.333 | INFO  | Full Pruning Progress: 9,876,543 / 10,000,000 (98.8%) | Speed: 33,333 nodes/sec | [████████████████████] 98.8%
2024-01-15 10:35:18.334 | INFO  | Full Pruning Finished: 00:05:03.211 | Nodes: 9,876,543 / 10,000,000 (98.8%) | Speed: 32,567 nodes/sec | Mirrored: 9.9 mln nodes
```

## Trie Verification Log Samples

### Starting Trie Verification
```
2024-01-15 11:00:00.000 | INFO  | Trie Verification Progress: 0 / 0 (0.0%) | Speed: 0 nodes/sec | [████████████████████] 0%
```

### During Trie Verification
```
2024-01-15 11:00:30.123 | INFO  | Trie Verification Progress: 1,000,000 / 0 (0.0%) | Speed: 33,333 nodes/sec | [████████████████████] 100%
2024-01-15 11:01:00.456 | INFO  | Trie Verification Progress: 2,000,000 / 0 (0.0%) | Speed: 33,333 nodes/sec | [████████████████████] 100%
2024-01-15 11:01:30.789 | INFO  | Trie Verification Progress: 3,000,000 / 0 (0.0%) | Speed: 33,333 nodes/sec | [████████████████████] 100%
```

### Completed Trie Verification
```
2024-01-15 11:02:01.122 | INFO  | Trie Verification Progress: 3,456,789 / 0 (0.0%) | Speed: 28,806 nodes/sec | [████████████████████] 100%
2024-01-15 11:02:01.123 | INFO  | Trie Verification completed: 3,456,789 nodes verified in 00:02:01.122
```

## Supply Verification Log Samples

### Starting Supply Verification
```
2024-01-15 12:00:00.000 | INFO  | Supply Verification Progress: 0 / 0 (0.0%) | Speed: 0 accounts/sec | [████████████████████] 0%
```

### During Supply Verification
```
2024-01-15 12:00:10.123 | INFO  | Supply Verification Progress: 1,000 / 0 (0.0%) | Speed: 100 accounts/sec | [████████████████████] 100%
2024-01-15 12:00:20.456 | INFO  | Supply Verification Progress: 2,000 / 0 (0.0%) | Speed: 100 accounts/sec | [████████████████████] 100%
2024-01-15 12:00:30.789 | INFO  | Supply Verification Progress: 3,000 / 0 (0.0%) | Speed: 100 accounts/sec | [████████████████████] 100%
```

### Completed Supply Verification
```
2024-01-15 12:01:00.122 | INFO  | Supply Verification Progress: 5,234 / 0 (0.0%) | Speed: 87 accounts/sec | [████████████████████] 100%
2024-01-15 12:01:00.123 | INFO  | Supply Verification completed: Total ETH supply: 120,000,000.123456789 ETH
```

## Total Difficulty Fix Migration Log Samples

### Starting Migration
```
2024-01-15 13:00:00.000 | INFO  | TotalDifficulty Fix Progress: 0 / 1,000,000 (0.0%) | Speed: 0 blocks/sec | [████████████████████] 0%
```

### During Migration
```
2024-01-15 13:00:30.123 | INFO  | TotalDifficulty Fix Progress: 1,000 / 1,000,000 (0.1%) | Speed: 33 blocks/sec | [░░░░░░░░░░░░░░░░░░░░] 0.1%
2024-01-15 13:01:00.456 | INFO  | TotalDifficulty Fix Progress: 2,000 / 1,000,000 (0.2%) | Speed: 33 blocks/sec | [░░░░░░░░░░░░░░░░░░░░] 0.2%
2024-01-15 13:01:30.789 | INFO  | TotalDifficulty Fix Progress: 3,000 / 1,000,000 (0.3%) | Speed: 33 blocks/sec | [░░░░░░░░░░░░░░░░░░░░] 0.3%
```

### Completed Migration
```
2024-01-15 13:45:30.122 | INFO  | TotalDifficulty Fix Progress: 1,000,000 / 1,000,000 (100.0%) | Speed: 37 blocks/sec | [████████████████████] 100%
2024-01-15 13:45:30.123 | INFO  | TotalDifficulty Fix Migration completed: 1,000,000 blocks processed, 1,234 blocks fixed
```

## Key Improvements Demonstrated

1. **Visual Progress Bars**: Clear visual representation of progress with ASCII progress bars
2. **Percentage Completion**: Exact percentage completion for operations with known totals
3. **Speed Metrics**: Real-time speed measurements (nodes/sec, accounts/sec, blocks/sec)
4. **Detailed Statistics**: Comprehensive information about processed items and timing
5. **Consistent Format**: Standardized logging format across all operations
6. **Backward Compatibility**: Fallback logging when ProgressLogger is not available

## Notes on Full Pruning Progress Tracking

As noted by @asdacap in the GitHub issue:
- The progress cannot be estimated by simply counting total keys in the database
- Full pruning reduces the number of keys in the DB, so not all keys will be copied
- The current implementation provides a conservative estimate based on typical state sizes
- The actual progress shows both estimated total and actual copied nodes
- This gives users a realistic expectation of the operation's scope while showing actual progress
