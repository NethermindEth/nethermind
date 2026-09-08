# Opcode naming

This build-only Fody weaver gives every opcode a distinct `Op…` method
name in CPU profiles, even when the profiler omits generic arguments.
The EVM project runs it after InlineIL on ordinary builds.

It clones `ExecuteOpcode` (or the dedicated JUMPI dispatcher) and its pointer factory for each opcode,
then redirects the table construction calls to the named factories. The cloned
methods retain all generic arguments, constraints, implementation flags and the
explicit `tail.calli`. The instruction implementation stays in one source
template; there is no runtime forwarding wrapper. Table refreshes use the same
rewritten construction paths.

CALL, CALLCODE, DELEGATECALL and STATICCALL have separate names, as do CREATE
and CREATE2. Their fork-selection factory chains are cloned with the opcode name
carried through to the final function pointer. Gas-policy and fork variants keep
their generic specialization. The build fails if the generated names do not cover
the instruction enum; unassigned table entries use `OpBadInstruction`.

Validation:

```sh
dotnet build src/Nethermind/Nethermind.Evm.Test/Nethermind.Evm.Test.csproj -c Release -nr:false
dotnet test --project src/Nethermind/Nethermind.Evm.Test/Nethermind.Evm.Test.csproj -c Release --no-build -- --filter FullyQualifiedName~VirtualMachineTests
```

When editing the weaver itself, disable MSBuild node reuse as above so a node
cannot retain a previously loaded version of the add-in.

For native-code inspection, set `DOTNET_JitDisasm` to
`*:OpPush1 *:OpPush2 *:OpAdd *:OpSLoad` and
`DOTNET_JitStdOutFile` to a local output file before running the tests.
Confirm that the named methods contain the instruction work and tail transfers,
not calls to `ExecuteOpcode`. Compare unprofiled timings separately before
assuming unchanged throughput; identical IL semantics do not guarantee identical
JIT layout or tiering behavior.
