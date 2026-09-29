// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { test } = require('node:test');
const { configure, assess, prepare, publish } = require('./open-code-review.cjs');
const defaults = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../../.github/open-code-review/config.json')));
const settings = { OCR_MODEL: 'test-model', OCR_API_BASE_URL: 'https://gateway.example/v1' };

function commandFixture() {
  const workflow = fs.readFileSync(path.resolve(__dirname, '../../.github/workflows/open-code-review.yml'), 'utf8');
  const request = workflow.split('  request:\n')[1].split('\n  review:')[0];
  const script = request.split('          script: |\n')[1].replace(/^            /gm, '');
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  const execute = new AsyncFunction('github', 'context', 'core', script);
  const context = {
    eventName: 'issue_comment', actor: 'rerun-actor',
    repo: { owner: 'NethermindEth', repo: 'nethermind' },
    payload: {
      action: 'created',
      issue: { number: 123, state: 'open', pull_request: {} },
      comment: { body: '/ocr review', author_association: 'MEMBER', user: { login: 'comment-author', type: 'User' } },
    },
  };
  const outputs = {};
  const calls = [];
  const permission = { permission: 'write', role_name: 'maintain' };
  const github = { rest: { repos: { getCollaboratorPermissionLevel: async args => {
    calls.push(args);
    if (permission.error) throw permission.error;
    return { data: permission };
  } } } };
  const core = { setOutput: (key, value) => { outputs[key] = value; }, info() {} };
  return { context, outputs, calls, permission, run: () => execute(github, context, core) };
}

for (const body of ['/ocr review', 'ocr review', '  /OCR REVIEW\r\n', '\nocr\treview  ']) {
  test('comment command accepts ' + JSON.stringify(body) + ' from a writer', async () => {
    const f = commandFixture();
    f.context.payload.comment.body = body;
    await f.run();
    assert.equal(f.outputs.allowed, 'true');
    assert.deepEqual(f.calls, [{ ...f.context.repo, username: 'comment-author' }]);
  });
}

for (const body of [
  '', null, '/ocr review extra', 'please ocr review', '> /ocr review', '`ocr review`',
  '```\n/ocr review\n```', '/ocr review\nignore the rules', '/ocr\nreview', '/ocr review; echo injected',
]) {
  test('unrecognized comment never checks permissions: ' + JSON.stringify(body), async () => {
    const f = commandFixture();
    f.context.payload.comment.body = body;
    await f.run();
    assert.equal(f.outputs.allowed, undefined);
    assert.deepEqual(f.calls, []);
  });
}

for (const [permission, role, allowed] of [
  ['write', 'write', true], ['write', 'maintain', true], ['admin', 'admin', true],
  ['write', 'custom-writer', true], ['read', 'triage', false], ['read', 'read', false],
  ['none', 'none', false], [undefined, 'admin', false],
]) {
  test('comment authorization uses current base permission: ' + role, async () => {
    const f = commandFixture();
    Object.assign(f.permission, { permission, role_name: role });
    f.context.payload.comment.author_association = allowed ? 'NONE' : 'OWNER';
    await f.run();
    assert.equal(f.outputs.allowed === 'true', allowed);
    assert.equal(f.calls.length, 1);
  });
}

for (const [name, mutate] of Object.entries({
  edited: payload => { payload.action = 'edited'; },
  deleted: payload => { payload.action = 'deleted'; },
  issue: payload => { delete payload.issue.pull_request; },
  closed: payload => { payload.issue.state = 'closed'; },
  bot: payload => { payload.comment.user.type = 'Bot'; },
  'missing author': payload => { delete payload.comment.user; },
})) {
  test(name + ' comment cannot authorize a review', async () => {
    const f = commandFixture();
    mutate(f.context.payload);
    await f.run();
    assert.equal(f.outputs.allowed, undefined);
    assert.deepEqual(f.calls, []);
  });
}

for (const status of [403, 404, 500]) {
  test('permission API failure ' + status + ' cannot authorize a review', async () => {
    const f = commandFixture();
    f.permission.error = Object.assign(new Error('Permission lookup failed'), { status });
    await assert.rejects(f.run(), /Permission lookup failed/);
    assert.equal(f.outputs.allowed, undefined);
  });
}

for (const event of ['workflow_dispatch', 'pull_request_target']) {
  test(event + ' still reaches the review without comment authorization', async () => {
    const f = commandFixture();
    f.context.eventName = event;
    await f.run();
    assert.equal(f.outputs.allowed, 'true');
    assert.deepEqual(f.calls, []);
  });
}

test('only authorized jobs join review concurrency, load secrets, and publish command results', () => {
  const workflow = fs.readFileSync(path.resolve(__dirname, '../../.github/workflows/open-code-review.yml'), 'utf8');
  const request = workflow.split('  request:\n')[1].split('\n  review:')[0];
  const review = workflow.split('\n  review:\n')[1];
  assert.doesNotMatch(workflow, /^concurrency:/m);
  assert.doesNotMatch(request, /secrets\.|concurrency:/);
  assert.match(review, /needs: request\n    if: needs\.request\.outputs\.allowed == 'true'/);
  assert.match(review, /concurrency:\n      group: open-code-review-.*github\.event\.issue\.number/);
  assert.match(review, /PR_NUMBER:.*github\.event\.issue\.number/);
  assert.match(review, /PUBLISH_REVIEW:.*github\.event_name != 'workflow_dispatch' \|\| inputs\.publish/);
  assert.match(workflow, /issue_comment:\n    types: \[created\]/);
});

test('model and endpoint are required even when a template contains fallback values', () => {
  const template = structuredClone(defaults);
  template.model = 'unused-model';
  template.providers.litellm.url = 'https://unused.invalid';
  for (const name of ['OCR_MODEL', 'OCR_API_BASE_URL']) {
    for (const value of [undefined, '', ' ']) {
      assert.throws(() => configure(template, { ...settings, [name]: value }), new RegExp(name));
    }
  }
  assert.ok(!Object.hasOwn(defaults, 'model'));
  assert.ok(!Object.hasOwn(defaults.providers.litellm, 'url'));
});

test('model and API path come from settings without serializing credentials', () => {
  const original = structuredClone(defaults);
  const config = configure(defaults, {
    OCR_MODEL: ' another/model ',
    OCR_API_BASE_URL: ' https://gateway.example/proxy/v1/ ',
    OCR_LLM_TOKEN: 'test-only-secret',
  });
  assert.equal(config.model, 'another/model');
  assert.equal(config.providers.litellm.url, 'https://gateway.example/proxy/v1');
  assert.deepEqual(config.providers.litellm.extra_body, {});
  assert.equal(config.providers.litellm.api_key_cmd, 'printenv OCR_LLM_TOKEN');
  assert.ok(!JSON.stringify(config).includes('test-only-secret'));
  assert.deepEqual(defaults, original);
});

test('request options come only from settings and default to empty', () => {
  const config = configure(defaults, {
    ...settings, OCR_EXTRA_BODY: '{"reasoning_effort":"low"}',
  });
  assert.deepEqual(config.providers.litellm.extra_body, { reasoning_effort: 'low' });
  for (const extra of [undefined, '', '{}']) {
    assert.deepEqual(configure(defaults, { ...settings, OCR_EXTRA_BODY: extra }).providers.litellm.extra_body, {});
  }
});

test('invalid settings fail before a request and errors do not echo supplied values', () => {
  for (const base of [
    'invalid', 'http://gateway.example/v1', 'https://user:test-only-secret@gateway.example/v1',
    'https://gateway.example/v1?key=test-only-secret', 'https://gateway.example/v1#fragment',
  ]) {
    assert.throws(() => configure(defaults, { ...settings, OCR_API_BASE_URL: base }), error => {
      assert.match(error.message, /OCR_API_BASE_URL/);
      assert.doesNotMatch(error.message, /test-only-secret/);
      return true;
    });
  }
  assert.throws(() => configure(defaults, { ...settings, OCR_MODEL: 'first\nsecond' }), /OCR_MODEL/);
  for (const extra of [
    '{broken', 'null', '[]', '"text"', '{"model":"override"}', '{"messages":[]}', '{"tools":[]}',
  ]) {
    assert.throws(() => configure(defaults, { ...settings, OCR_EXTRA_BODY: extra }), /OCR_EXTRA_BODY/);
  }
});

function fixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ocr-test-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const target = { number: 123, base: 'a'.repeat(40), head: 'b'.repeat(40), merge_base: 'c'.repeat(40) };
  const config = configure(defaults, settings);
  const item = { item_id: 'one', path: 'Example.cs' };
  const result = {
    comments: [],
    summary: { input_tokens: 100, output_tokens: 20, elapsed: '2s' },
    manifest: {
      schema_version: 'ocr.run-manifest/v1',
      terminal_state: 'complete',
      input: { resolved_head: target.head, resolved_base: target.merge_base },
      execution: { model: config.model },
      coverage: { selected: [item], completed: [item], reused: [], failed: [], waived: [] },
    },
  };
  const preview = { files: [{ path: item.path, will_review: true }] };
  const calls = [];
  const current = { state: 'open', head: { sha: target.head }, base: { sha: target.base } };
  const comments = [];
  const github = {
    rest: {
      pulls: { get: async () => { calls.push('read PR'); return { data: current }; } },
      issues: {
        listComments: async () => ({ data: comments }),
        createComment: async args => { calls.push({ create: args }); },
        updateComment: async args => { calls.push({ update: args }); },
      },
    },
    paginate: async () => comments,
  };
  const outputs = {};
  const core = {
    summary: { addRaw() { return this; }, async write() {} },
    setOutput: (key, value) => { outputs[key] = value; },
    warning: message => calls.push({ warning: message }),
  };
  const context = { repo: { owner: 'NethermindEth', repo: 'nethermind' }, serverUrl: 'https://github.com', runId: 42 };
  const write = (name, value) => fs.writeFileSync(path.join(directory, name + '.json'), JSON.stringify(value));
  const run = async (enabled = true, exitCode = 0, postReview = async ({ core: publisherCore }) => {
    calls.push('post review');
    publisherCore.setOutput('summary_comment_url', 'https://github.com/example');
  }) => {
    write('target', target);
    write('config', config);
    write('preview', preview);
    write('result', result);
    fs.writeFileSync(path.join(directory, 'exit-code.txt'), String(exitCode));
    return publish({ github, context, core, directory, enabled, postReview });
  };
  return { directory, target, config, result, preview, calls, current, comments, outputs, run };
}

test('complete zero-finding review accounts for every selected file', t => {
  const f = fixture(t);
  const report = assess(f.result, f.preview, f.target, 0, f.config.model);
  assert.equal(report.complete, true);
  assert.match(report.markdown, /Findings: 0/);
  assert.match(report.markdown, /100 input, 20 output/);
});

test('publication validates the captured model without disclosing it in the public summary', async t => {
  const f = fixture(t);
  f.config.model = 'another/model';
  f.result.manifest.execution.model = f.config.model;
  const report = await f.run();
  assert.equal(report.complete, true);
  assert.ok(!report.markdown.includes(f.config.model));
  assert.ok(!fs.readFileSync(path.join(f.directory, 'summary.md'), 'utf8').includes(f.config.model));
  assert.ok(f.calls.includes('post review'));
});

test('a previous model is rejected when the captured configuration selected another', async t => {
  const f = fixture(t);
  f.config.model = 'another/model';
  const report = await f.run();
  assert.equal(report.complete, false);
  assert.ok(report.reasons.includes('unexpected model in the review manifest'));
  assert.ok(!f.calls.includes('post review'));
});

for (const [name, mutate] of Object.entries({
  'partial coverage despite exit zero': f => { f.result.manifest.terminal_state = 'partial'; },
  'exhausted budget despite complete state': f => { f.result.summary.budget_exceeded = true; },
  'missing manifest': f => { delete f.result.manifest; },
  'wrong commit': f => { f.result.manifest.input.resolved_head = 'd'.repeat(40); },
  'wrong merge base': f => { f.result.manifest.input.resolved_base = 'd'.repeat(40); },
  'wrong model': f => { f.result.manifest.execution.model = 'another-model'; },
  'missing completed file': f => { f.result.manifest.coverage.completed = []; },
  'duplicate coverage': f => { f.result.manifest.coverage.completed.push(f.result.manifest.coverage.completed[0]); },
  'waived file': f => { f.result.manifest.coverage.waived.push(f.result.manifest.coverage.selected[0]); },
  'selection mismatch': f => { f.preview.files.push({ path: 'Other.cs', will_review: true }); },
  'oversize exclusion': f => { f.preview.files.push({ path: 'Huge.cs', will_review: false, exclude_reason: 'too_large' }); },
  'failed run': f => { f.result.manifest.run_failure = { classification: 'internal' }; },
})) {
  test(name + ' publishes only an incomplete summary', async t => {
    const f = fixture(t);
    mutate(f);
    const report = await f.run();
    assert.equal(report.complete, false);
    assert.ok(!f.calls.includes('post review'));
    const posted = f.calls.find(call => call.create);
    assert.match(posted.create.body, /INCOMPLETE/);
    assert.doesNotMatch(posted.create.body, /Looks good|✅/);
  });
}

test('nonzero exit cannot publish a successful review', async t => {
  const f = fixture(t);
  assert.equal((await f.run(true, 124)).complete, false);
  assert.ok(!f.calls.includes('post review'));
});

for (const change of ['head', 'base', 'closed', 'draft']) {
  test('PR ' + change + ' change suppresses all writes', async t => {
    const f = fixture(t);
    if (change === 'closed') f.current.state = 'closed';
    else if (change === 'draft') { f.target.automatic = true; f.current.draft = true; }
    else f.current[change].sha = 'd'.repeat(40);
    await f.run();
    assert.equal(f.outputs.stale, 'true');
    assert.ok(!f.calls.includes('post review'));
    assert.ok(!f.calls.some(call => call.create || call.update));
  });
}

test('manual publication remains available for draft PRs', async t => {
  const f = fixture(t);
  f.current.draft = true;
  await f.run();
  assert.ok(f.calls.includes('post review'));
});

test('artifact-only run makes no GitHub calls', async t => {
  const f = fixture(t);
  await f.run(false);
  assert.deepEqual(f.calls, []);
  assert.ok(fs.existsSync(path.join(f.directory, 'summary.md')));
});

test('complete reviews delegate inline posting and annotate both summary paths', async t => {
  const f = fixture(t);
  await f.run(true, 0, async args => {
    assert.equal(args.incremental, true);
    assert.equal(args.resolveOutdated, 'false');
    assert.equal(args.routeSeverityBelow, 'low');
    assert.equal(args.prNumber, 123);
    await args.github.rest.issues.createComment({ body: '<!-- ocr-summary -->\nNo findings' });
    await args.github.rest.issues.updateComment({ body: '<!-- ocr-summary -->\nFindings' });
    args.core.setOutput('summary_comment_url', 'https://github.com/example');
  });
  for (const call of f.calls.filter(call => call.create || call.update)) {
    assert.match((call.create || call.update).body, /Reviewed 1 \/ 1/);
    assert.match((call.create || call.update).body, /actions\/runs\/42/);
  }
});

test('upstream summary lookup cannot select a human comment with the bot marker', async t => {
  const f = fixture(t);
  f.comments.push(
    { id: 1, user: { login: 'human' }, body: '<!-- ocr-summary -->' },
    { id: 2, user: { login: 'github-actions[bot]' }, body: '<!-- ocr-summary -->' },
  );
  await f.run(true, 0, async args => {
    const { data } = await args.github.rest.issues.listComments({});
    assert.equal(data.length, 2);
    assert.ok(!data[0].body.includes('<!-- ocr-summary -->'));
    assert.ok(data[1].body.includes('<!-- ocr-summary -->'));
    args.core.setOutput('summary_comment_url', 'https://github.com/example');
  });
});

for (const operation of ['createComment', 'updateComment']) {
  test(operation + ' distinguishes policy routing from an inline posting failure', async t => {
    const f = fixture(t);
    const routed = 'Routed to summary (severity low · category maintainability)';
    const failure = '⚠️ GitHub could not post this as an inline comment: HTTP 422';
    await f.run(true, 0, async args => {
      await args.github.rest.issues[operation]({ body:
        '<!-- ocr-summary -->\n⚠️ GitHub could not post this as an inline comment: ' + routed + '\n\n' + failure });
      args.core.setOutput('summary_comment_url', 'https://github.com/example');
    });
    const posted = f.calls.find(call => call.create || call.update);
    const body = (posted.create || posted.update).body;
    assert.ok(body.includes('ℹ️ ' + routed + '.'));
    assert.ok(!body.includes('inline comment: ' + routed));
    assert.ok(body.includes(failure));
  });
}

test('workflow uploads only the public summary and loads runtime settings from secrets', () => {
  const workflow = fs.readFileSync(path.resolve(__dirname, '../../.github/workflows/open-code-review.yml'), 'utf8');
  const artifact = workflow.split('      - name: Retain public review summary\n')[1].split('\n      - name:')[0];
  assert.match(artifact, /path: \$\{\{ env\.OCR_DIRECTORY \}\}\/summary\.md\n/);
  assert.doesNotMatch(artifact, /config\.json|result\.json|stderr\.log|preview\.json|target\.json|exit-code\.txt/);
  for (const name of ['OCR_MODEL', 'OCR_API_BASE_URL', 'OCR_EXTRA_BODY']) {
    assert.ok(workflow.includes('${{ secrets.' + name + ' }}'));
    assert.ok(!workflow.includes('vars.' + name));
  }
});

for (const failure of ['missing summary', 'failed inline']) {
  test('publication failure: ' + failure, async t => {
    const f = fixture(t);
    await assert.rejects(f.run(true, 0, async args => {
      if (failure === 'failed inline') {
        args.core.setOutput('summary_comment_url', 'https://github.com/example');
        args.core.setOutput('comments_failed', 1);
      }
    }), /publication did not finish/);
  });
}

test('incomplete rerun updates only the bot-owned OCR summary', async t => {
  const f = fixture(t);
  f.result.manifest.terminal_state = 'partial';
  f.comments.push(
    { id: 1, user: { login: 'human' }, body: '<!-- ocr-summary -->' },
    { id: 2, user: { login: 'github-actions[bot]' }, body: 'Claude review' },
    { id: 3, user: { login: 'github-actions[bot]' }, body: '<!-- ocr-summary --> old' },
  );
  await f.run();
  assert.equal(f.calls.find(call => call.update).update.comment_id, 3);
  assert.ok(!f.calls.some(call => call.create));
});

test('excluded files are disclosed without claiming full-PR coverage', t => {
  const f = fixture(t);
  f.preview.files.push({ path: 'deleted.cs', will_review: false, exclude_reason: 'deleted' });
  const report = assess(f.result, f.preview, f.target, 0, f.config.model);
  assert.equal(report.complete, true);
  assert.match(report.markdown, /excluded 1/);
  assert.match(report.markdown, /deleted.cs/);
});

test('generated rules embed canonical guidance and put test rules before general C#', t => {
  const f = fixture(t);
  const root = path.resolve(__dirname, '../..');
  prepare(root, f.directory, settings);
  const rules = JSON.parse(fs.readFileSync(path.join(f.directory, 'rules.json')));
  const robustness = fs.readFileSync(path.join(root, '.agents/rules/robustness.md'), 'utf8');
  assert.ok(rules.rules[0].rule.includes(robustness));
  assert.match(rules.rules[0].rule, /TestBlockchain/);
  assert.equal(rules.rules[1].path, '**/*.cs');
  assert.ok(rules.include.some(pattern => pattern.includes('csproj')));
});

test('prepare CLI captures the configured model and endpoint without serializing the key', t => {
  const f = fixture(t);
  const root = path.resolve(__dirname, '../..');
  const ran = spawnSync(process.execPath, [
    path.join(__dirname, 'open-code-review.cjs'), 'prepare', root, f.directory,
  ], {
    encoding: 'utf8',
    env: { ...process.env, OCR_MODEL: 'another/model', OCR_API_BASE_URL: 'https://gateway.example/api/v1/',
      OCR_EXTRA_BODY: '{"reasoning_effort":"low"}', OCR_LLM_TOKEN: 'test-only-secret' },
  });
  assert.equal(ran.status, 0, ran.stderr);
  const raw = fs.readFileSync(path.join(f.directory, 'config.json'), 'utf8');
  const config = JSON.parse(raw);
  assert.equal(config.model, 'another/model');
  assert.equal(config.providers.litellm.url, 'https://gateway.example/api/v1');
  assert.deepEqual(config.providers.litellm.extra_body, { reasoning_effort: 'low' });
  assert.ok(![raw, ran.stdout, ran.stderr].some(text => text.includes('test-only-secret')));
});

for (const name of ['OCR_MODEL', 'OCR_API_BASE_URL']) {
  test('prepare CLI refuses to write runtime configuration without ' + name, t => {
    const f = fixture(t);
    const ran = spawnSync(process.execPath, [
      path.join(__dirname, 'open-code-review.cjs'), 'prepare', path.resolve(__dirname, '../..'), f.directory,
    ], {
      encoding: 'utf8',
      env: { ...process.env, ...settings, [name]: '', OCR_EXTRA_BODY: '' },
    });
    assert.notEqual(ran.status, 0);
    assert.match(ran.stderr, new RegExp('Set the required ' + name + ' Actions secret'));
    assert.ok(!fs.existsSync(path.join(f.directory, 'config.json')));
  });
}

for (const [budget, secret, cliExit, expectedExit] of [
  ['500000', 'test-token', 0, 0],
  ['1000000', 'test-token', 1, 0],
  ['2000000', 'test-token', 124, 0],
  ['0', 'test-token', 0, 1],
  ['500000', '', 0, 1],
]) {
  test('workflow captures CLI outcome with budget=' + budget + ', CLI exit=' + cliExit +
    ', configured=' + Boolean(secret), t => {
    const f = fixture(t);
    const workflow = fs.readFileSync(path.resolve(__dirname, '../../.github/workflows/open-code-review.yml'), 'utf8');
    const step = workflow.split('      - name: Review captured range\n')[1].split('\n      - name:')[0];
    const script = step.split('        run: |\n')[1].replace(/^          /gm, '');
    fs.writeFileSync(path.join(f.directory, 'ocr'), [
      '#!/usr/bin/env bash',
      'printf "%s\\n" "$*" >> "$OCR_DIRECTORY/arguments.txt"',
      'if [[ "$*" == *"--preview"* ]]; then echo \'{"files": []}\'; exit 0; fi',
      'echo \'{"status": "fixture"}\'',
      'exit "$FAKE_OCR_EXIT"',
      '',
    ].join('\n'), { mode: 0o755 });
    const ran = spawnSync('bash', ['-e', '-o', 'pipefail', '-c', script], {
      encoding: 'utf8',
      env: { ...process.env, OCR_DIRECTORY: f.directory, OCR_LLM_TOKEN: secret,
        HEAD_SHA: f.target.head, REVIEW_MERGE_BASE: f.target.merge_base,
        REVIEW_TOKEN_BUDGET: budget, FAKE_OCR_EXIT: String(cliExit) },
    });
    assert.equal(ran.status, expectedExit, ran.stderr);
    assert.ok(!ran.stdout.includes('test-token'));
    if (expectedExit === 0) {
      assert.equal(fs.readFileSync(path.join(f.directory, 'exit-code.txt'), 'utf8').trim(), String(cliExit));
      assert.match(fs.readFileSync(path.join(f.directory, 'arguments.txt'), 'utf8'),
        new RegExp('--max-tokens-budget ' + budget));
    } else {
      assert.ok(!fs.existsSync(path.join(f.directory, 'arguments.txt')));
    }
  });
}
