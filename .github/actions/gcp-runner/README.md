# GCP runner scripts

The composite action sets up the .NET SDK from `global.json` before running `create.cs` or `destroy.cs`.
Both programs preserve the action's environment inputs and GitHub output names.
Runner names include the run attempt so retries can register a new JIT runner.
Registering the JIT runner is retried on GitHub 429 and 5xx responses; a 409 after such a
retry means the timed-out request did register the name, so that registration is removed first.

"Re-run failed jobs" does not re-run a create job that succeeded, so a re-run sync job would
wait for a runner that no longer exists. Callers should create, sync and tear down in one
reusable workflow per runner, skip the sync when the create job's attempt differs from
`github.run_attempt`, and have users re-run the create job, which re-runs its dependents.

Teardown runs in a separate job and still requires SDK installation and compilation.
The pinned SDK supports `#:include`; an arbitrary preinstalled SDK may not. Restore
uses only the local action directory and disables NuGet auditing and the file-based
`PublishAot` default, whose implicit ILCompiler and runtime-pack references would need
a feed, because these scripts have no package dependencies. An SDK download failure or
cancellation before `destroy.cs` starts can leave the VM until GCE's `max_run_duration` deletion backstop.
Choose that lifetime to cover boot and the job without an excessive cleanup delay.

`startup-script.sh` is deliberately retained as the fresh-VM bootstrap exception.
The Ubuntu image has no SDK. The launcher downloads the pinned Linux x64 or arm64 SDK
for the VM's architecture, checks its SHA-512 digest before extraction, fetches the C# sources supplied in
instance metadata, and publishes `startup.cs` once before running it. It explicitly
sets `HOME` and `DOTNET_CLI_HOME` for GCE's system service environment. Bootstrap
failures report both a failed stage and a diagnostic metadata attribute.
Updating the SDK requires updating
its URL and both digests from the official .NET release metadata. Provisioning logic,
disk validation, package installation, metadata status reporting, and runner
service configuration live in `startup-provision.helpers.cs`.

The SDK and startup sources remain under `/opt/gh-runner-bootstrap` because the
runner service runs the compiled DLL directly to read the private JIT credential and
launch the vendor runner, without another restore or compile. The service stops its
whole control group, including the vendor runner's descendants. Dockerfiles, image builds, and container entrypoints
are unchanged. Python remains available for other tools used by sync jobs.

Changing the `gh-` prefix in `Gcp.InstanceName`, `GCP_ZONES`, the subnetwork or the workflow
files that create runners needs a matching change to the spawner's permissions.

Run the Linux regression suites without cloud credentials or real instances:

```sh
dotnet run --file scripts/ci/run-script-tests.cs -- scripts/sync
dotnet run --file scripts/ci/test-sync-metrics.cs
dotnet run --file scripts/ci/run-script-tests.cs -- --check .github/actions/gcp-runner
```

Lifecycle tests inject command results; these checks do not provision a live VM or
download the SDK archive.
