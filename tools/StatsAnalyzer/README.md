# Stats Analyzer Plugin

The plugin serves JSON traces of the stats of top patterns of **2-7 opcodes** observed by the EVM during execution.

**Relevant config parameters**:

- **Enabled**: The ability to enable or disable the plugin.

## Pattern Analyzer

The Analyzer primarily consists of the **StatsAnalyzer**, **CMSKetch**, **NGram** and **StatsProcessingQueue**.

### Stats Analyzer

This is primarily responsible for processing the instructions and accumulating the stats for them. It accumulates counts for all n-grams observed of sizes 2-7 opcodes. Under the hood, it tracks these patterns via the CMSketch component and has the ability to dynamically provision new sketches which is determined by a sketch reset parameter e.g. setting it to 0.01 would provision a new sketch when the max expected error for any given opcode has breached 1% and thus strategically attempts to curtail the total observed error to 1%. Furthermore, the number of such sketches provisioned can be configured by adjusting the buffer size.

**Relevant config parameters**:

- **AnalyzerTopN**: The number of top **NGrams** to track .
- **AnalyzerMinSupportThreshold**: sets the initial minimum count a pattern has to breach to start getting tracked by the analyzer.
- **AnalyzerCapacity** sets the initially allocated space for intermediary storage of n-gram frequencies which are later used to populate or update the top N queue.
- **AnalyzerBufferSize** Buffer size for sketches e.g. setting it 1 would only use one sketch for the stats;
- **AnalyzerSketchResetOrReuseThreshold**: Sets the error threshold for provisioning new sketches.
- **Ignore**: comma separated string of opcodes to ignore e.g. setting this to "JUMP, JUMPDEST" will not track those two opcodes

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

- (not used, to remove) **InstructionsQueueSize**: Sets the size of the queue used to gather instructions per write frequency.

## Tracer

The plugin hosts a set of tracing components of which help serve the traces to a given JSON file at the provided write frequency. The trace contains the Blocks traversed, the error and confidence of the stats and then the stats of the n-grams witnessed during execution. Furthermore, the user can choose the sort order of the stats delivered or have them unordered. This has effect on processing time: unordered stats being the fastest followed by ascending stats and finally, descending stats are the slowest.

```JSON
{"initialBlockNumber":15537396,"currentBlockNumber":15537396,"errorPerItem":0.006,"confidence":0.9375,"stats":[{"pattern":"PUSH1 PUSH1","bytes":[96,96],"count":2},{"pattern":"PUSH1 PUSH1 PUSH1","bytes":[96,96,96],"count":1}]}
```

**Relevant config parameters**:

- **File**: Configurable file location for dumping the stats
- **WriteFrequency**: Users can define the frequency (in blocks) for writing stats to disk.
- **ProcessingQueueSize**: Sets the number of tasks that can be queued when tracing & dumping stats in background
- **ProcessingMode**: Can be set to either sequential or bulk. This strategy is triggered when the queue is full and the tracer decides to either clear the processing debt one task at a time or in bulk.
- **Sort**: can be unordered, ascending and descending.


## Call Analyzer

Gives stats on the most frequent address that were called (STATICCALL,
DELEGATECALL, CALLCODE, CALL),  excludes precompiles.

```JSON
{"initialBlockNumber":70805,"currentBlockNumber":1636994,"stats":[{"address":"0xedda782791e195b660d6fcf38e63eda268634ff7","count":26},{"address":"0x45e1022953a9406cd46f4aeff12ee2530c6aae20","count":38},{"address":"0x5c210ef41cd1a72de73bf76ec39637bb0d3d7bee","count":64},{"address":"0x23317519a16b4387ac9096679672ebfc1368ad5e","count":65},{"address":"0x7a1bac17ccc5b313516c5e16fb24f7659aa5ebed","count":114},{"address":"0xb227f007804c16546bd054dfed2e7a1fd5437678","count":118},{"address":"0xa385fd5fd33e41a8cab51de119825876d04e23d5","count":156},{"address":"0x6085268ab3e3b414a08762b671dc38243b29621c","count":170},{"address":"0xe6626372f2dd20467db08ec1f2f0e7c6c4e9bbcd","count":176},{"address":"0x83b91c103e0e8760b57bbc3265afbbc5585d8393","count":176},{"address":"0x995b96ea23cdbe69e3c3ebca351b500db09a70ca","count":225},{"address":"0xa567c273df3154c86ebcd4bda09cdde60a29ec5e","count":264},{"address":"0xa293d1072d01fb9330c79bbebc4ccff5becd1f48","count":401},{"address":"0x464e1e4a69f6d497b24fd0a084dc401e31092b97","count":1621},{"address":"0xb4c4a493ab6356497713a78ffa6c60fb53517c63","count":1670},{"address":"0x40296c73ac768f962c20558d19b1e2371e3a1a45","count":1670},{"address":"0x76aa17dcda9e8529149e76e9ffae4ad1c4ad701b","count":1735},{"address":"0x1f1df9f7fc939e71819f766978d8f900b816761b","count":1735}]}
```

**Relevant config parameters**:

- **File**: Configurable file location for dumping the stats
- **WriteFrequency**: Users can define the frequency (in blocks) for writing stats to disk.
- **ProcessingQueueSize**: Sets the number of tasks that can be queued when tracing & dumping stats in background
- **ProcessingMode**: Can be set to either sequential or bulk. This strategy is triggered when the queue is full and the tracer decides to either clear the processing debt one task at a time or in bulk.
- **Sort**: can be unordered, ascending and descending.
- **TopN**: The number of top addresses to track .
