# Pattern Analyzer Plugin

The plugin serves JSON traces of the stats of top patterns observed by the EVM
during execution.

**Relevant config parameters**:

- **Enabled**: The ability to enable or disable the plugin.

## Analyzer

The Analyzer primarily consists of the **StatsAnalyzer**, **CMSKetch**,
**NGram **and **StatsProcessingQueue
**.

### Stats Analyzer

This is primarily responsible for processing the instructions and accumulating the stats for them. It accumulates counts for all n-grams observed of sizes 2-7 opcodes. Under the hood, it tracks these patterns via the CMSketch component and has the ability to dynamically provision new sketches which is determined by a sketch reset parameter e.g. setting it to 0.01 would provision a new sketch when the max expected error for any given opcode has breached 1% and thus strategically attempts to curtail the total observed error to 1%. Furthermore, the number of such sketches provisioned can be configured by adjusting the buffer size.

**Relevant config parameters**:

- **AnalyzerTopN**: The number of top **NGrams** to track .
- **AnalyzerMinSupportThreshold**: sets the initial minimum count a
  pattern has to breach to start getting tracked by the analyzer.
- **AnalyzerCapacity** sets the initially allocated space for intermediary storage of n-gram frequencies which are later used to populate or update the top N queue.
- **AnalyzerBufferSize** for filters used in tracking.
- **AnalyzerSketchResetOrReuseThreshold**: Sets the error threshold for
  provisioning new sketches.
- **Ignore**: comma separated string of opcodes to ignore e.g. setting this to "JUMP, JUMPDEST"
  will not track those two opcodes

### CMSketch

A [Count Min SKetch](http://dimacs.rutgers.edu/~graham/pubs/papers/cm-full.pdf) is a probabilistic data structure that is efficient for tracking high volume data. The implemented component allows us to build our sketch from either (max confidence and min error) or by setting the (number of buckets and the number of hash functions). Furthermore, the sketch tracks the expected error per item and the confidence that our counts our below (count + error per item) regardless of which pair of parameters were used in configuring the sketch.

**Relevant config parameters**:

- **SketchBuckets**: Sets the number of buckets to use in CMSketch
- **SketchHashFunctions**: Sets the number of hash functions to use in CMSketch
- **SketchMaxError**: Sets the number of buckets derived from error to use in CMSketch
- **SketchMinConfidence**: Sets the number of hash functions derived from min confidence to use in CMSketch

Note: The user can set either the pair (buckets, hash functions) or (error, confidence) but not both.

### NGram

This component handles efficient encoding of of opcode patterns observed packing a given pattern into a ulong value. It also has an efficient way to derive all the sub-sequences from a pattern and apply a given processing function to them.

### StatsProcessingQueue.

This consists of primarily of the stats analyzer and instruction buffer to queue the instructions from the EVM that are later processed by the analyzer when the queue is disposed.

**Relevant config parameters**:

- **InstructionsQueueSize**: Sets the size of the queue used to gather instructions per write frequency.

## Tracer

The plugin hosts a set of tracing components of which help serve the traces to a
given JSON file at the provided write frequency. The trace contains the Blocks
traversed, the error and confidence of the stats and then the stats of the
n-grams witnessed during execution

```JSON
{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}
```

**Relevant config parameters**:

- **File**: Configurable file location for dumping the stats
- **WriteFrequency**: Users can define the frequency (in blocks) for writing stats to disk.
- **ProcessingQueueSize**: Sets the number of tasks that can be queued when tracing & dumping stats in background
