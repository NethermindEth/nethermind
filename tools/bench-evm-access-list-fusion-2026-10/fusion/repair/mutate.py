#!/usr/bin/env python3
"""Repair mutation driver for the compare-branch fusion. Runs in /root/fusion-b3/repair/nethermind on build3.

usage: mutate.py <name> [<name>...]   (or 'all')
Each mutation: restore the source, apply one exact replacement (must match once), build Release incrementally,
run the chosen test filter under a timeout, record the summary, restore the source.
"""
import os, re, subprocess, sys, time

ROOT = '/root/fusion-b3/repair/nethermind'
SRC = 'src/Nethermind/Nethermind.Evm/VirtualMachine.HostHandlers.std.cs'
LOGS = '/root/fusion-b3/repair/logs'
TESTDIR = ROOT + '/src/Nethermind/artifacts/bin/Nethermind.Evm.Test/release'
ENV = dict(os.environ, DOTNET_ROOT='/root/.dotnet', PATH=os.environ['PATH'] + ':/root/.dotnet',
           DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1', MSBUILDDISABLENODEREUSE='1')

ALL = ('FullyQualifiedName~HostCompareBranchFusionTests|FullyQualifiedName~HostMemoryFastPathTests'
       '|FullyQualifiedName~Cancellation_is_polled')

# name: (old, new, filter, extra env)
MUTATIONS = {
    # The comparison run alone (ExecuteCompareBranch): wrong body, gas, count or successor.
    'aloneIsZeroAsNot': (
        'Math1ParamCore<EvmInstructions.OpIsZero, OffFlag, OnFlag>(ref stack);',
        'Math1ParamCore<EvmInstructions.OpNot, OffFlag, OnFlag>(ref stack);', ALL, {}),
    'aloneEqAsXor': (
        'BitwiseCore<EvmInstructions.OpBitwiseEq, OffFlag, OnFlag>(ref stack);',
        'BitwiseCore<EvmInstructions.OpBitwiseXor, OffFlag, OnFlag>(ref stack);', ALL, {}),
    'aloneOrderAlwaysLt': (
        'Math2ParamCore<TOpMath, OffFlag, OnFlag>(ref stack);',
        'Math2ParamCore<EvmInstructions.OpLt, OffFlag, OnFlag>(ref stack);', ALL, {}),
    'aloneNoGas': (
        '            gasLeft -= VeryLowGasCost.GasCost;\n            pc++;\n            opCodeCount++;\n\n            // ISZERO dispatches',
        '            pc++;\n            opCodeCount++;\n\n            // ISZERO dispatches', ALL, {}),
    'aloneNoOpcodeCount': (
        '            opCodeCount++;\n\n            // ISZERO dispatches',
        '\n            // ISZERO dispatches', ALL, {}),
    'aloneIsZeroWrongSuccessor': (
        'TCondition.Inputs == 1 ? following :',
        'TCondition.Inputs == 1 ? following + 1 :', ALL, {}),
    # The hand-off to the fused handler and its checks.
    'noIsZeroHandOff': (
        'if (following == (byte)Instruction.PUSH2 || following == (byte)Instruction.ISZERO)',
        'if (following == (byte)Instruction.PUSH2)', ALL, {}),
    'handOffSwapped': (
        'nint fused = following == (byte)Instruction.PUSH2\n',
        'nint fused = following != (byte)Instruction.PUSH2\n', ALL, {}),
    'fusedSkipsJumpiCheck': (
        ': Unsafe.Add(ref stack.Code, pc + 4) != (byte)Instruction.JUMPI)',
        ': false)', ALL, {}),
    'fusedInvertedSkipsBranchCheck': (
        'if (TInverted.IsActive ? !IsBranch(ref stack.Code, pc + 2) :',
        'if (TInverted.IsActive ? false :', ALL, {}),
    'fusedHeadCheckDropped': (
        '            if (stack.Head < TCondition.Inputs)\n                goto Plain;\n            // The comparison',
        '            // The comparison', ALL, {}),
    # ExecuteCompareAlone, the fused handler's way out when no branch follows.
    'compareAloneNoGas': (
        '                goto Plain;\n            gasLeft -= VeryLowGasCost.GasCost;\n            pc++;\n            opCodeCount++;\n\n            nint next',
        '                goto Plain;\n            pc++;\n            opCodeCount++;\n\n            nint next', ALL, {}),
    'compareAloneSkipsComparison': (
        'if (gasLeft < VeryLowGasCost.GasCost || TCondition.ExecuteAlone(ref stack) != EvmExceptionType.None)',
        'if (gasLeft < VeryLowGasCost.GasCost)', ALL, {}),
}


def run(cmd, log, timeout):
    t0 = time.time()
    with open(log, 'w') as f:
        try:
            p = subprocess.run(['nice', '-n', '10', 'bash', '-c', cmd], cwd=ROOT, env=ENV, stdout=f, stderr=subprocess.STDOUT, timeout=timeout)
            rc = p.returncode
        except subprocess.TimeoutExpired:
            rc = 'TIMEOUT'
    return rc, time.time() - t0


def summary(log):
    text = open(log, errors='replace').read()
    m = {k: re.search(r'^\s*' + k + r': (\d+)', text, re.M) for k in ('total', 'failed', 'succeeded', 'skipped')}
    failed_names = re.findall(r'^failed (\S+)', text, re.M)
    return {k: (int(v.group(1)) if v else None) for k, v in m.items()}, failed_names[:5]


def restore():
    subprocess.run(['git', 'checkout', '--', SRC], cwd=ROOT, check=True)


def main(names):
    if names == ['all']:
        names = list(MUTATIONS)
    out = open(LOGS + '/mutations.tsv', 'a')
    for name in names:
        old, new, flt, extra = MUTATIONS[name]
        restore()
        path = os.path.join(ROOT, SRC)
        text = open(path).read()
        n = text.count(old)
        if n != 1:
            out.write(f'{name}\tAPPLY-FAILED\tmatches={n}\n'); out.flush(); continue
        open(path, 'w').write(text.replace(old, new))
        rc, dt = run('dotnet build src/Nethermind/Nethermind.Evm.Test/Nethermind.Evm.Test.csproj -c Release -nr:false /p:UseSharedCompilation=false -v q',
                     f'{LOGS}/mut-{name}-build.log', 1800)
        if rc != 0:
            out.write(f'{name}\tBUILD-FAILED\trc={rc}\n'); out.flush(); restore(); continue
        envs = ' '.join(f'{k}={v}' for k, v in extra.items())
        rc, dt = run(f"cd {TESTDIR} && {envs} dotnet Nethermind.Evm.Test.dll --filter '{flt}'", f'{LOGS}/mut-{name}-test.log', 1500)
        s, failed = summary(f'{LOGS}/mut-{name}-test.log')
        verdict = 'KILLED' if (rc == 'TIMEOUT' or (s['failed'] or 0) > 0) else 'SURVIVED'
        out.write(f'{name}\t{verdict}\trc={rc}\t{s}\t{dt:.0f}s\t{failed}\n'); out.flush()
        restore()
    # Leave the tree clean and rebuilt at the branch.
    run('dotnet build src/Nethermind/Nethermind.Evm.Test/Nethermind.Evm.Test.csproj -c Release -nr:false /p:UseSharedCompilation=false -v q',
        f'{LOGS}/mut-restore-build.log', 1800)
    out.write('DONE\n'); out.flush()


if __name__ == '__main__':
    main(sys.argv[1:])
