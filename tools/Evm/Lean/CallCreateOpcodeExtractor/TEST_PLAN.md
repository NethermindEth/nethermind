# Fail-closed and refinement test plan

Dynamic evidence is complete for this correction: all 41 discovered MTP tests,
the 14-job package Lake build, and 4 direct warning-as-error Lean checks pass.
Two post-format fresh extractions are byte-identical to each other and all three
checked artifacts. The suite uses parameterized cases and in-test loops, so its
discovered-case count does not equal the larger number of individual mutations
executed. Every
canonical IR/manifest property and every admitted C#/raw input is visited by
the specific checks below. Acceptance requires two explicit regenerations to
match each other and the checked artifacts byte for byte, plus package and
direct warning-as-error Lean checks for both specification modules, the
generated kernel, and the refinement module.

## Extractor and artifact boundary

The test fixture builds one minimal exact source tree from the production
input paths and exercises extraction through the same public package entry point
used to regenerate checked artifacts.

The executable mutation coverage is exactly:

- every object property in the checked IR and manifest is independently
  omitted, set to null, renamed by changing the first letter's case, duplicated,
  and, for primitive values, changed to another value of the same JSON type;
- each scalar entry of `forkLineage`, `openExtractionObligations`, every opcode
  `effectOrder`, and manifest `semanticBindings` is independently set to null or
  changed; each non-singleton scalar array also has its first two entries swapped;
- every C# source and each of the four raw inputs has one byte appended in an
  isolated source-tree copy and must be rejected by extraction;
- competing standard declarations are injected for the three opcode-handler
  types; four representation mutations cover action tracing, rollback token,
  child error, and direct-precompile gas type;
- every manifest digest is tested with uppercase and newline injection; manifest
  IR/Lean artifact digests are also replaced by a different valid lowercase
  digest; the emitter's IR and source digests are each tested for short, long,
  non-hex, uppercase, leading-space, and embedded-newline forms, plus successful
  embedding of alternate valid lowercase digests;
- fresh extraction is compared byte-for-byte with checked IR, manifest, and
  Lean, and two fresh outputs are compared with each other; checked artifacts
  are tested as strict UTF-8 without BOM, LF-only, and newline-terminated;
- generated Lean is scanned for proof declarations and handwritten target names;
  one opcode operation leaf and the CALL-value schedule leaf are changed and
  shown to change executable emitted definitions.
- the inert representation is checked not to define table tracing, address
  normalization, or deposit ordering; generated and reference modules must own
  distinct definitions. One-sided reference mutations cover a traced-table
  inversion, low-160 address-width change, and CREATE deposit-order swap. The
  generated module is rejected if it imports or names that reference;

The suite does not claim systematic declaration deletion/case-renaming,
namespace/owner/generic/signature/overload mutations, competing case aliases,
separate trivia-versus-normalized-syntax mutations, std/zk declaration swaps,
canonical JSON reserialization checks, trailing-space digest injection, object
array entry mutations, or emitter coverage for every semantic IR leaf.

## Operational mutation matrix

The semantic matrix below is the review inventory used to shape IR ordering,
generated definitions, executable assertions, and the universal immediate and
resume/settlement equalities. It does not imply one separately discovered MTP
case for every bullet; the theorem quantifies the complete state and oracle
domain instead of enumerating example vectors.

### Dispatch and tracing

- start tracing after rather than before PC/count advancement;
- omit one of the four dispatch tables or one of the 28 opcode roots;
- select a wrong Amsterdam fork flag at any CALL/CREATE/SELFDESTRUCT selector;
- treat CREATE as generic continuable trace ownership;
- remove CREATE's pre-reservation trace end or move it after reservation;
- close successful CREATE suspend twice;
- omit early CREATE continuation closure;
- omit CALL suspend closure or the parent-resume result report;
- close SELFDESTRUCT in the immediate body rather than terminal VM handling;
- report a result push before the stack mutation or with the wrong byte width.

### CALL family

- make call operands atomic instead of retaining sequential-pop residue;
- pop value for DELEGATECALL/STATICCALL or fail to pop it for CALL/CALLCODE;
- preserve high code-source bits instead of reducing every CALL-family address
  to its low 160 bits;
- use zero rather than environment value for DELEGATECALL;
- reject CALLCODE value in a static context;
- charge value after base/memory/access;
- give value charge to DELEGATECALL/STATICCALL;
- swap input and output memory expansion;
- warm/access the target before a value-charge OOG;
- load code before charging code-source access;
- charge delegated access before delegation discovery or omit it;
- use physical existence instead of deadness for Amsterdam NEW_ACCOUNT;
- charge NEW_ACCOUNT before account access;
- cap requested gas before all fixed/dynamic/state charges;
- include state reservoir in the 63/64 cap;
- debit the stipend a second time or omit it from child gas;
- check depth/balance before reservation;
- burn rather than return reservation on depth/balance failure;
- retain NEW_ACCOUNT charge on depth/balance failure;
- mutate world state before the empty-code success push;
- treat standard untraced STATICCALL precompile as an ordinary child;
- execute RIPEMD160 through the inline STATICCALL path;
- accept inline-precompile remaining gas above the reserved child gas;
- load a non-zero-extended or wrong-length input slice;
- fail to normalize a zero-length output destination;
- snapshot before the transfer debit or share the wrong access journal.

### CREATE family

- check stack before the static-context rejection;
- pop CREATE2 salt in the wrong position;
- move init-code size/ceiling checks after charging;
- omit init-word or CREATE2 hash-word execution cost;
- charge CREATE2 hash words on CREATE;
- check depth before memory expansion;
- read balance/nonce or derive/warm destination before init-code load;
- derive destination before depth/balance/nonce failure;
- fail to warm the destination before collision classification;
- collapse physical-leaf and logical-account facts;
- ignore storage-only collision;
- charge CREATE state gas for logical existence or omit it for logical absence;
- reserve child gas before state charge;
- report pre-child remaining gas after reservation;
- snapshot/increment nonce in the wrong order;
- return reserved gas on collision instead of burning it;
- retain CREATE state charge on collision;
- clear return data or push zero in the wrong order;
- clear storage on Amsterdam noncollision;
- transfer value or create a child on collision;
- fail to carry physical preexistence into deposit rollback.

### Child resume, journal, and code deposit

- accept a child gas state that does not descend from the staged entry;
- merge child state/journal on REVERT or exceptional halt;
- return child execution gas on exception;
- burn remaining execution gas on REVERT;
- omit NEW_ACCOUNT/CREATE state refill on REVERT/exception;
- merge advanced state-gas credits in the wrong order;
- copy more return bytes than requested or use the wrong output destination;
- expose exception output as return data;
- push child result after resumed dispatch rather than before it;
- charge code deposit before child gas refund or charge the child rather than
  the refunded parent;
- use bytes rather than ceiling words for Amsterdam deposit execution gas;
- omit per-byte deposit state gas or charge it as execution gas;
- insert code before both deposit dimensions and validity pass;
- accept invalid `0xEF` runtime code;
- treat duplicate valid code as a free deposit;
- leave the destination materialized on failed fresh creation;
- convert optional legacy deposit failure into mandatory Amsterdam behavior or
  vice versa;
- emit action end/error/revert against the wrong gas boundary.

### SELFDESTRUCT

- pop beneficiary before the 5,000 base charge;
- fail to retain base charge on stack underflow;
- preserve beneficiary high bits instead of normalizing immediately after pop;
- use the raw rather than normalized beneficiary for warmth, access gas, action
  tracing, classification, or the world-transition oracle boundary;
- charge access without warming on access OOG;
- mark destruction when the source was not created in this transaction;
- classify beneficiary before balance/action reporting;
- reverse ACCOUNT_WRITE execution and NEW_ACCOUNT state charges;
- charge NEW_ACCOUNT on a zero balance;
- create/credit the beneficiary before all charges pass;
- move balance for an Amsterdam self-target;
- burn self-target balance despite EIP-8246;
- log a zero/self-target transfer incorrectly;
- debit source before beneficiary creation/credit;
- return a continuable status rather than terminal stop.

## Lean checks

The standalone package builds the representation module, generated kernel, and
refinement module, then runs each file directly with warnings as errors. The
current refinement surface includes:

- one package-wide equality over all seven opcodes, four dispatch tables,
  handler oracles, production-domain machine states, and supplied child
  outcomes, covering both immediate execution and child resume/settlement;

- all four dispatch-root specializations for every opcode;
- CALL value/new-account rules for all four call kinds and CALLCODE state-charge
  suppression;
- CREATE entry and code-deposit cost equality for both create kinds;
- child success, REVERT, exception, invalid-code, deposit-OOG, and upfront-state
  refill gas projections;
- low-160-bit code-source normalization for all four call kinds, the
  `2^160 + 3` RIPEMD160 alias rejection, and the inline-precompile remaining-gas
  bound;
- raw CREATE child refund equivalence, parent-deposit/child-deposit success
  observation, explicit refund-to-halt failure residue, and phase order;
- the SELFDESTRUCT new-account predicate;
- exact trace ownership at successful CREATE suspend, early CREATE continuation,
  CALL suspend/resume, and SELFDESTRUCT termination;
- preservation of state-gas fields by pure execution charges and retained state
  on state-charge OOG;
- CREATE-fact and child-gas oracle-consistency rejection before mutation/merge.

Proof placeholders and kernel-native shortcuts are prohibited. Direct
warning-as-error checks must use the same toolchain pinned by `lean-toolchain`.
