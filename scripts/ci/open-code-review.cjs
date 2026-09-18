// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

'use strict';

const fs = require('node:fs');
const path = require('node:path');

const MARKER = '<!-- ocr-summary -->';

function configure(defaults, environment) {
  const config = structuredClone(defaults);
  config.model = environment.OCR_MODEL?.trim();
  if (!config.model) throw new Error('Set the required OCR_MODEL Actions secret');
  if (/[\x00-\x1f\x7f]/.test(config.model)) throw new Error('OCR_MODEL must be a single-line model identifier');
  const provider = config.providers.litellm;
  const base = environment.OCR_API_BASE_URL?.trim();
  if (!base) throw new Error('Set the required OCR_API_BASE_URL Actions secret');
  let url;
  try { url = new URL(base); } catch { throw new Error('OCR_API_BASE_URL must be an HTTPS API base URL'); }
  if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash) {
    throw new Error('OCR_API_BASE_URL must use HTTPS without credentials, query parameters, or a fragment');
  }
  provider.url = url.href.replace(/\/+$/, '');
  provider.extra_body = {};
  const extra = environment.OCR_EXTRA_BODY?.trim();
  if (extra) {
    try { provider.extra_body = JSON.parse(extra); } catch { throw new Error('OCR_EXTRA_BODY must be a JSON object'); }
    if (!provider.extra_body || Array.isArray(provider.extra_body) || typeof provider.extra_body !== 'object') {
      throw new Error('OCR_EXTRA_BODY must be a JSON object');
    }
    if (['model', 'messages', 'tools'].some(key => Object.hasOwn(provider.extra_body, key))) {
      throw new Error('OCR_EXTRA_BODY cannot override model, messages, or tools');
    }
  }
  return config;
}

function prepare(root, output, environment = process.env) {
  fs.mkdirSync(output, { recursive: true });
  const read = file => fs.readFileSync(path.join(root, file), 'utf8');
  const runtime = configure(JSON.parse(read('.github/open-code-review/config.json')), environment);
  const common = read('.github/open-code-review/review.md');
  const rules = names => [common, ...names.map(name =>
    'Project rules: ' + name + '\n' + read('.agents/rules/' + name + '.md')
  )].join('\n\n');
  const csharp = ['coding-style', 'robustness', 'performance', 'di-patterns'];
  const config = {
    // include overrides OCR's extension and test exclusions; it is not an allowlist.
    include: ['**/*.{cs,csproj,props,targets,sln,slnx}', '**/*{test,spec}*.*', '**/*Test*/**'],
    exclude: ['src/tests/**', 'src/bench_precompiles/**', '**/bin/**', '**/obj/**'],
    rules: [
      { path: '**/*{Test,Benchmark}*/**/*.cs', rule: rules([...csharp, 'test-infrastructure']) },
      { path: '**/*.cs', rule: rules(csharp) },
      { path: '**/*.{csproj,props,targets,sln,slnx}', rule: rules(['package-management']) },
      { path: '.github/**/*.{yml,yaml}', rule: rules(['github-workflows']) },
      { path: '**', rule: common, merge_system_rule: true },
    ],
  };
  fs.writeFileSync(path.join(output, 'rules.json'), JSON.stringify(config, null, 2) + '\n');
  fs.writeFileSync(path.join(output, 'config.json'), JSON.stringify(runtime, null, 2) + '\n');
  const validation = configure(JSON.parse(read('.github/open-code-review/config.json')), {
    ...environment,
    OCR_MODEL: environment.OCR_VALIDATION_MODEL?.trim() || environment.OCR_MODEL,
    OCR_EXTRA_BODY: environment.OCR_VALIDATION_EXTRA_BODY?.trim() ||
      (environment.OCR_VALIDATION_MODEL?.trim() ? '{}' : environment.OCR_EXTRA_BODY),
  });
  fs.writeFileSync(path.join(output, 'validation-config.json'), JSON.stringify(validation, null, 2) + '\n');
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function assess(result, preview, target, exitCode, expectedModel, additionalReasons = []) {
  const manifest = result.manifest;
  const coverage = manifest?.coverage;
  const reasons = [...additionalReasons];
  if (exitCode !== 0) reasons.push('OCR exited with code ' + exitCode);
  if (manifest?.schema_version !== 'ocr.run-manifest/v1') reasons.push('missing or unsupported coverage manifest');
  if (manifest?.input?.resolved_head !== target.head) reasons.push('reviewed head does not match the requested commit');
  if (manifest?.input?.resolved_base !== target.merge_base) reasons.push('reviewed base does not match the requested range');
  if (!expectedModel || manifest?.execution?.model !== expectedModel) reasons.push('unexpected model in the review manifest');
  if (manifest?.terminal_state !== 'complete') reasons.push('review state: ' + (manifest?.terminal_state || 'unavailable'));
  if (manifest?.run_failure) reasons.push('OCR reported a run failure');
  if (result.summary?.budget_exceeded) reasons.push('token budget exhausted');
  const sets = ['selected', 'completed', 'reused', 'failed', 'waived'];
  if (!sets.every(key => Array.isArray(coverage?.[key]))) reasons.push('invalid coverage sets');
  else {
    if (coverage.failed.length || coverage.waived.length) reasons.push('some selected files were not reviewed');
    const selected = coverage.selected.map(item => item.item_id).sort();
    const covered = [...coverage.completed, ...coverage.reused].map(item => item.item_id).sort();
    if (!selected.length) reasons.push('no files reviewed');
    if (new Set(selected).size !== selected.length || JSON.stringify(selected) !== JSON.stringify(covered)) {
      reasons.push('coverage does not account for every selected file');
    }
  }
  if (!Array.isArray(preview.files)) reasons.push('selection preview is unavailable');
  else {
    const expected = preview.files.filter(item => item.will_review).map(item => item.path).sort();
    const selected = (coverage?.selected || []).map(item => item.path).sort();
    if (JSON.stringify(expected) !== JSON.stringify(selected)) reasons.push('coverage differs from the selection preview');
    if (preview.files.some(item => item.exclude_reason === 'too_large')) reasons.push('a changed file exceeded the prompt limit');
  }
  if (!Array.isArray(result.comments)) reasons.push('findings are missing or malformed');
  const complete = reasons.length === 0;
  const summary = result.summary || {};
  const excluded = (preview.files || []).filter(item => !item.will_review);
  const quote = text => String(text).replace(/[<>&\x00-\x1f`]/g, '?');
  const lines = [
    '**AI code review: ' + (complete ? 'completed selected files' : 'INCOMPLETE') + '**',
    '',
    'Advisory review of `' + target.head + '`.',
    'Reviewed ' + ((coverage?.completed?.length || 0) + (coverage?.reused?.length || 0)) +
      ' / ' + (coverage?.selected?.length || 0) + ' selected files; excluded ' + excluded.length + '.',
    'Static source review only; this action does not build the PR or run tests. File coverage does not measure review depth.',
    'Primary tokens: ' + (summary.input_tokens ?? 'unknown') + ' input, ' +
      (summary.output_tokens ?? 'unknown') + ' output. Elapsed: ' + quote(summary.elapsed || 'unknown') + '.',
    'Billed cost: see the dedicated LiteLLM key; OCR does not report the proxy charge.',
  ];
  if (complete) lines.push('Findings: ' + result.comments.length + '. Human review is still required.');
  else lines.push('', ...reasons.map(reason => '- ' + reason),
    '', 'This run cannot establish that the PR is clean. Findings from incomplete reviews are not published.');
  if (excluded.length) {
    lines.push('', '<details><summary>Excluded files</summary>', '',
      ...excluded.slice(0, 100).map(item => '- `' + quote(item.path) + '`: ' + quote(item.exclude_reason)),
      ...(excluded.length > 100 ? ['- ' + (excluded.length - 100) + ' additional files excluded.'] : []),
      '', '</details>');
  }
  return { complete, reasons, markdown: lines.join('\n') };
}

async function publish({ github, context, core, directory, enabled, postReview }) {
  const target = readJson(path.join(directory, 'target.json'));
  const preview = readJson(path.join(directory, 'preview.json'));
  const config = readJson(path.join(directory, 'config.json'));
  let result = {};
  try { result = readJson(path.join(directory, 'result.json')); } catch { /* Report an incomplete run below. */ }
  const exitCode = Number(fs.readFileSync(path.join(directory, 'exit-code.txt'), 'utf8').trim());
  const { verifyValidation } = require('./open-code-review-validation.cjs');
  const validated = verifyValidation(directory, target, result);
  const report = assess(validated?.result || result, preview, target, exitCode, config.model,
    validated ? [] : ['independent source validation is missing, incomplete, or does not match this review']);
  if (validated) {
    const { report: validation, context: evidence } = validated;
    report.markdown += '\n\nIndependent source checks: ' + validation.checks.filter(check => check.status === 'checked').length +
      ' checked, ' + validation.checks.filter(check => check.status === 'not_applicable').length + ' not applicable.' +
      '\nCandidate validation: ' + validation.decisions.filter(decision => decision.verdict === 'confirmed').length +
      ' confirmed, ' + validation.decisions.filter(decision => decision.verdict === 'rejected').length +
      ' rejected; ' + validation.discovered + ' candidates from independent discovery.' +
      '\nAdditional validation tokens: ' + validation.usage.input + ' input, ' + validation.usage.output + ' output.' +
      '\nSource checks are bounded and model-assessed; they are not proof of correctness.';
    if (evidence.truncated) report.markdown += '\nInitial source context was truncated; additional reads were available to the reviewer.';
  } else {
    try {
      const validation = readJson(path.join(directory, 'validation.json'));
      if (validation.head === target.head && validation.base === target.merge_base &&
          [validation.usage?.input, validation.usage?.output].every(value => Number.isSafeInteger(value) && value >= 0)) {
        report.markdown += '\n\nReported validation tokens before completion failed: ' + validation.usage.input +
          ' input, ' + validation.usage.output + ' output.';
        const publicReasons = ['Validation response reached its output limit', 'Validation token budget exhausted',
          'Validation time limit reached', 'Validation request limit reached before a valid submission',
          'Some assigned checks or candidate findings remain unverified'];
        if (publicReasons.includes(validation.reason)) report.markdown += '\nValidation stopped: ' + validation.reason + '.';
      }
    } catch { /* Missing validation is already an incomplete outcome. */ }
  }
  let pr;
  try { pr = readJson(path.join(directory, 'pr-context.json')); } catch { /* Disclosure below. */ }
  if (pr?.head === target.head && pr?.base === target.base && Array.isArray(pr.checks) && Array.isArray(pr.statuses)) {
    const states = [...pr.checks.map(check => check.status !== 'completed' ? 'pending' : check.conclusion), ...pr.statuses.map(status => status.state)];
    const passed = states.filter(state => state === 'success').length;
    const failed = states.filter(state => ['failure', 'error', 'timed_out', 'action_required', 'startup_failure'].includes(state)).length;
    const pending = states.filter(state => ['pending', 'queued', 'in_progress', 'waiting'].includes(state)).length;
    report.markdown += '\n\nCI snapshot at captured head: ' + passed + ' successful, ' + failed + ' failed, ' + pending +
      ' pending, ' + (states.length - passed - failed - pending) + ' other/neutral/skipped checks.' +
      (pr.unavailable?.length ? ' Some CI or discussion context was unavailable.' : '') +
      ' This is a bounded snapshot taken at review start; it does not establish which regression scenarios ran.';
  } else report.markdown += '\n\nCI status at the captured head was unavailable.';
  const runUrl = context.serverUrl + '/' + context.repo.owner + '/' + context.repo.repo +
    '/actions/runs/' + context.runId;
  report.markdown += '\n\n[Review run and artifacts](' + runUrl + ')';
  fs.writeFileSync(path.join(directory, 'summary.md'), report.markdown + '\n');
  await core.summary.addRaw(report.markdown).write();
  core.setOutput('complete', String(report.complete));
  if (!enabled) return report;

  const { data: current } = await github.rest.pulls.get({
    ...context.repo, pull_number: target.number,
  });
  if (current.state !== 'open' || current.head.sha !== target.head || current.base.sha !== target.base ||
      (target.automatic && current.draft)) {
    core.warning('PR closed, changed, or returned to draft during review; retaining the summary without posting.');
    core.setOutput('stale', 'true');
    return report;
  }

  // Intercept only OCR's own summary to include coverage and usage on every path,
  // including its "no comments" path. Incomplete runs never reach its green verdict.
  const decorate = method => async args => method({
    ...args,
    body: args.body?.includes(MARKER)
      ? args.body.replace(MARKER, MARKER + '\n' + report.markdown + '\n\n---\n')
        .replace(/^⚠️ GitHub could not post this as an inline comment: (Routed to summary \([^\r\n]*\))$/gm, 'ℹ️ $1.')
      : args.body,
  });
  if (report.complete) {
    const posting = {};
    const client = {
      ...github,
      rest: { ...github.rest, issues: {
        ...github.rest.issues,
        // Upstream identifies summaries by marker alone. Preserve pagination
        // lengths while hiding markers in comments the workflow does not own.
        listComments: async args => {
          const response = await github.rest.issues.listComments(args);
          return { ...response, data: response.data.map(comment =>
            comment.user?.login === 'github-actions[bot]' ? comment : {
              ...comment, body: (comment.body || '').replaceAll(MARKER, ''),
            }) };
        },
        createComment: decorate(github.rest.issues.createComment),
        updateComment: decorate(github.rest.issues.updateComment),
      } },
    };
    await postReview({
      github: client, context, core: {
        ...core,
        setOutput: (key, value) => { posting[key] = value; core.setOutput(key, value); },
      }, fs, prNumber: target.number,
      resultPath: path.join(directory, 'validated-result.json'),
      stderrPath: path.join(directory, 'stderr.log'),
      stickySummary: true, incremental: true, routeSeverityBelow: 'low',
      routeCategories: 'style,documentation', resolveOutdated: 'false',
    });
    if (!posting.summary_comment_url || Number(posting.comments_failed) > 0) {
      throw new Error('Review publication did not finish; see artifacts and any posted summary.');
    }
  } else {
    const comments = await github.paginate(github.rest.issues.listComments, {
      ...context.repo, issue_number: target.number, per_page: 100,
    });
    const existing = comments.findLast(comment => comment.user?.login === 'github-actions[bot]' &&
      comment.body?.includes(MARKER));
    const body = MARKER + '\n' + report.markdown;
    if (existing) await github.rest.issues.updateComment({ ...context.repo, comment_id: existing.id, body });
    else await github.rest.issues.createComment({ ...context.repo, issue_number: target.number, body });
  }
  return report;
}

module.exports = { configure, prepare, assess, publish };

if (require.main === module) {
  if (process.argv[2] !== 'prepare' || process.argv.length !== 5) {
    throw new Error('Usage: node open-code-review.cjs prepare <trusted-repository> <output-directory>');
  }
  prepare(path.resolve(process.argv[3]), path.resolve(process.argv[4]));
}
