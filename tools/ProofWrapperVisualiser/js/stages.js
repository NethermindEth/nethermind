// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Maps debug_proofWrapperEvents types to the lifecycle stage they belong to, and names each step after the EIP-8437 message
// or EIP-8288 rule it implements, so the UI doubles as a guide to the spec.
(function (root) {
  'use strict';

  // Lifecycle stages, in the order a wrapper normally passes through them. `group` picks the colour family.
  const STAGES = [
    { id: 'submitted', label: 'Submitted', group: 'origin', blurb: 'eth_sendProofWrapper: a wrapper enters this node locally.' },
    { id: 'aggregated', label: 'Aggregated', group: 'origin', blurb: 'EIP-8288 background aggregation folds pooled proofs into one mode-1 wrapper (reaggregation reuses the previous proof).' },
    { id: 'announced', label: 'Announced', group: 'retrieval', blurb: 'NewProofWrapperHashes (0x16): [length, hash] pairs, 1–64 per message, strictly ascending.' },
    { id: 'requested', label: 'Requested', group: 'retrieval', blurb: 'GetProofWrappers (0x17): 1–64 distinct hashes, each announced by the recipient.' },
    { id: 'received', label: 'Received', group: 'retrieval', blurb: 'ProofWrappers (0x18): whole wrappers in request order, matched to hashes and announced lengths.' },
    { id: 'recovery', label: 'Envelope recovery', group: 'recovery', blurb: 'GetPooledTransactions (0x09) / PooledTransactions (0x0a) for envelopes the wrapper lists but this node lacks.' },
    { id: 'validated', label: 'Validated', group: 'validation', blurb: 'Encoding, length, hash, every listed envelope resolved, then full EIP-8288 validation (or a verdict-cache hit).' },
    { id: 'rejected', label: 'Rejected / refused', group: 'failure', blurb: 'Invalid proofs are discarded and may disconnect the server; local refusal or unresolved envelopes are not misbehaviour.' },
    { id: 'admitted', label: 'Admitted', group: 'pool', blurb: 'Dependency-bearing transactions enter the pool only under a fully validated covering wrapper.' },
    { id: 'relayed', label: 'Relayed', group: 'relay', blurb: 'Offered with its envelopes and announced to every eth/73 peer not known to hold it; served on request.' },
    { id: 'included', label: 'Included', group: 'chain', blurb: 'A transaction the wrapper covers landed in a canonical block.' },
    { id: 'dropped', label: 'Dropped / evicted', group: 'failure', blurb: 'EIP-8288 upgrade rule (mode 1 dropped, mode 0 kept only if it re-validates), pool eviction, or an abandoned fetch.' },
  ];

  const STAGE_INDEX = Object.fromEntries(STAGES.map((s, i) => [s.id, i]));

  const SPEC = {
    announce: 'https://eips.ethereum.org/EIPS/eip-8437#newproofwrapperhashes-0x16',
    request: 'https://eips.ethereum.org/EIPS/eip-8437#getproofwrappers-0x17',
    response: 'https://eips.ethereum.org/EIPS/eip-8437#proofwrappers-0x18',
    propagation: 'https://eips.ethereum.org/EIPS/eip-8437#transaction-propagation',
    validation: 'https://eips.ethereum.org/EIPS/eip-8437#validation',
    security: 'https://eips.ethereum.org/EIPS/eip-8437#security-considerations',
    wrapper: 'https://eips.ethereum.org/EIPS/eip-8288#mempool-wrapper-object',
    aggregation: 'https://eips.ethereum.org/EIPS/eip-8288#recursive-mempool-aggregation',
    block: 'https://eips.ethereum.org/EIPS/eip-8288#block-validity-recursive-stark',
  };

  // What a wrapper goes through on one node, in order. Each feed event belongs to one phase; the wrapper view shows one row
  // per phase per node and keeps the raw events behind it.
  const PHASES = [
    { id: 'originate', label: 'Originate', spec: SPEC.aggregation, blurb: 'The wrapper starts on this node: submitted over JSON-RPC, or built by background aggregation.' },
    { id: 'announce', label: 'Announce', spec: SPEC.announce, blurb: 'A peer announced the wrapper with NewProofWrapperHashes (0x16); the node decides whether to fetch it.' },
    { id: 'fetch', label: 'Fetch', spec: SPEC.request, blurb: 'GetProofWrappers (0x17) to an announcer, answered by ProofWrappers (0x18) matched by hash and announced length.' },
    { id: 'recover', label: 'Recover envelopes', spec: SPEC.propagation, blurb: 'GetPooledTransactions (0x09) rounds for listed transactions the node lacks; omissions move on to another announcer.' },
    { id: 'validate', label: 'Validate', spec: SPEC.validation, blurb: 'Encoding, length and hash, every envelope resolved, then full EIP-8288 validation (claimed proofs may be checked during recovery).' },
    { id: 'admit', label: 'Admit', spec: SPEC.validation, blurb: 'The covered transactions enter the pool; a pool refusal leaves a valid proof standing and is not a peer fault.' },
    { id: 'relay', label: 'Relay', spec: SPEC.announce, blurb: 'Offered with its envelopes, announced to peers not known to hold it, and served on request.' },
    { id: 'include', label: 'Include', spec: SPEC.block, blurb: 'A transaction the wrapper lists landed in a canonical block.' },
    { id: 'drop', label: 'Drop / evict', spec: SPEC.validation, blurb: 'Removed by the EIP-8288 upgrade rule or pool eviction, or the fetch was given up.' },
  ];
  const PHASE_INDEX = Object.fromEntries(PHASES.map((p, i) => [p.id, i]));

  // Spec text quoted or closely paraphrased from EIP-8437 / EIP-8288, shown next to each step.
  const RULES = {
    connect: 'EIP-8437: peers SHOULD announce offered wrappers on connection.',
    announce: 'EIP-8437: announcements establish no validity; a node fetches wrappers it wants.',
    share: 'EIP-8437: implementations should bound each peer\'s share of pending announcements.',
    request: 'EIP-8437: request 1 to 64 distinct wrapper hashes, each announced by the recipient.',
    response: 'EIP-8437: requesters match wrappers to distinct requested hashes and the responder\'s announced lengths in order.',
    unavailable: 'EIP-8437: omitted entries are unavailable from that peer; unavailability alone MUST NOT be treated as misbehaviour.',
    truncated: 'EIP-8437: responses stop before the 2 MiB soft limit; re-request hashes after the last returned wrapper.',
    validate: 'EIP-8437: check encoding, length and hash, resolve all listed envelopes and fully validate under EIP-8288 before announcing, serving or reaggregating.',
    recover: 'EIP-8437: missing envelopes are recovered with GetPooledTransactions from peers that announced or served the wrapper (≤256 hashes per request).',
    omissions: 'EIP-8437: envelopes omitted before the last returned one are unavailable from that peer; continue with another announcer. Omissions are not misbehaviour.',
    incomplete: 'EIP-8437: unresolved envelopes leave a wrapper incomplete, not invalid.',
    claims: 'EIP-8437: proof checks against the declared dependencies MAY run before or alongside envelope recovery.',
    verdictCache: 'EIP-8288: verdict caches avoid repeat verification of identical wrappers.',
    invalid: 'EIP-8437: invalid proofs MUST be discarded and MAY cause disconnection or penalties.',
    grace: 'EIP-8437: around a verifier change, proof failures are local refusal for a bounded window (one slot before until 64 blocks after the fork).',
    refusal: 'EIP-8437: unavailability or local refusal alone MUST NOT be treated as misbehaviour.',
    violation: 'EIP-8437: count, size or ordering violations SHOULD disconnect; malformed, unmatched or excess data MUST be discarded.',
    admit: 'EIP-8437: dependency-bearing transactions MUST NOT be admitted or relayed without a fully validated covering wrapper.',
    relay: 'EIP-8437: on fully validating a new wrapper, announce it to every connected eth/73 peer not known to hold it.',
    serve: 'EIP-8437: return requested, offered wrappers in request order, omitting unavailable ones.',
    envelopeCache: 'EIP-8437: serve every envelope a held wrapper lists, even after pool rejection or eviction, from a bounded wrapper-envelope cache.',
    upgrade: 'EIP-8288 upgrade rule: drop every mode-1 proof; keep mode-0 witnesses only if they re-validate; evict transactions left uncovered. EIP-8437: never announce or serve wrappers validated only under the previous verifier.',
    deadline: 'EIP-8437: recovery deadlines limit withholding.',
    aggregate: 'EIP-8288: nodes reaggregate validated proofs into mode-1 wrappers on the aggregation cadence.',
    include: 'EIP-8288: a block carries one recursive proof over the dependencies of its transactions.',
    production: 'EIP-8288: a producer never waits for native proving; a block whose proof is not ready is rebuilt from already-proven dependencies.',
  };

  // Event type -> { stage, label, message, rule }. `stage` may be a function of the event.
  const TYPES = {
    'peer-connected': { stage: 'network', label: 'Peer connected (eth/73 Status)', rule: RULES.connect },
    'peer-disconnected': { stage: 'network', label: 'Peer disconnected' },
    'announcement-received': {
      stage: e => (e.result || '').startsWith('dropped') ? 'dropped' : 'announced',
      label: 'Announcement received', message: 'NewProofWrapperHashes (0x16)',
      rule: e => (e.result || '').includes('share') ? RULES.share : RULES.announce,
    },
    'announcements-ignored': { stage: 'network', label: 'Announcements ignored (rate limit)', message: 'NewProofWrapperHashes (0x16)', rule: RULES.share },
    'wrapper-requested': { stage: 'requested', label: 'Wrapper requested', message: 'GetProofWrappers (0x17)', rule: RULES.request, wire: 'out' },
    'wrapper-received': { stage: 'received', label: 'Wrapper received', message: 'ProofWrappers (0x18)', rule: RULES.response },
    'wrapper-unavailable': { stage: 'received', label: 'Omitted by responder', message: 'ProofWrappers (0x18)', rule: RULES.unavailable },
    'wrapper-truncated': { stage: 'received', label: 'Cut by the soft limit', message: 'ProofWrappers (0x18)', rule: RULES.truncated },
    'request-expired': { stage: 'network', label: 'Request timed out', rule: RULES.deadline },
    'fetch-abandoned': { stage: 'dropped', label: 'Fetch abandoned', rule: RULES.deadline },
    'validation-started': { stage: e => e.count > 0 ? 'recovery' : 'received', label: 'Validation started', rule: RULES.validate },
    'envelope-requested': { stage: 'recovery', label: 'Envelopes requested', message: 'GetPooledTransactions (0x09)', rule: RULES.recover, wire: 'out' },
    'envelope-response': { stage: 'recovery', label: 'Envelopes returned', message: 'PooledTransactions (0x0a)', rule: e => e.detail ? RULES.omissions : RULES.recover },
    'envelope-recovery': { stage: 'recovery', label: 'Envelope recovery finished', rule: e => (e.result || '').startsWith('complete') ? RULES.recover : RULES.incomplete },
    'claims-checked': { stage: e => e.result === 'invalid' ? 'rejected' : 'validated', label: 'Claimed proofs checked', rule: e => e.result === 'invalid' ? RULES.invalid : e.durationMs === undefined ? RULES.verdictCache : RULES.claims },
    'proof-verified': { stage: e => e.result === 'invalid' ? 'rejected' : 'validated', label: 'Full EIP-8288 validation', rule: e => e.result === 'invalid' ? RULES.invalid : RULES.validate },
    'verdict-cached': { stage: e => e.result === 'invalid' ? 'rejected' : 'validated', label: e => `Verdict cache hit: ${e.result}`, rule: RULES.verdictCache },
    'verdict': {
      stage: e => e.result === 'Valid' ? 'validated' : 'rejected', label: e => `Verdict: ${e.result}`,
      rule: e => ({ Valid: RULES.validate, Invalid: RULES.invalid, GraceRefused: RULES.grace, Unresolved: RULES.incomplete })[e.result] || RULES.refusal,
    },
    'violation': { stage: 'rejected', label: 'Protocol violation: disconnect', rule: RULES.violation },
    'offered': { stage: 'relayed', label: e => e.result === 'local aggregate' ? 'Offered (own aggregate)' : 'Offered (validated)', rule: RULES.relay },
    'announced': { stage: 'relayed', label: 'Announced onward', message: 'NewProofWrapperHashes (0x16)', rule: RULES.relay, wire: 'out' },
    'served': { stage: 'relayed', label: e => e.result === 'served' ? 'Wrapper served' : `Serve: ${e.result}`, message: 'ProofWrappers (0x18)', rule: RULES.serve, wire: 'out' },
    'envelope-served': { stage: 'relayed', label: 'Envelope served from cache', message: 'PooledTransactions (0x0a)', rule: RULES.envelopeCache },
    'submitted': { stage: e => e.message === 'eth/73' ? 'received' : 'submitted', label: e => e.message === 'eth/73' ? 'Admission (from eth/73)' : 'Submitted (eth_sendProofWrapper)', rule: RULES.validate },
    'admitted': { stage: e => /Accepted|already-pending/.test(e.result || '') ? 'admitted' : 'rejected', label: e => `Pool admission: ${e.result}`, rule: RULES.admit },
    'aggregated': { stage: 'aggregated', label: e => `Aggregation: ${e.result}`, rule: RULES.aggregate },
    'verifier-changed': { stage: 'dropped', label: 'Verifier changed', rule: RULES.upgrade },
    'upgrade-kept': { stage: 'validated', label: 'Upgrade rule: kept (mode 0 re-validated)', rule: RULES.upgrade },
    'upgrade-dropped': { stage: 'dropped', label: 'Upgrade rule: dropped', rule: RULES.upgrade },
    'upgrade-evicted': { stage: 'dropped', label: 'Upgrade rule: transaction evicted', rule: RULES.upgrade },
    'pool-evicted': { stage: 'dropped', label: 'Evicted by the pool' },
    'included': { stage: 'included', label: e => `Included in block ${e.block}`, rule: RULES.include },
    'proof-step': { stage: 'proving', label: e => `Native ${e.message} proof: ${e.result}`, rule: RULES.aggregate },
    'recursive-verify': { stage: 'proving', label: e => `Recursive proof verify: ${e.result}`, rule: RULES.verdictCache },
    'production-scheduled': { stage: 'proving', label: 'Production proof scheduled', rule: RULES.production },
    'production-proof': { stage: 'proving', label: e => `Production proof: ${e.result}`, rule: RULES.production },
    'production-pass': { stage: 'proving', label: e => `Block ${e.block} production: ${e.result}`, rule: RULES.production },
    'gauges': { stage: 'metrics', label: 'Queue depths' },
  };

  // Every feed event type: the phase it belongs to (null when it is not about one wrapper), whether it occurs on the happy
  // path, its spec section, and a plain-English meaning. This is the step audit shown in the Guide tab.
  const AUDIT = {
    'submitted': ['originate', true, SPEC.wrapper, 'A wrapper entered EIP-8288 validation: over eth_sendProofWrapper ("local"), or after eth/73 retrieval ("eth/73", folded into Validate).'],
    'aggregated': ['originate', true, SPEC.aggregation, 'Background aggregation built a new mode-1 wrapper over the pending pool: proven fresh, extended from the previous aggregate, or reusing a cached proof.'],
    'announcement-received': ['announce', true, SPEC.announce, 'A peer announced the wrapper. Queued for fetching, added as another source, or ignored (already held, recently rejected, pending cache full or the peer\'s share used up).'],
    'wrapper-requested': ['fetch', true, SPEC.request, 'GetProofWrappers sent to an announcer.'],
    'wrapper-received': ['fetch', true, SPEC.response, 'ProofWrappers returned the wrapper, matching the requested hash and the announced length; it is queued for validation.'],
    'wrapper-unavailable': ['fetch', false, SPEC.response, 'The responder omitted the wrapper: unavailable there, not misbehaviour; another announcer is tried.'],
    'wrapper-truncated': ['fetch', false, SPEC.response, 'The response stopped before this wrapper at the 2 MiB soft limit; it is requested again from the same peer.'],
    'fetch-abandoned': ['drop', false, SPEC.security, 'The node gave up fetching: every source tried, the pending lifetime passed, or the recovery deadline.'],
    'validation-started': ['recover', true, SPEC.validation, 'Validation began; the count is how many listed envelopes are missing (0 means none to recover, folded into Validate).'],
    'envelope-requested': ['recover', true, SPEC.propagation, 'GetPooledTransactions for missing envelopes, first to the peer that served the wrapper, then to other announcers.'],
    'envelope-response': ['recover', true, SPEC.propagation, 'PooledTransactions answered; envelopes omitted before the last returned one are unavailable from that peer.'],
    'envelope-recovery': ['recover', true, SPEC.validation, 'Recovery finished: complete, or incomplete (the wrapper is unresolved, not invalid).'],
    'claims-checked': ['validate', true, SPEC.validation, 'The claimed proofs were verified against the declared dependencies while envelopes were being recovered.'],
    'proof-verified': ['validate', true, SPEC.wrapper, 'Full EIP-8288 validation of the wrapper against its resolved transactions.'],
    'verdict-cached': ['validate', false, SPEC.wrapper, 'The same wrapper was validated before under this verifier; the remembered verdict is reused.'],
    'verdict': ['validate', true, SPEC.validation, 'The eth/73 transport\'s conclusion: Valid, Invalid (server penalised), GraceRefused, Refused or Unresolved.'],
    'violation': ['validate', false, SPEC.validation, 'The peer sent invalid or unmatched data and was disconnected.'],
    'admitted': ['admit', true, SPEC.validation, 'Pool admission of one listed transaction: Accepted, already pending, or the pool\'s refusal reason.'],
    'offered': ['relay', true, SPEC.announce, 'Stored with its envelopes in the bounded wrapper-envelope cache and offered to peers.'],
    'announced': ['relay', true, SPEC.announce, 'NewProofWrapperHashes sent to a peer not known to hold the wrapper (again after a reconnect).'],
    'served': ['relay', true, SPEC.response, 'ProofWrappers answered a peer\'s request: served, unavailable, or held but cut by the soft limit or serving budget.'],
    'envelope-served': ['relay', true, SPEC.propagation, 'A listed envelope served from the wrapper-envelope cache after the pool no longer held it.'],
    'included': ['include', true, SPEC.block, 'A listed transaction was included in a canonical block (recorded when this node processed the block).'],
    'upgrade-kept': ['drop', false, SPEC.validation, 'Verifier change: this mode-0 wrapper re-validated and stays offered.'],
    'upgrade-dropped': ['drop', false, SPEC.validation, 'Verifier change: dropped and never announced or served again.'],
    'upgrade-evicted': ['drop', false, SPEC.validation, 'Verifier change: a pending transaction lost its proof coverage and left the pool.'],
    'pool-evicted': ['drop', false, SPEC.validation, 'The pool evicted a listed transaction under its own policy.'],
    'peer-connected': [null, true, SPEC.announce, 'An eth/73 peer connected; offered wrappers are announced to it.'],
    'peer-disconnected': [null, false, SPEC.validation, 'An eth/73 peer disconnected.'],
    'announcements-ignored': [null, false, SPEC.security, 'A NewProofWrapperHashes message over the peer\'s announcement rate was ignored.'],
    'request-expired': [null, false, SPEC.security, 'A GetProofWrappers or GetPooledTransactions request got no response in time.'],
    'verifier-changed': [null, false, SPEC.validation, 'The target fork changed AGGREGATED_VK and the upgrade rule ran.'],
    'proof-step': [null, true, SPEC.aggregation, 'One native proving call (leaf, merge, fold or discard) or a proof-cache hit.'],
    'recursive-verify': [null, true, SPEC.block, 'A recursive STARK verification: cold, or answered from the verification cache.'],
    'production-scheduled': [null, false, SPEC.block, 'Block production found no ready proof and scheduled one off the deadline path.'],
    'production-proof': [null, false, SPEC.block, 'A scheduled production proof finished, was superseded by a later slot, or failed.'],
    'production-pass': [null, true, SPEC.block, 'A block-production pass: its proof was reused, prepared, proven inline, or not ready by the deadline.'],
    'gauges': [null, true, SPEC.security, 'Once a second: peers, pending announcements, validation queue, offered wrappers and charged bytes.'],
  };

  /** Phase of an event within a wrapper's history; eth/73 admission entry and an empty recovery fold into Validate. */
  function phaseOf(e) {
    if (e.type === 'submitted' && e.message === 'eth/73') return 'validate';
    if (e.type === 'validation-started' && !(e.count > 0)) return 'validate';
    if (e.type === 'announcement-received' && (e.result || '').startsWith('dropped')) return 'announce';
    return AUDIT[e.type]?.[0] ?? null;
  }

  const value = (v, e) => typeof v === 'function' ? v(e) : v;

  function describe(e) {
    const t = TYPES[e.type] || { stage: 'other', label: e.type };
    return { stage: value(t.stage, e), label: value(t.label, e), message: value(t.message, e) || e.message, rule: value(t.rule, e), wire: t.wire };
  }

  const api = { STAGES, STAGE_INDEX, PHASES, PHASE_INDEX, SPEC, AUDIT, RULES, TYPES, describe, phaseOf };
  root.PWV = Object.assign(root.PWV || {}, { stages: api });
  if (typeof module !== 'undefined') module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
