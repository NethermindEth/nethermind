# JitAsm

A tool for viewing JIT-compiled assembly output for .NET methods. Useful for analyzing code generation, verifying optimizations, and comparing different implementations.

## How It Works

JitAsm spawns a child process with JIT diagnostic environment variables (`DOTNET_JitDisasm`) to capture disassembly output. This provides accurate Tier1 optimized output without external dependencies.

### Two-Pass Static Constructor Handling

When a method references static fields, the JIT may include static constructor initialization checks. JitAsm automatically detects these and runs a second pass with static constructors pre-initialized, showing the steady-state optimized code path.

## Usage

```bash
dotnet run --project tools/JitAsm -c Release -- [options]
```

### Options

| Option | Description |
|--------|-------------|
| `-a, --assembly <path>` | Path to the assembly containing the method (required) |
| `-t, --type <name>` | Fully qualified type name (optional, searches all types if not specified) |
| `-m, --method <name>` | Method name to disassemble (required) |
| `--type-params <types>` | Generic type parameters (comma-separated) |
| `--skip-cctor-detection` | Skip automatic static constructor detection (single pass only) |
| `-v, --verbose` | Show resolution details and both passes |

## Examples

### Basic Usage

```bash
# Disassemble a simple method
dotnet run --project tools/JitAsm -c Release -- \
    -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll \
    -m PushBytesRef
```

### With Type Specification

```bash
# Specify the containing type
dotnet run --project tools/JitAsm -c Release -- \
    -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll \
    -t Nethermind.Evm.EvmStack \
    -m PushBytesRef
```

### Generic Methods

```bash
# Disassemble a generic method with type parameters
dotnet run --project tools/JitAsm -c Release -- \
    -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll \
    -t Nethermind.Evm.EvmStack \
    -m PushBytes \
    --type-params Nethermind.Core.OffFlag
```

### Multiple Generic Parameters

```bash
# Use comma-separated type names for multiple parameters
dotnet run --project tools/JitAsm -c Release -- \
    -a Nethermind.Evm.dll \
    -m SomeGenericMethod \
    --type-params "System.Int32,System.String"
```

### EVM Instruction Example

Disassemble the MUL opcode implementation with specific gas policy and tracing flags:

```bash
dotnet run --project tools/JitAsm -c Release -- \
    -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll \
    -t Nethermind.Evm.EvmInstructions \
    -m InstructionMath2Param \
    --type-params "Nethermind.Evm.GasPolicy.EthereumGasPolicy,Nethermind.Evm.EvmInstructions+OpMul,Nethermind.Core.OffFlag"
```

This shows the JIT output for `InstructionMath2Param<EthereumGasPolicy, OpMul, OffFlag>`:
- `EthereumGasPolicy` - Standard Ethereum gas accounting
- `OpMul` - The multiplication operation (nested type, use `+` syntax)
- `OffFlag` - Tracing disabled (allows dead code elimination of tracing paths)

Other math operations can be viewed by replacing `OpMul` with: `OpAdd`, `OpSub`, `OpDiv`, `OpMod`, `OpSDiv`, `OpSMod`, `OpLt`, `OpGt`, `OpSLt`, `OpSGt`, `OpEq`, `OpAnd`, `OpOr`, `OpXor`.

### Type Aliases

Common C# type aliases are supported:

```bash
# These are equivalent
--type-params int
--type-params System.Int32
```

Supported aliases: `bool`, `byte`, `sbyte`, `char`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`, `string`, `object`, `nint`, `nuint`

### Verbose Mode

```bash
# Show detailed output including cctor detection
dotnet run --project tools/JitAsm -c Release -- \
    -a Nethermind.Evm.dll \
    -m PushBytesRef \
    -v
```

### Skip Static Constructor Detection

```bash
# Single pass only (faster, but may show cctor overhead)
dotnet run --project tools/JitAsm -c Release -- \
    -a Nethermind.Evm.dll \
    -m SomeMethod \
    --skip-cctor-detection
```

## Example Output

```
; Assembly listing for method Nethermind.Evm.EvmStack:PushBytesRef():byref:this (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX + EVEX on Windows
; FullOpts code
; optimized code
; rsp based frame
; partially interruptible
; No PGO data

G_M000_IG01:                ;; offset=0x0000
       sub      rsp, 40

G_M000_IG02:                ;; offset=0x0004
       mov      eax, dword ptr [rcx+0x18]
       lea      edx, [rax+0x01]
       shl      eax, 5
       add      rax, bword ptr [rcx+0x08]
       cmp      edx, 0x401
       jae      SHORT G_M000_IG04
       mov      dword ptr [rcx+0x18], edx

G_M000_IG03:                ;; offset=0x001C
       add      rsp, 40
       ret

G_M000_IG04:                ;; offset=0x0021
       call     [Nethermind.Evm.EvmStack:ThrowEvmStackOverflowException()]
       int3

; Total bytes of code 40
```

## Iterative Workflow

JitAsm is designed for rapid iteration when optimizing code:

1. **Modify source code**
2. **Build the target assembly:**
   ```bash
   dotnet build src/Nethermind/Nethermind.Evm -c Release
   ```
3. **View the generated assembly:**
   ```bash
   dotnet run --project tools/JitAsm -c Release -- \
       -a src/Nethermind/artifacts/bin/Nethermind.Evm/release/Nethermind.Evm.dll \
       -m YourMethod
   ```
4. **Compare output between iterations**

## Environment Variables

The tool sets these JIT diagnostic variables for the child process:

| Variable | Value | Purpose |
|----------|-------|---------|
| `DOTNET_TieredCompilation` | `0` | Disable tiered compilation |
| `DOTNET_TC_QuickJit` | `0` | Disable quick JIT |
| `DOTNET_JitDisasm` | `<method>` | Output disassembly for matching methods |
| `DOTNET_JitDiffableDasm` | `1` | Consistent, diffable output format |

## Troubleshooting

### No disassembly output

- Verify the assembly path is correct
- Check if the method name is spelled correctly
- Try without the `-t` option to search all types
- Use `-v` for verbose output to see what's happening

### Method not found

- Ensure the assembly is built (Release configuration recommended)
- For generic methods, provide all required type parameters
- Use fully qualified type names for type parameters from other assemblies

### Multiple methods with same name

- Specify the type with `-t` to narrow the search
- The tool will use the first matching overload if multiple exist

## Running Tests

```bash
dotnet test --project tools/JitAsm.Test -c Release
```
