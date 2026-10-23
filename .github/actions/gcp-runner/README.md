# GCP runner scripts

The composite action sets up .NET 10.0.402 before running `Create.cs` or `Destroy.cs`.
Both programs preserve the action's environment inputs and GitHub output names.
Runner names include the run attempt so retries can register a new JIT runner.

`startup-script.sh` is deliberately retained as the fresh-VM bootstrap exception.
The Ubuntu image has no SDK. The launcher downloads the pinned Linux x64 SDK,
checks its SHA-512 digest before extraction, fetches the C# sources supplied in
instance metadata, and runs `Startup.cs`. Updating the SDK requires updating both
its URL and digest from the official .NET release metadata. Provisioning logic,
disk validation, package installation, metadata status reporting, and runner
service configuration live in `StartupProvision.Helpers.cs`.

The SDK and startup sources remain under `/opt/gh-runner-bootstrap` because the
runner service uses the C# entry point to read the private JIT credential and
launch the vendor runner. Dockerfiles, image builds, and container entrypoints
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
