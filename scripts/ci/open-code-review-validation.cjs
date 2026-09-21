// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { Repository, sourcePath } = require('./open-code-review-context.cjs');

const hash = value => createHash('sha256').update(JSON.stringify(value)).digest('hex');
const read = (directory, name) => JSON.parse(fs.readFileSync(path.join(directory, name + '.json')));
const write = (directory, name, value) => fs.writeFileSync(path.join(directory, name + '.json'), JSON.stringify(value, null, 2));
const string = (value, max = 5000) => typeof value === 'string' && value.trim().length > 0 && value.length <= max;
const object = properties => ({ type: 'object', properties, required: Object.keys(properties), additionalProperties: false });
const text = { type: 'string' };
const explanation = { type: 'string', maxLength: 1200, description: 'Brief evidence-based explanation, at most 1200 characters. Do not include private reasoning.' };
const integer = { type: 'integer' };
const array = items => ({ type: 'array', items });
const enumeration = values => ({ type: 'string', enum: values });
const evidenceSchema = object({ path: text, snapshot: enumeration(['head', 'base']), start_line: integer, end_line: integer });
const findingSchema = object({ path: text, start_line: integer, end_line: integer, content: text,
  severity: enumeration(['critical', 'high', 'medium', 'low']),
  category: enumeration(['bug', 'security', 'performance', 'maintainability', 'test', 'style', 'documentation', 'other']) });
const candidateSchema = object({ finding: findingSchema, evidence: array(evidenceSchema) });
const toolsFor = phase => [
  ['read_source', 'Read a regular source file at head or merge-base. Large ranges return at most 160 lines and next_line for continuation. Never reads the working tree.',
    object({ path: text, snapshot: enumeration(['head', 'base']), start_line: integer, end_line: integer })],
  ['search_source', 'Find a literal source string (3 to 100 characters, no regex); prioritizes other writers and initialization/recovery paths. Results are bounded.',
    object({ symbol: text, snapshot: enumeration(['head', 'base']) })],
  ['read_diff', 'Read a changed file patch, starting at offset (characters), up to 16000 characters per call.',
    object({ path: text, offset: integer })],
  ['submit_review', 'Finish this pass. All checks or candidate IDs must be accounted for exactly once.', phase === 'discovery'
    ? object({ checks: array(object({ id: text, status: enumeration(['checked', 'not_applicable', 'unverified']),
      explanation, evidence: array(evidenceSchema) })), findings: array(candidateSchema) })
    : object({ decisions: array(object({ id: text, verdict: enumeration(['confirmed', 'rejected', 'unverified']),
      explanation, evidence: array(evidenceSchema) })) })],
].map(([name, description, parameters]) => ({ type: 'function', function: { name, description, parameters } }));

class ValidationError extends Error {}
const fail = message => { throw new ValidationError(message); };

// Never echo provider errors: they can contain the configured model, endpoint, or request.
async function completion(config, token, body, timeout, fetcher = fetch) {
  try {
    const response = await fetcher(config.providers.litellm.url + '/chat/completions', {
      method: 'POST', redirect: 'error', signal: AbortSignal.timeout(timeout),
      headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' },
      body: JSON.stringify({ ...config.providers.litellm.extra_body, ...body, model: config.model, stream: false }),
    });
    if (!response.ok) fail('Validation gateway request failed (HTTP ' + response.status + ')');
    const chunks = [];
    let size = 0;
    for await (const chunk of response.body) {
      size += chunk.length;
      if (size > 2 * 1024 * 1024) fail('Validation gateway response exceeded the size limit');
      chunks.push(chunk);
    }
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } catch (error) {
    if (error instanceof ValidationError) throw error;
    fail('Validation gateway request failed or timed out');
  }
}

function evidence(repository, refs, required) {
  if (!Array.isArray(refs) || refs.length > 12 || (required && refs.length === 0) ||
      !refs.every(ref => repository.hasEvidence(ref))) {
    const error = new ValidationError('Submission cites missing or unread source evidence');
    error.unread = Array.isArray(refs) ? refs.filter(ref => !repository.hasEvidence(ref)).slice(0, 12) : [];
    throw error;
  }
}

function finding(repository, changes, value) {
  if (!value || !sourcePath(value.path) || !Number.isSafeInteger(value.start_line) ||
      !Number.isSafeInteger(value.end_line) || value.start_line < 1 || value.end_line < value.start_line ||
      value.end_line - value.start_line > 10 || !string(value.content) ||
      !findingSchema.properties.severity.enum.includes(value.severity) ||
      !findingSchema.properties.category.enum.includes(value.category) ||
      !changes.some(change => change.path === value.path && [...change.ranges, ...change.deletion_ranges].some(range =>
        value.start_line >= range.start && value.start_line <= range.end)) ||
      !repository.hasEvidence({ path: value.path, snapshot: 'head', start_line: value.start_line, end_line: value.end_line })) {
    fail('Finding must cite a read source range anchored in changed code');
  }
  // Do not forward arbitrary model fields (in particular hidden reasoning).
  return Object.fromEntries(Object.keys(findingSchema.properties).map(key => [key, value[key]]));
}

async function runPass({ phase, repository, context, candidates, prompt, ask, usage, budget, deadline,
  maxRounds = 10, maxOutputTokens = 32768 }) {
  const changes = repository.changes();
  const tools = toolsFor(phase);
  const suppliedContext = phase === 'validation' ? { ...context, obligations: undefined } : context;
  const messages = [
    { role: 'system', content: prompt + '\nCurrent phase: ' + phase + '. You have at most ' + maxRounds + ' requests; batch independent source reads (up to eight tools per request). Reserve the last request for submit_review.' +
      (phase === 'validation' ? ' Submit only candidate decisions; do not repeat discovery checks.' : '') },
    { role: 'user', content: JSON.stringify({ context: suppliedContext, ...(phase === 'validation' ? { candidates } : {}) }) },
  ];
  let outputLimitRetries = 0;
  // Two bounded repairs accommodate malformed JSON/citations, including one last evidence read.
  for (let turn = 0; turn < maxRounds + 2; turn++) {
    if (Date.now() >= deadline) fail('Validation time limit reached');
    if (usage.input + usage.output >= budget) fail('Validation token budget exhausted');
    if (turn === maxRounds - 3) messages.push({ role: 'user', content: 'Three requests remain. Finish the assigned checks and prepare submit_review. Use unverified for unresolved checks; do not silently omit them.' });
    if (turn === maxRounds - 1 || turn === maxRounds + 1) messages.push({ role: 'user', content: 'Submit the review now. Mark unresolved checks/candidates unverified.' });
    // Some reasoning providers reject forced tool_choice; keep their default selection.
    const response = await ask({ messages, tools: turn === maxRounds - 1 || turn === maxRounds + 1
      ? tools.filter(tool => tool.function.name === 'submit_review') : tools, max_tokens: maxOutputTokens },
    Math.min(120000, deadline - Date.now()));
    const counted = response.usage;
    if (!Number.isSafeInteger(counted?.prompt_tokens) || !Number.isSafeInteger(counted?.completion_tokens) ||
        counted.prompt_tokens < 0 || counted.completion_tokens < 0) fail('Validation gateway did not report token usage');
    usage.input += counted.prompt_tokens;
    usage.output += counted.completion_tokens;
    usage.requests++;
    if (usage.input + usage.output > budget) fail('Validation token budget exhausted');
    const message = response.choices?.[0]?.message;
    const calls = message?.tool_calls;
    if (response.choices?.[0]?.finish_reason === 'length') {
      if (outputLimitRetries++ || turn === maxRounds + 1) fail('Validation response reached its output limit');
      usage.output_limit_retries = (usage.output_limit_retries || 0) + 1;
      // A truncated response may contain partial tool calls. Do not execute or replay any of it.
      messages.push({ role: 'user', content: 'The response was truncated. Retry with concise tool arguments and explanations. Use focused source reads; submit unresolved items as unverified. Do not repeat a long analysis or add a prose summary.' });
      continue;
    }
    if (!Array.isArray(calls) || calls.length === 0 || calls.length > 8 ||
        calls.some(call => !string(call.id, 200) || call.type !== 'function' || !string(call.function?.arguments, 100000)) ||
        new Set(calls.map(call => call.id)).size !== calls.length) fail('Validation did not return valid tool calls');
    // Preserve reasoning continuity for compatible providers, without persisting or publishing it.
    messages.push({ role: 'assistant', content: message.content ?? null, tool_calls: calls,
      ...(typeof message.reasoning_content === 'string' ? { reasoning_content: message.reasoning_content } : {}) });
    let submitted;
    for (const call of calls) {
      let result;
      try {
        const args = JSON.parse(call.function.arguments);
        switch (call.function.name) {
          case 'read_source': result = repository.read(args); break;
          case 'search_source': result = repository.search(args); break;
          case 'read_diff': {
            const change = changes.find(change => change.path === args.path);
            if (!change || !Number.isSafeInteger(args.offset) || args.offset < 0) throw new Error('Invalid patch');
            result = { path: change.path, patch: change.patch.slice(args.offset, args.offset + 16000),
              next_offset: args.offset + 16000 < change.patch.length ? args.offset + 16000 : null };
            break;
          }
          case 'submit_review': {
            if (calls.length !== 1) fail('Submit the review separately from source tools');
            if (phase === 'discovery') {
              if (!Array.isArray(args.checks) || !Array.isArray(args.findings) || args.findings.length > 8 ||
                  JSON.stringify(args.checks.map(check => check.id).sort()) !==
                  JSON.stringify(context.obligations.map(check => check.id).sort())) fail('Discovery did not account for every assigned check');
              for (const check of args.checks) {
                if (!['checked', 'not_applicable', 'unverified'].includes(check.status) || !string(check.explanation, 1200)) fail('Invalid discovery check or explanation exceeds 1200 characters');
                if (check.id === 'changed_behavior' && check.status === 'not_applicable') fail('Changed behavior must be investigated');
                evidence(repository, check.evidence, check.status !== 'unverified');
              }
              for (const candidate of args.findings) {
                candidate.finding = finding(repository, changes, candidate.finding);
                evidence(repository, candidate.evidence, true);
              }
            } else {
              if (!Array.isArray(args.decisions) || JSON.stringify(args.decisions.map(decision => decision.id).sort()) !==
                  JSON.stringify(candidates.map(candidate => candidate.id).sort())) fail('Validation did not account for every candidate');
              for (const decision of args.decisions) {
                if (!['confirmed', 'rejected', 'unverified'].includes(decision.verdict) || !string(decision.explanation, 1200)) fail('Invalid candidate decision or explanation exceeds 1200 characters');
                evidence(repository, decision.evidence, decision.verdict !== 'unverified');
                // Judge the candidate as written. A validator rewrite would introduce unreviewed claims.
                if (decision.verdict === 'confirmed') decision.finding = finding(repository, changes,
                  candidates.find(candidate => candidate.id === decision.id).finding);
                else decision.finding = null;
              }
            }
            submitted = args;
            result = { accepted: true };
            break;
          }
          default: throw new Error('Unknown source tool');
        }
      } catch (error) {
        // Submission errors are safe constants. Filesystem, git and JSON errors are not.
        result = { error: error instanceof ValidationError ? error.message : error instanceof SyntaxError
          ? 'Tool arguments must be valid JSON. Escape quotes inside strings and keep explanations brief.'
          : 'Invalid or unavailable source request; use a valid source path, symbol and bounded range.' };
        if (error.unread) {
          result.unread = error.unread;
          result.new_source = [];
          // Serve bounded missing citations, then require a NEW decision after the model sees them.
          // This never accepts the original submission using evidence it had not yet received.
          for (const ref of error.unread.slice(0, 4)) {
            try { result.new_source.push(repository.read(ref)); } catch { /* Unavailable citations remain unresolved. */ }
          }
          if (result.new_source.length) result.instruction = 'Review the newly supplied source before resubmitting. Correct or withdraw unsupported statements; unresolved items must be unverified.';
        }
      }
      messages.push({ role: 'tool', tool_call_id: call.id, content: JSON.stringify(result) });
    }
    if (submitted) return submitted;
  }
  fail('Validation request limit reached before a valid submission');
}

async function validate({ root, directory, token = process.env.OCR_LLM_TOKEN,
  budget = Number(process.env.OCR_VALIDATION_TOKEN_BUDGET || 1000000),
  maxOutputTokens = Number(process.env.OCR_VALIDATION_MAX_OUTPUT_TOKENS || 32768), ask, duration = 8 * 60000 }) {
  const usage = { input: 0, output: 0, requests: 0 };
  const report = { schema_version: 'nethermind.ocr-validation/v1', complete: false, usage };
  fs.rmSync(path.join(directory, 'validated-result.json'), { force: true });
  try {
    if (![500000, 1000000, 2000000].includes(budget)) fail('Unsupported validation token budget');
    if (![8192, 16384, 32768, 65536].includes(maxOutputTokens)) fail('Unsupported validation response token limit');
    const target = read(directory, 'target');
    const context = read(directory, 'context');
    let primary = {};
    try { primary = read(directory, 'result'); } catch { /* Independent discovery can still review captured source. */ }
    const config = read(directory, 'validation-config');
    const primaryConfig = read(directory, 'config');
    const exitCode = Number(fs.readFileSync(path.join(directory, 'exit-code.txt'), 'utf8').trim());
    const { assess } = require('./open-code-review.cjs');
    const primaryAssessment = assess(primary, read(directory, 'preview'), target, exitCode, primaryConfig.model);
    if (context.head !== target.head || context.base !== target.merge_base || context.pr.head !== target.head ||
        context.pr.base !== target.base) fail('Validation context does not match captured commits');
    if (!ask && !token) fail('Validation API key is unavailable');
    Object.assign(report, { head: target.head, base: target.merge_base, model: config.model,
      context_hash: hash(context), primary_hash: hash(primary) });
    const prompt = ['.github/open-code-review/validate.md', '.github/open-code-review/review.md',
      '.agents/rules/robustness.md', '.agents/rules/test-infrastructure.md']
      .map(file => fs.readFileSync(path.join(root, file), 'utf8')).join('\n\n');
    const repository = new Repository(root, target);
    // Re-read every initial excerpt from git before allowing it to count as evidence.
    for (const source of context.related) {
      if (repository.read(source).content !== source.content) fail('Initial source evidence does not match captured commit');
    }
    const common = { repository, context, prompt, usage, budget, maxOutputTokens, deadline: Date.now() + duration,
      ask: ask || ((body, timeout) => completion(config, token, body, timeout)) };
    const discovery = await runPass({ ...common, phase: 'discovery' });
    report.checks = discovery.checks;
    const manifest = primary.manifest;
    const primaryBound = manifest?.schema_version === 'ocr.run-manifest/v1' &&
      manifest.input?.resolved_head === target.head && manifest.input?.resolved_base === target.merge_base &&
      manifest.execution?.model === primaryConfig.model && Array.isArray(primary.comments);
    const changes = repository.changes();
    const validCandidate = value => value && sourcePath(value.path) &&
      Number.isSafeInteger(value.start_line) && Number.isSafeInteger(value.end_line) &&
      value.start_line >= 1 && value.end_line >= value.start_line && value.end_line - value.start_line <= 10 &&
      string(value.content) && findingSchema.properties.severity.enum.includes(value.severity) &&
      findingSchema.properties.category.enum.includes(value.category) &&
      changes.some(change => change.path === value.path && [...change.ranges, ...change.deletion_ranges]
        .some(range => value.start_line >= range.start && value.start_line <= range.end));
    const primaryCandidates = primaryBound ? primary.comments.filter(validCandidate).map(finding => ({ finding })) : [];
    report.primary_complete = primaryAssessment.complete;
    report.primary_candidates = primaryCandidates.length;
    const candidates = [...primaryCandidates, ...discovery.findings]
      .map((candidate, index) => ({ id: hash([index, candidate.finding]).slice(0, 20),
        finding: Object.fromEntries(Object.keys(findingSchema.properties).map(key => [key, candidate.finding[key]])) }));
    if (candidates.length > 40) fail('Too many candidate findings for bounded validation');
    // A fresh transcript must independently read the evidence, rather than inherit discovery's tool history.
    repository.served = context.related.map(({ path, snapshot, start_line, end_line }) => ({ path, snapshot, start_line, end_line }));
    const validated = candidates.length ? await runPass({ ...common, phase: 'validation', candidates }) : { decisions: [] };
    report.decisions = validated.decisions;
    report.discovered = discovery.findings.length;
    report.complete = !report.checks.some(check => check.status === 'unverified') &&
      !report.decisions.some(decision => decision.verdict === 'unverified');
    if (!report.complete) fail('Some assigned checks or candidate findings remain unverified');
    const privateValues = [token, config.model, config.providers.litellm.url,
      primaryConfig.model, primaryConfig.providers.litellm.url].filter(Boolean);
    const comments = report.decisions.filter(decision => decision.verdict === 'confirmed').map(decision => ({
      ...decision.finding,
      content: privateValues.reduce((content, value) => content.replaceAll(value, '[private setting]'), decision.finding.content),
    }));
    // Upstream renders message/warnings verbatim. Never forward raw gateway diagnostics or reasoning.
    const result = { summary: primary.summary, manifest: primary.manifest, comments,
      message: 'No confirmed findings in this bounded source review. Human review is still required.',
      warnings: primary.warnings?.length ? ['OCR reported diagnostics; raw details are retained only on the runner.'] : [],
    };
    report.result_hash = hash(result);
    write(directory, 'validated-result', result);
  } catch (error) {
    report.complete = false;
    report.reason = error instanceof ValidationError ? error.message : 'Validation could not complete safely';
  }
  write(directory, 'validation', report);
  return report;
}

function verifyValidation(directory, target, primary) {
  try {
    const report = read(directory, 'validation');
    const result = read(directory, 'validated-result');
    const context = read(directory, 'context');
    const config = read(directory, 'validation-config');
    if (report.schema_version !== 'nethermind.ocr-validation/v1' || report.complete !== true ||
        report.head !== target.head || report.base !== target.merge_base || report.model !== config.model ||
        report.primary_hash !== hash(primary) || report.context_hash !== hash(context) ||
        report.result_hash !== hash(result) || !Array.isArray(result.comments) ||
        report.checks.some(check => check.status === 'unverified') ||
        report.decisions.some(decision => decision.verdict === 'unverified')) return null;
    return { report, result, context };
  } catch { return null; }
}

module.exports = { hash, completion, runPass, validate, verifyValidation };

if (require.main === module) {
  if (process.argv.length !== 4) throw new Error('Usage: node open-code-review-validation.cjs <trusted-root> <output-directory>');
  validate({ root: path.resolve(process.argv[2]), directory: path.resolve(process.argv[3]) })
    .then(report => { console.log(report.complete ? 'Source validation completed.' : 'Source validation incomplete: ' + report.reason); })
    .catch(() => { console.error('Source validation could not write its completion report.'); process.exitCode = 1; });
}
