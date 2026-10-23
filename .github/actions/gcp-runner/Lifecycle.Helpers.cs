// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

sealed class Lifecycle(Func<string, string[], string?, CancellationToken, Task<CommandResult>> run, Func<string, string?> environment, Func<TimeSpan, CancellationToken, Task> delay)
{
    private string Env(string key, string fallback = "") => environment(key) ?? fallback;
    private string Name => Gcp.InstanceName(Env("RUNNER_LABEL"), Env("GITHUB_RUN_ATTEMPT", "1"));
    private string Project => $"--project={Env("PROJECT_ID")}";
    private void Output(params string[] lines) => File.AppendAllText(Env("GITHUB_OUTPUT"), string.Join('\n', lines) + "\n");
    private void Summary(string line)
    {
        if (Env("GITHUB_STEP_SUMMARY").Length > 0) File.AppendAllText(Env("GITHUB_STEP_SUMMARY"), line + "\n");
    }
    private Task<CommandResult> Cloud(string[] args, CancellationToken token) => run("gcloud", args, null, token);
    private Task<CommandResult> Resolve(CancellationToken token) => Cloud(["compute", "instances", "list", Project, $"--filter=name={Name}", "--format=value(zone.basename())", "--limit=1"], token);
    private Task<CommandResult> Delete(string zone, CancellationToken token, bool asynchronous = false) => Cloud(["compute", "instances", "delete", Name, Project, $"--zone={zone}", "--quiet", "--delete-disks=all", .. asynchronous ? new[] { "--async" } : []], token);

    internal async Task<int> Destroy(CancellationToken token)
    {
        Console.WriteLine($"instance name: {Name}");
        var lookup = await Resolve(token);
        if (lookup.Code != 0) { await delay(TimeSpan.FromSeconds(5), token); lookup = await Resolve(token); }
        var zone = lookup.Output.Trim();
        var preempted = false;
        long timestamp = 0;
        var failed = lookup.Code != 0;
        var lookupFailedInitially = failed;
        var terminated = "lookup-failed";
        if (failed)
        {
            Console.WriteLine($"::error title=GCP runner::could not determine whether {Name} still exists; not treating it as reaped");
            zone = "";
        }
        else if (zone.Length > 0)
        {
            Console.WriteLine($"deleting {Name} in {zone}");
            if ((await Delete(zone, token)).Code == 0) terminated = "deleted-by-action";
            else
            {
                var present = "unconfirmed";
                for (var i = 0; i < 12; i++)
                {
                    lookup = await Resolve(token);
                    if (lookup.Code == 0 && lookup.Output.Trim().Length == 0) { present = "no"; break; }
                    present = lookup.Code == 0 ? "yes" : "unconfirmed";
                    await delay(TimeSpan.FromSeconds(5), token);
                }
                terminated = present == "no" ? "deleted-concurrently" : present == "yes" ? "delete-failed" : "lookup-failed";
                failed = present != "no";
                if (failed) Console.WriteLine($"::error title=GCP runner::could not confirm deletion of {Name}; --max-run-duration will reap it");
                else Console.WriteLine($"{Name} was already being deleted and is now gone");
            }
        }
        else
        {
            var operations = await Cloud(["compute", "operations", "list", Project, $"--zones={Env("ZONES")}", $"--filter=targetLink~/instances/{Name}$", "--format=value(operationType)"], token);
            var ops = operations.Output.Trim();
            Console.WriteLine($"operations found: {(ops.Length == 0 ? "<none>" : ops)}");
            if (ops.Split('\n').Contains("compute.instances.preempted"))
            {
                preempted = true;
                terminated = "preempted";
                var time = await Cloud(["compute", "operations", "list", Project, $"--zones={Env("ZONES")}", $"--filter=targetLink~/instances/{Name}$ AND operationType=compute.instances.preempted", "--format=value(insertTime)", "--limit=1"], token);
                if (DateTimeOffset.TryParse(time.Output.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) timestamp = parsed.ToUnixTimeSeconds();
                Console.WriteLine($"::warning title=GCP runner::{Env("RUNNER_LABEL")} was preempted — infrastructure reclaim, not a sync failure");
                Summary($"⚠️ `{Env("RUNNER_LABEL")}` was preempted — infrastructure reclaim, not a sync failure");
            }
            else if (Env("EXPECT_INSTANCE", "true") != "true")
            {
                terminated = "creation-failed";
                Console.WriteLine($"{Name}: creation did not complete ({(ops.Length == 0 ? "no operations recorded" : ops)})");
            }
            else if (ops.Split('\n').Contains("delete"))
            {
                terminated = "already-deleted";
                Console.WriteLine($"{Name} had already been deleted");
            }
            else
            {
                terminated = ops.Length == 0 ? "unknown" : "terminated";
                Console.WriteLine(ops.Length == 0 ? $"::warning title=GCP runner::{Name} vanished with no operations recorded"
                    : $"::warning title=GCP runner::{Name} was terminated externally ({ops.Replace("\n", ", ", StringComparison.Ordinal)})");
            }
        }
        if (!lookupFailedInitially && Env("GH_TOKEN").Length > 0)
        {
            var result = await run("gh", ["api", "--paginate", $"/repos/{Env("GITHUB_REPOSITORY")}/actions/runners", "-q", $".runners[] | select(.name == \"{Name}\") | .id"], null, token);
            var id = result.Output.Split('\n')[0].Trim();
            if (id.Length > 0)
            {
                Console.WriteLine($"removing leftover runner registration {id}");
                await run("gh", ["api", "--method", "DELETE", $"/repos/{Env("GITHUB_REPOSITORY")}/actions/runners/{id}"], null, token);
            }
        }
        Output($"instance_name={Name}", $"zone={zone}", $"preempted={preempted.ToString().ToLowerInvariant()}", $"preempted_at={timestamp}", $"terminated_by={terminated}");
        return failed ? 1 : 0;
    }

    internal async Task<int> Create(string actionDirectory, CancellationToken token)
    {
        Console.WriteLine($"instance name: {Name}");
        if (!Regex.IsMatch(Env("LOCAL_SSD_COUNT"), "^[0-9]+$") || !int.TryParse(Env("LOCAL_SSD_COUNT"), out var ssdCount))
        {
            Console.WriteLine($"::error title=GCP runner::local_ssd_count must be a non-negative integer, got '{Env("LOCAL_SSD_COUNT")}'");
            return 1;
        }
        var jit = Path.Combine(Env("RUNNER_TEMP"), "jitconfig.b64");
        var runnerId = "";
        var currentZone = "";
        var chosen = "";
        var success = false;
        try
        {
            var request = new JsonObject { ["name"] = Name, ["runner_group_id"] = int.Parse(Env("RUNNER_GROUP_ID")), ["labels"] = new JsonArray(Env("RUNNER_LABEL")), ["work_folder"] = "_work" };
            var registration = await run("gh", ["api", "--method", "POST", $"/repos/{Env("GITHUB_REPOSITORY")}/actions/runners/generate-jitconfig", "-H", "X-GitHub-Api-Version: 2022-11-28", "--input", "-"], request.ToJsonString(), token);
            if (registration.Code != 0) throw new InvalidOperationException("Failed to register JIT runner: " + registration.Error);
            var response = JsonNode.Parse(registration.Output)!;
            runnerId = response["runner"]!["id"]!.ToString();
            var config = response["encoded_jit_config"]!.GetValue<string>();
            Console.WriteLine($"::add-mask::{config}");
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var writer = new StreamWriter(new FileStream(jit, options))) await writer.WriteLineAsync(config.AsMemory(), token);
            Console.WriteLine($"JIT runner id: {runnerId}");
            var models = new List<string> { Env("PROVISIONING_MODEL") };
            if (models[0] == "SPOT" && Env("SPOT_FALLBACK_TO_STANDARD") == "true") models.Add("STANDARD");
            var zones = Env("ZONE_ORDER", "listed") == "rotate" ? Gcp.OrderZones(Name, Env("ZONES")) : Gcp.Zones(Env("ZONES"));
            var offered = false;
            var quotas = new List<string>();
            var used = "";
            var instance = "";
            foreach (var model in models)
            {
                var blocked = new HashSet<string>();
                foreach (var zone in zones)
                {
                    if (blocked.Contains(Gcp.Region(zone))) continue;
                    currentZone = zone;
                    var arguments = CreateArguments(model, actionDirectory, jit, ssdCount);
                    Console.WriteLine($"::group::create {Name} in {zone} ({model})");
                    var result = await Cloud([.. arguments, $"--zone={zone}"], token);
                    Console.WriteLine(result.Error);
                    Console.WriteLine("::endgroup::");
                    if (result.Code == 0) { chosen = zone; used = model; instance = result.Output; break; }
                    var kind = Gcp.Classify(result.Error);
                    if (kind != "not-offered") offered = true;
                    if (kind == "quota") { blocked.Add(Gcp.Region(zone)); quotas.Add($"{model}/{Gcp.Region(zone)}"); }
                    if (kind is "not-offered" or "quota" or "retryable") { await Delete(zone, token); continue; }
                    Console.WriteLine($"::error title=GCP runner::{kind} create failure in {zone}: {result.Error}");
                    return 1;
                }
                if (chosen.Length > 0 || !offered) break;
            }
            if (chosen.Length == 0)
            {
                Console.WriteLine($"::error title=GCP runner::{Env("MACHINE_TYPE")} {(offered ? "could not be created" : "is not offered")} in any of {Env("ZONES")}");
                if (quotas.Count > 0) Console.WriteLine($"::error title=GCP runner::regions at quota: {string.Join(' ', quotas)}");
                if (offered && models.Count == 1 && models[0] == "SPOT" && quotas.Count == 0)
                {
                    Console.WriteLine("::error title=GCP runner::no SPOT capacity in any zone and STANDARD was not requested; re-run later, or re-dispatch with an explicit STANDARD provisioning model if on-demand cost is acceptable");
                    Output("spot_exhausted=true", $"spot_exhausted_at={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
                }
                return 1;
            }
            if (used == "STANDARD") Console.WriteLine($"::warning title=GCP runner::{Env("RUNNER_LABEL")} {(models[0] == "STANDARD" ? "runs on STANDARD (requested explicitly)" : "fell back to STANDARD (no SPOT capacity)")}, billed at on-demand rates");
            var node = JsonNode.Parse(instance)!;
            if (node is JsonArray array) node = array[0]!;
            var ip = node["networkInterfaces"]?[0]?["accessConfigs"]?[0]?["natIP"]?.ToString() ?? "";
            Output($"instance_name={Name}", $"zone={chosen}", $"instance_ip={ip}", $"runner_id={runnerId}", $"provisioning_model_used={used}");
            Summary($"- `{Env("RUNNER_LABEL")}` → {used} in {chosen} ({Env("MACHINE_TYPE")})");
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var lastCheck = TimeSpan.Zero;
            var timeout = TimeSpan.FromSeconds(int.Parse(Env("BOOT_TIMEOUT")));
            while (true)
            {
                var runnerFile = Path.Combine(Env("RUNNER_TEMP"), "runner.json");
                var status = await run("curl", ["-sS", "-o", runnerFile, "-w", "%{http_code}", "-H", $"Authorization: Bearer {Env("GH_TOKEN")}", "-H", "Accept: application/vnd.github+json", "-H", "X-GitHub-Api-Version: 2022-11-28", $"https://api.github.com/repos/{Env("GITHUB_REPOSITORY")}/actions/runners/{runnerId}"], null, token);
                if (status.Code == 0 && status.Output.Trim() == "200" && JsonNode.Parse(await File.ReadAllTextAsync(runnerFile, token))?["status"]?.ToString() == "online")
                {
                    Console.WriteLine($"runner {Env("RUNNER_LABEL")} is online");
                    success = true;
                    return 0;
                }
                if (status.Output.Trim() == "404") throw new InvalidOperationException($"JIT runner {runnerId} disappeared before coming online");
                if (elapsed.Elapsed - lastCheck >= TimeSpan.FromSeconds(30))
                {
                    lastCheck = elapsed.Elapsed;
                    var vm = await Cloud(["compute", "instances", "describe", Name, Project, $"--zone={chosen}", "--format=value(status)"], token);
                    if (vm.Code != 0 || vm.Output.Trim() != "RUNNING") throw new InvalidOperationException($"VM entered {vm.Output.Trim()} before the runner registered");
                    var stage = await Cloud(["compute", "instances", "get-guest-attributes", Name, Project, $"--zone={chosen}", "--query-path=gh-runner/stage", "--format=value(value)"], token);
                    Console.WriteLine($"startup stage: {stage.Output.Trim()}");
                    if (stage.Output.Trim() == "failed")
                    {
                        var error = await Cloud(["compute", "instances", "get-guest-attributes", Name, Project, $"--zone={chosen}", "--query-path=gh-runner/error", "--format=value(value)"], token);
                        throw new InvalidOperationException("startup script failed: " + error.Output.Trim());
                    }
                }
                if (elapsed.Elapsed >= timeout) throw new TimeoutException($"timed out after {Env("BOOT_TIMEOUT")}s waiting for the runner");
                await delay(TimeSpan.FromSeconds(15), token);
            }
        }
        catch (OperationCanceledException) { return 143; }
        catch (Exception exception) { Console.Error.WriteLine($"::error title=GCP runner::{exception.Message}"); return 1; }
        finally
        {
            if (!success)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(token.IsCancellationRequested ? 5 : 120));
                try
                {
                    var zone = chosen.Length > 0 ? chosen : currentZone;
                    if (zone.Length == 0 && !token.IsCancellationRequested) zone = (await Resolve(cleanup.Token)).Output.Trim();
                    if (zone.Length > 0)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            Console.WriteLine("::group::serial console output");
                            var console = await Cloud(["compute", "instances", "get-serial-port-output", Name, Project, $"--zone={zone}"], cleanup.Token);
                            Console.WriteLine(console.Output);
                            Console.WriteLine("::endgroup::");
                        }
                        await Delete(zone, cleanup.Token, token.IsCancellationRequested);
                    }
                }
                catch (Exception exception) { Console.Error.WriteLine($"Instance cleanup failed: {exception.Message}"); }
                try
                {
                    if (runnerId.Length > 0) await run("gh", ["api", "--method", "DELETE", $"/repos/{Env("GITHUB_REPOSITORY")}/actions/runners/{runnerId}"], null, cleanup.Token);
                }
                catch (Exception exception) { Console.Error.WriteLine($"Runner cleanup failed: {exception.Message}"); }
            }
            File.Delete(jit);
        }
    }

    private List<string> CreateArguments(string model, string directory, string jit, int count)
    {
        var args = new List<string> { "compute", "instances", "create", Name, Project };
        foreach (var (flag, key) in new[] { ("machine-type", "MACHINE_TYPE"), ("image-family", "IMAGE_FAMILY"), ("image-project", "IMAGE_PROJECT"), ("boot-disk-type", "BOOT_DISK_TYPE"), ("network", "NETWORK"), ("subnet", "SUBNET"), ("tags", "NETWORK_TAG"), ("service-account", "RUNNER_SERVICE_ACCOUNT"), ("max-run-duration", "MAX_RUN_DURATION") }) args.Add($"--{flag}={Env(key)}");
        args.AddRange([$"--boot-disk-size={Env("BOOT_DISK_SIZE")}GB", $"--boot-disk-device-name={Name}", "--scopes=https://www.googleapis.com/auth/logging.write,https://www.googleapis.com/auth/monitoring.write", "--shielded-secure-boot", "--shielded-vtpm", "--shielded-integrity-monitoring", $"--provisioning-model={model}", "--instance-termination-action=DELETE", "--no-restart-on-failure",
            $"--metadata=enable-guest-attributes=TRUE,serial-port-logging-enable=TRUE,runner-version={Env("RUNNER_VERSION")},data-mount={Env("DATA_MOUNT")},local-ssd-count={Env("LOCAL_SSD_COUNT")}",
            $"--metadata-from-file=startup-script={Path.Combine(directory, "startup-script.sh")},runner-jit-config={jit},runner-startup-source={Path.Combine(directory, "Startup.cs")},runner-startup-helpers={Path.Combine(directory, "Gcp.Helpers.cs")},runner-provision-helpers={Path.Combine(directory, "StartupProvision.Helpers.cs")}",
            $"--labels=gh-run-id={Env("GITHUB_RUN_ID")},gh-run-attempt={Env("GITHUB_RUN_ATTEMPT")},managed-by=gcp-runner-action", "--format=json"]);
        if (model == "SPOT") args.Add("--maintenance-policy=TERMINATE");
        if (!Env("MACHINE_TYPE").EndsWith("-lssd", StringComparison.Ordinal))
            for (var i = 0; i < count; i++) args.Add("--local-ssd=interface=NVME");
        return args;
    }
}
