#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include ../version.helpers.cs
#:include script-process.helpers.cs
using System.Text.RegularExpressions;

if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run release workflow fixtures on Linux.");
var root = ScriptVersion.Root();
var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/release-bootnode.yml")).Replace("\r\n", "\n");
var temporary = Directory.CreateTempSubdirectory("bootnode release ");
var saved = new Dictionary<string, string?>();
var count = 0;
const string mock = """
sudo() { :; }
git() {
  case "$1" in
    check-ref-format) command git "$@" ;;
    ls-remote)
      echo TAG_LOOKUP >&2
      [[ "$*" == "ls-remote --refs origin refs/tags/bootnode-$VERSION_PREFIX" ]] || return 99
      [[ "$TAG_STATE" != error ]] || return 128
      if [[ "$TAG_STATE" != absent ]]; then
        printf '%s\t%s\n' "$TAG_STATE" refs/tags/bootnode-1.0.0
      fi
      ;;
    *) return 99 ;;
  esac
}
gh() {
  case "$1 $2" in
    'release view')
      [[ "$RELEASE_EXISTS" == true ]] || return 1
      if [[ "${4:-}" == --json ]]; then
        [[ "$5" == isDraft,targetCommitish ]] || return 99
        printf '{"isDraft":%s,"targetCommitish":"%s"}\n' "$IS_DRAFT" "$DRAFT_TARGET"
      fi
      ;;
    'release create') RELEASE_EXISTS=true; IS_DRAFT=true; DRAFT_TARGET=$GITHUB_SHA ;;
    'release upload') echo ASSETS_UPLOADED ;;
    'release edit') echo RELEASE_PUBLISHED ;;
    'api repos/NethermindEth/nethermind/commits/'*)
      case "${2##*/commits/}" in
        refs/tags/bootnode-1.0.0|bootnode-1.0.0)
          [[ "$TAG_STATE" != absent ]] || return 1
          [[ "$TAG_COMMIT" != error ]] || return 1
          printf '%s\n' "$TAG_COMMIT"
          ;;
        A|B) printf '%s\n' "$DRAFT_TARGET" ;;
        *) return 1 ;;
      esac
      ;;
    *) return 99 ;;
  esac
}
""";
try
{
    foreach (var (version, success) in new[]
    {
        ("1.0.0", true), ("1.10.0", true), (new string('1', 128), true), (new string('1', 129), false),
        ("", false), (".1.0.0", false), ("-1.0.0", false), ("1.0.0+build", false), ("1.0.0/extra", false),
        ("1.0.0 extra", false), ("1.0.0\nextra", false), ("1..0", false), ("1.0.0.lock", false)
    })
    {
        var scripts = Directory.CreateDirectory(Path.Combine(temporary.FullName, "scripts"));
        foreach (var file in new[] { "version.cs", "version.helpers.cs" }) File.Copy(Path.Combine(root, "scripts", file), Path.Combine(scripts.FullName, file), overwrite: true);
        var project = Path.Combine(temporary.FullName, "tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, $"<Project>\n  <PropertyGroup>\n    <VersionPrefix>{version}</VersionPrefix>\n  </PropertyGroup>\n</Project>\n");
        Defaults();
        Set("VERSION_PREFIX", version);
        var result = await Run("Detect version", temporary.FullName);
        var output = result.Output + result.Error;
        Check((result.Code == 0) == success && output.Contains("TAG_LOOKUP", StringComparison.Ordinal) == success &&
            (success || output.Contains("must produce valid Docker and Git tags", StringComparison.Ordinal)), $"version {version}", output);
    }
    foreach (var step in new[] { "Detect version", "Publish" })
        foreach (var (name, state, commit, exists, draft, target, success) in new[]
        {
            ("new release", "absent", "A", "false", "true", "B", true),
            ("new draft", "absent", "A", "true", "true", "B", true),
            ("stale draft", "absent", "A", "true", "true", "A", false),
            ("stale lightweight tag", "A", "A", "true", "true", "B", false),
            ("stale annotated tag", "tag-object", "A", "true", "true", "B", false),
            ("tag overrides draft", "B", "B", "true", "true", "A", true),
            ("matching annotated tag", "tag-object", "B", "true", "true", "A", true),
            ("published rerun", "B", "B", "true", "false", "A", true),
            ("published stale tag", "A", "A", "true", "false", "B", false),
            ("published missing tag", "absent", "A", "true", "false", "B", false),
            ("retained tag without release", "A", "A", "false", "true", "B", false),
            ("matching tag without release", "B", "B", "false", "true", "B", true),
            ("tag lookup failure", "error", "A", "true", "true", "B", false),
            ("tag resolution failure", "A", "error", "true", "true", "B", false),
            ("draft resolution failure", "absent", "A", "true", "true", "error", false)
        })
        {
            Defaults();
            foreach (var (key, value) in new[] { ("TAG_STATE", state), ("TAG_COMMIT", commit), ("RELEASE_EXISTS", exists), ("IS_DRAFT", draft), ("DRAFT_TARGET", target) }) Set(key, value);
            var result = await Run(step, root);
            var output = result.Output + result.Error;
            Check((result.Code == 0) == success && (success || output.Contains("::error::", StringComparison.Ordinal)) &&
                output.Contains("ASSETS_UPLOADED", StringComparison.Ordinal) == (success && step == "Publish") &&
                output.Contains("RELEASE_PUBLISHED", StringComparison.Ordinal) == (success && step == "Publish"), $"{step}/{name}", output);
        }
    Console.WriteLine($"Passed {count} tests.");
}
finally
{
    foreach (var (key, value) in saved) Environment.SetEnvironmentVariable(key, value);
    temporary.Delete(recursive: true);
}

void Set(string key, string value)
{
    saved.TryAdd(key, Environment.GetEnvironmentVariable(key));
    Environment.SetEnvironmentVariable(key, value);
}

void Defaults()
{
    foreach (var (key, value) in new[]
    {
        ("VERSION_PREFIX", "1.0.0"), ("TAG_STATE", "absent"), ("TAG_COMMIT", "A"), ("RELEASE_EXISTS", "false"),
        ("IS_DRAFT", "true"), ("DRAFT_TARGET", "B"), ("GITHUB_SHA", "B"), ("GITHUB_REPOSITORY", "NethermindEth/nethermind"),
        ("RELEASE_TAG", "bootnode-1.0.0"), ("RELEASE_NAME", "Bootnode 1.0.0"), ("RELEASE_VERSION", "1.0.0"),
        ("PACKAGE_DIR", "packages"), ("INPUT_PUBLISH_LATEST", "false"), ("GITHUB_OUTPUT", "/dev/null"), ("GITHUB_ENV", "/dev/null")
    }) Set(key, value);
}

async Task<(int Code, string Output, string Error)> Run(string step, string directory)
{
    var match = (step == "Detect version" ? DetectVersionStep() : PublishStep()).Match(workflow);
    if (!match.Success) throw new InvalidOperationException($"Missing workflow step: {step}");
    var script = Path.Combine(temporary.FullName, "step.sh");
    File.WriteAllText(script, mock + "\n" + Indentation().Replace(match.Groups["script"].Value, ""));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    return await ScriptProcess.Run("bash", ["-e", script], timeout.Token, workingDirectory: directory);
}

void Check(bool condition, string name, string output)
{
    if (!condition) throw new InvalidOperationException($"{name}: {output}");
    Console.WriteLine($"PASS: {name}");
    count++;
}

partial class Program
{
    [GeneratedRegex(@"(?ms)^      - name: Detect version\n.*?^        run: \|\n(?<script>(?:^          [^\n]*\n|^\n)+)")]
    private static partial Regex DetectVersionStep();
    [GeneratedRegex(@"(?ms)^      - name: Publish\n.*?^        run: \|\n(?<script>(?:^          [^\n]*\n|^\n)+)")]
    private static partial Regex PublishStep();
    [GeneratedRegex("(?m)^          ")]
    private static partial Regex Indentation();
}
