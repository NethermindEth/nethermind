# Generated artifacts

This directory contains the deterministic Stage A extraction artifacts:

- `EvmFrameMachineKernel.ir.json`: exact 114-source, 181-selector, 1024-route, 14-package, and 18-precompile-identity IR;
- `EvmFrameMachineKernel.source-manifest.json`: source/member/artifact/dependency digests;
- `EvmFrameMachineKernel.lean`: theorem-free route, precompile, and dependency lookup data plus imports for all 14 pinned sibling modules and `#check`s for the 16 adequate operational theorem identities of all 14 admitted packages.

These artifacts close only Stage A. They contain no executable frame transition, settlement implementation, precompile body, transaction adapter, or whole-EVM theorem. All 612 enabled opcode routes are composition-admitted through their sibling theorems; the 18 precompile wrapper identities remain explicitly unadmitted.
