#!/usr/bin/env bash
# Run test projects one after another and report every one that fails. Small projects share a job so the
# runner setup is paid once, but never run side by side: timing-sensitive tests fail when two test hosts
# share the runner's CPU. Each project runs exactly as it would in a job of its own.
# Environment: PROJECTS lists the projects separated by spaces; COVERAGE=true collects coverage;
# CRASH_RETRY=false fails a crashed test host instead of running it again; TEST_CHUNK passes through to the tests.
set -eo pipefail  # no -u: macOS runners may ship bash 3.2, where an empty array counts as unset

read -ra projects <<< "${PROJECTS:?PROJECTS lists the test projects to run}"

coverage=()
if [ "${COVERAGE:-}" = "true" ]; then
  # Absolute path: with --coverage the test host restarts itself under the coverage controller, and the child
  # rejects a relative settings path
  coverage=(--coverage --coverage-output-format cobertura --coverage-settings
    "$GITHUB_WORKSPACE/src/Nethermind/codecoverage.json")
fi

# Runs one project in its directory. errexit does not apply inside a function called in a condition, so every
# step returns explicitly.
run_project() {
  local project=$1 hang_timeout=8m rc=0
  local run_tests
  if [ "$RUNNER_OS" = "Linux" ]; then
    # Run the prebuilt test host directly: dotnet test would restore and evaluate the project first.
    run_tests=("../artifacts/bin/$project/release/$project" --results-directory TestResults)
  else
    dotnet build "$project.csproj" -c release || return 1  # Windows/macOS: no artifact, build in-job
    hang_timeout=4m  # the in-job build leaves less of the job budget for a hang to be dumped in
    run_tests=(dotnet test --project "$project.csproj" -c release --no-build --no-restore)
  fi
  dotnet build-server shutdown || true  # macOS VB/C# server sometimes fails to shut down; harmless
  # A hung test host is killed with a dump of every thread instead of running into the job
  # timeout, which kills the job without one. The timeout counts inactivity, not runtime, and
  # a mini dump keeps the write and upload inside whatever job budget the hang leaves; a pack
  # gets a larger job timeout, so a hang in its last project still has that budget.
  # A host that dies mid-run leaves a dump, a crash report where the runtime writes one, and a
  # sequence log of the tests in flight; on its own, exit code 7 names neither test nor cause.
  local test_args=(
    --hangdump --hangdump-timeout "$hang_timeout" --hangdump-type Mini
    --crashdump --crashdump-type Mini --crash-report-if-supported)

  "${run_tests[@]}" "${test_args[@]}" "${coverage[@]}" || rc=$?
  if [[ $rc -eq 0 ]]; then
    return 0
  elif [[ $rc -eq 1 || $rc -eq 2 ]]; then  # 2: at least one test failed, which a retry would only mask
    return 1
  elif compgen -G "TestResults/*_hang.*" > /dev/null; then
    echo "::error::$project: test host hung and was dumped; see the hang-dump artifact"
    return 1
  elif [ "${CRASH_RETRY:-true}" = "false" ]; then
    echo "::error::$project: test process crashed (exit code $rc)"
    return 1
  fi
  echo "::warning::$project: test process crashed (exit code $rc), retrying..."
  echo "crashed=true" >> "$GITHUB_OUTPUT"  # keep the dump the crash just wrote, even if the retry passes
  "${run_tests[@]}" "${test_args[@]}" "${coverage[@]}"
}

# Each project of a pack must write its own report, as it did when it had a job of its own.
coverage_written() {
  [ "${COVERAGE:-}" != "true" ] || compgen -G "TestResults/*.cobertura.xml" > /dev/null ||
    { echo "::error::$1: wrote no coverage report"; return 1; }
}

failed=()
for project in "${projects[@]}"; do
  echo "::group::$project"
  (cd "src/Nethermind/$project" && run_project "$project" && coverage_written "$project") || failed+=("$project")
  echo "::endgroup::"
done

if ((${#failed[@]})); then
  echo "::error::Failed: ${failed[*]}"
  exit 1
fi
