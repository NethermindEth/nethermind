# Build stall watchdog

Run a Linux command with output monitoring:

```sh
dotnet run --file scripts/ci/RunWithStallWatchdog.cs -- dotnet build path/to/project.csproj
```

The command runs in its own process group. Its output is forwarded to the console and
saved in `command.log`. A normally completed command retains its exit code. On a
stall, the watchdog gathers diagnostics, sends TERM to the process group, waits for
the termination grace period, sends KILL to remaining group members, and exits 1.
Cancellation also cleans up the process group and exits 143.

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `STALL_SECONDS` | 300 | Output inactivity threshold |
| `POLL_SECONDS` | 15 | Output sampling interval |
| `DIAG_DIR` | `$RUNNER_TEMP/stall-diagnostics`, or `/tmp/stall-diagnostics` | Output directory |
| `DIAGNOSTIC_SECONDS` | 120 | Total subprocess diagnostic budget |
| `INSTALL_TIMEOUT_SECONDS` | 30 | Maximum `dotnet-stack` installation time, capped by the remaining budget |
| `STACK_TIMEOUT_SECONDS` | 10 | Maximum time per stack capture, capped by the remaining budget |
| `TERMINATION_SECONDS` | 30 | Grace period between TERM and KILL |
| `MSBUILDDEBUGPATH` | unset | Optional directory containing MSBuild scheduler logs |

Diagnostics include system resource and process information, installation output,
and managed process stacks when `dotnet-stack` is available. Timed-out commands
retain their partial diagnostic output. Each scheduler log contributes at most its
last 5 MB. Linux `setsid`, `kill`, and standard process diagnostic tools must be on
`PATH`.

Run the regression suite with:

```sh
CI=true dotnet run --file scripts/ci/TestStallWatchdog.cs
```

Linux exercises real process groups and bounded diagnostic commands using isolated
command stubs. The MSBuild generator-target checks also run on Windows. Tests do
not install diagnostic tools or run a solution build.
