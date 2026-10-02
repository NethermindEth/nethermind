Experimental spin mechanism diagnostic

This branch accepts one pinned master-c88b947 image and one fresh realblocks replay
per workflow dispatch. Default and DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0
use the same image, snapshot overlay, 3 warmup payloads and 1000 measured payloads.
The first dispatch is capability validation. A D/0/0/D quartet requires successful
review of that capture and unchanged inputs/tools. These profiled times do not
replace unprofiled performance measurements.

The EXPB source is commit797db21d602901e35dad6c2c5aa7b570f614d7ff plus four
reviewed patches applied only to an isolated runner-temporary checkout. SHA256
checks protect the bundle. Existing benchmark containers/overlays cause an abort;
this diagnostic does not reap another run's processes or state.

Observations: native GC EventPipe at informational level, bounded simultaneous
scheduler switch/wakeup metadata, native k6 timestamped request phases with
physical payload IDs and VALID checks, source payload/FCU hashes, actual running
image/runtime/file identities, independent process/cgroup CPU boundaries.
All process signals target owned children through verified identities. Captures
that exit early, exceed bounds or cannot confirm ownership fail closed.

The public artifact contains numeric target-thread/request projections and a CMS
encrypted archive. The corresponding private key is held only by the operator;
recipient.crt is public. Do not upload the raw scheduler data, trace, names, full
environment or decrypted archive. Original observations remain under the exact
private RUNNER_TEMP/spin-diagnostic-RUN_ID-ATTEMPT directory until retrieval is
confirmed. Failure evidence is encrypted as well; it is not a successful capture.

Review actual perf/tool versions, lost events, syscall/clock precision, target
thread coverage, full request/GC windows and cleanup after the Linux capability
run. Polling TID maps are incomplete; off-CPU is not automatically a managed
threadpool queue wait. Request and block processing intervals can overlap and
must not be subtracted as nested windows. Snapshot metadata fingerprints are
not full snapshot content hashes.

Preparation checks: scheduler25, runtime10, client identity11, request9 and
packaging14 offline tests; real Git patch composition, YAML/Bash resolve gates,
and synthetic OpenSSL encryption/decryption roundtrip. Windows tests do not
certify Linux availability, ownership or profiler overhead.
