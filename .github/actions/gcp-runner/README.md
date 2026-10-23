# GCP runner scripts

The composite action sets up .NET 10.0.402 before running `Create.cs` or `Destroy.cs`.
Both programs preserve the action's environment inputs and GitHub output names.
Runner names include the run attempt so retries can register a new JIT runner.

Teardown runs in a separate job and still requires SDK installation and compilation.
The pinned SDK supports `#:include`; an arbitrary preinstalled SDK may not. Restore
uses only the local action directory and disables NuGet auditing because these scripts
have no package dependencies. An SDK download failure or cancellation before
`Destroy.cs` starts can leave the VM until GCE's `max_run_duration` deletion backstop.
Choose that lifetime to cover boot and the job without an excessive cleanup delay.

`startup-script.sh` is deliberately retained as the fresh-VM bootstrap exception.
The Ubuntu image has no SDK. The launcher downloads the pinned Linux x64 SDK,
checks its SHA-512 digest before extraction, fetches the C# sources supplied in
instance metadata, and publishes `Startup.cs` once before running it. It explicitly
sets `HOME` and `DOTNET_CLI_HOME` for GCE's system service environment. Bootstrap
failures report both a failed stage and a diagnostic metadata attribute.
Updating the SDK requires updating both
its URL and digest from the official .NET release metadata. Provisioning logic,
disk validation, package installation, metadata status reporting, and runner
service configuration live in `StartupProvision.Helpers.cs`.

The SDK and startup sources remain under `/opt/gh-runner-bootstrap` because the
runner service runs the compiled DLL directly to read the private JIT credential and
launch the vendor runner, without another restore or compile. The service stops its
whole control group, including the vendor runner's descendants. Dockerfiles, image builds, and container entrypoints
are unchanged. Python remains available for other tools used by sync jobs.

Run the Linux regression suites without cloud credentials or real instances:

```sh
dotnet run --file scripts/ci/RunScriptTests.cs -- scripts/sync
dotnet run --file scripts/ci/TestSyncMetrics.cs
dotnet run --file scripts/ci/RunScriptTests.cs -- --check .github/actions/gcp-runner
```

Lifecycle tests inject command results; startup tests use temporary filesystem
roots and fake package, disk, service, and metadata commands. Process tests use
stub Docker/Sedge executables and verify follower cancellation. These checks do
not provision a live VM or download the SDK archive.
