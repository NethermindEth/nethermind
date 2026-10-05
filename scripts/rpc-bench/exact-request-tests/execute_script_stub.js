const fs = require('fs');
const vm = require('vm');
(async () => {
  const input = JSON.parse(fs.readFileSync(0, 'utf8'));
  let source = fs.readFileSync(input.script, 'utf8');
  source = source.substring(source.indexOf('export const options ='))
    .replace('export const options =', 'const options =')
    .replace('export default async function ()', 'async function run()');
  const posted = []; let checks = 0;
  const context = {
    config: {options: {}}, requestsData: Array.from({length: input.fixtureSize || 22000}, (_, i) => [i, 'synthetic', 'eth_call', String(i)]),
    __ENV: {RPC_GLOBAL_REQUEST_CAP: input.cap, RPC_CLIENT_ENDPOINT: 'synthetic-no-network'},
    exec: {scenario: {iterationInTest: 0}},
    http: {post: (_url, payload) => {posted.push(Number(payload)); return {status: 200, json: () => ({result: '0x'})};}},
    group: (_name, callback) => callback(),
    check: (value, rules) => {for (const test of Object.values(rules)) {if (!test(value)) throw new Error('synthetic check failed'); checks++;}},
    console: {error: () => {throw new Error('unexpected script catch');}},
  };
  let error = null;
  try {
    vm.createContext(context);
    vm.runInContext(source + '\nglobalThis.run = run;', context, {timeout: 1000});
    for (const idx of input.indices) {context.exec.scenario.iterationInTest = idx; await context.run();}
  } catch (failure) {error = failure.message;}
  process.stdout.write(JSON.stringify({count: posted.length, checks, posted, error}));
})();
