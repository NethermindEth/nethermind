# Precompile gas pricing refinement leaf

This leaf covers only the standard `EthereumGasPolicy` precompile base-plus-data
charge. `PrecompileGasPricingKernel.cs` is an allocation-free value kernel with
three explicit outcomes: success, base/data `ulong` overflow, and ordinary
out-of-gas. The standard-policy adapter evaluates `IPrecompile.BaseGasCost` then
`IPrecompile.DataGasCost`, delegates the decision to that kernel, and assigns only
the execution-gas field from the returned remaining value.

The restricted Roslyn profile `precompile-gas-pricing` accepts the exact kernel
and adapter shapes only. It rejects changed overflow/OOG semantics, directives,
partial kernel/result declarations, shadow kernel declarations, altered policy
dispatch, and changes to the full-frame local-copy or inline by-reference generic
call forms. For the full-frame source it permits only its two pinned `IDE0063`
diagnostics pragmas; conditional directives, disabled source text, aliases, and
alternate `EthereumVirtualMachine` gas-policy bindings are rejected. It also pins
the selected mainnet composition: `EthereumVirtualMachine` must bind
`VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>`, and
`BlockProcessingModule.Load` must retain the builder-rooted
`.AddScoped<IVirtualMachine, EthereumVirtualMachine>()` registration. The source
manifest hashes that module and the Core registration DSL along with the policy
and call-site sources. Its
deterministic artifacts are:

- `Extractor/Generated/PrecompileGasPricingKernel.ir.json`
- `Extractor/Generated/PrecompileGasPricingKernel.source-manifest.json`
- `Eip803x/Generated/PrecompileGasPricingKernel.lean`

`Eip803x/Refinement/PrecompileGasPricing.lean` proves that, for exact `ulong`
natural-number representations, the generated value kernel maps to
`Precompiles.Wrapper.tryConsumePrecompileGas`. The theorem distinguishes overflow
from ordinary insufficient gas: overflow retains execution gas, while an
affordable-width but insufficient total clears it; a successful total debits only
execution gas and retains all state-gas fields through the result map.

This is not a proof of precompile implementation correctness, a full VM/frame
execution proof, world-state effects, generic-policy behavior other than the
pinned standard `EthereumGasPolicy` dispatch, or integration into the umbrella
Lean/verification gate. The full-frame and inline source checks establish only
their selected generic static-interface invocation shapes; the DI check hashes
the selected module and Core DSL source and semantically binds that source
invocation to `Nethermind.Core.ContainerBuilderExtensions.AddScoped<IVirtualMachine,
EthereumVirtualMachine>()`. It is not a proof of runtime Autofac behavior,
arbitrary modules, or plugin registrations.
