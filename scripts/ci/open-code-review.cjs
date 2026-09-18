// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

'use strict';

const fs = require('node:fs');
const path = require('node:path');

const MARKER = '<!-- ocr-summary -->';

function configure(defaults, environment) {
  const config = structuredClone(defaults);
  config.model = environment.OCR_MODEL?.trim();
  if (!config.model) throw new Error('Set the required OCR_MODEL Actions variable');
  if (/[\x00-\x1f\x7f]/.test(config.model)) throw new Error('OCR_MODEL must be a single-line model identifier');
  const provider = config.providers.litellm;
  const base = environment.OCR_API_BASE_URL?.trim();
  if (!base) throw new Error('Set the required OCR_API_BASE_URL Actions variable');
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
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function assess(result, preview, target, exitCode, expectedModel) {
  const manifest = result.manifest;
  const coverage = manifest?.coverage;
  const reasons = [];
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
    'Advisory review of `' + target.head + '` with `' + quote(expectedModel || 'unconfigured') + '`.',
    'Reviewed ' + ((coverage?.completed?.length || 0) + (coverage?.reused?.length || 0)) +
      ' / ' + (coverage?.selected?.length || 0) + ' selected files; excluded ' + excluded.length + '.',
    'Tokens: ' + (summary.input_tokens ?? 'unknown') + ' input, ' +
      (summary.output_tokens ?? 'unknown') + ' output. Elapsed: ' + quote(summary.elapsed || 'unknown') + '.',
    'Billed cost: see the dedicated LiteLLM key; OCR does not report the proxy charge.',
  ];
  if (complete) lines.push('Findings: ' + result.comments.length + '. Human review is still required.');
  else lines.push('', ...reasons.map(reason => '- ' + reason),
    '', 'This run cannot establish that the PR is clean. Findings are retained in the run artifacts.');
  if (excluded.length) {
    lines.push('', '<details><summary>Excluded files</summary>', '',
      ...excluded.slice(0, 100).map(item => '- `' + quote(item.path) + '`: ' + quote(item.exclude_reason)),
      ...(excluded.length > 100 ? ['- See the selection artifact for the remaining files.'] : []),
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
  const report = assess(result, preview, target, exitCode, config.model);
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
    core.warning('PR closed, changed, or returned to draft during review; retaining artifacts without posting.');
    core.setOutput('stale', 'true');
    return report;
  }

  // Intercept only OCR's own summary to include coverage and usage on every path,
  // including its "no comments" path. Incomplete runs never reach its green verdict.
  const decorate = method => async args => method({
    ...args,
    body: args.body?.includes(MARKER)
      ? args.body.replace(MARKER, MARKER + '\n' + report.markdown + '\n\n---\n')
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
      resultPath: path.join(directory, 'result.json'),
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
