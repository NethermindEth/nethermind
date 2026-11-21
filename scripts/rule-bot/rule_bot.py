#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import argparse
import datetime
import fnmatch
import glob
import json
import os
import random
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = "NethermindEth/nethermind"
GATES = dict(prs=10, commenters=3, human=5, validity=0.6)
VALUE_MIN = 5
SIZE_MAX = 60
SAMPLE_SIZE = 20
SAMPLE_MIN_TRUE = 18
BOT_LABEL = "rule-bot"
COOLDOWN_DAYS = 56
ROUTE_B_TRIES = 3
UNSHIPPED = "src/Nethermind/Nethermind.Analyzers/AnalyzerReleases.Unshipped.md"
EDITORCONFIG = ".editorconfig"
OVERLAY_SECTIONS = {"[*.cs]": "warning", "[src/Nethermind/{*Test*,*Benchmark*}/**.cs]": "none", "[tools/*/{*Test*,*Benchmark*}/**.cs]": "none"}
GUARDED_PREFIXES = (".github/", ".agents/", "scripts/")
ANALYZER_DIRS = ("src/Nethermind/Nethermind.Analyzers/", "src/Nethermind/Nethermind.Analyzers.Test/")
FIX_ROOTS = ("src/", "tools/")
FIX_DENIED_PREFIXES = tuple(f"src/Nethermind/{p}/" for p in (
    "Nethermind.Analyzers", "Nethermind.Analyzers.Test", "Nethermind.Test.Analyzers", "Nethermind.Test.Analyzers.CodeFixes",
    "Nethermind.Serialization.JsonGenerator", "Nethermind.Serialization.SszGenerator", "Nethermind.Serialization.FluentRlp.Generator",
    "Nethermind.JsonRpc.SourceGenerator", "Nethermind.JsonRpc.SourceGenerator.Build", "Nethermind.BeaconChain.Generators"))
TRUSTED_ASSOCIATIONS = {"OWNER", "MEMBER", "COLLABORATOR"}
BOTS = {"claude", "copilot-pull-request-reviewer", "github-actions", "coderabbitai", "gemini-code-assist", "cursor",
        "chatgpt-codex-connector", "dependabot", "codecov"}
AGREE = re.compile(r"\b(fixed|done|addressed|good catch|nice catch|agreed|agree|updated|changed|resolved|thanks|applied|removed|reverted|will fix|yep|yes)\b", re.I)
REBUT = re.compile(r"\b(not (an issue|needed|applicable|relevant|true|correct)|false positive|intended|intentional|by design|won'?t fix|no need|disagree|not necessary|this is fine|irrelevant|incorrect|wrong)\b", re.I)
DIAG = re.compile(r"^(?P<path>[^\s(][^(]*)\((?P<line>\d+),(?P<col>\d+)\): (?P<sev>warning|error) (?P<id>[A-Z]+\d+): (?P<msg>.*?)(?: \[(?P<proj>[^\]]+)\])?$")
SEVERITY = re.compile(r"^\s*dotnet_diagnostic\.([A-Z]+\d+)\.severity\s*=\s*(\w+)", re.M)

GRAPHQL = """
query($q:String!, $after:String) {
  rateLimit { remaining resetAt }
  search(query:$q, type:ISSUE, first:25, after:$after) {
    issueCount
    pageInfo { hasNextPage endCursor }
    nodes { ... on PullRequest {
      number updatedAt mergedAt
      author { login }
      reviewThreads(first:100) { nodes {
        isResolved isOutdated path line originalLine
        comments(first:15) { nodes { author { login } authorAssociation body url createdAt } }
      } }
    } }
  }
}
"""


def load(path, default=None):
    if default is not None and not os.path.exists(path):
        return default
    with open(path) as f:
        return json.load(f)


def save(path, data):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w") as f:
        json.dump(data, f, indent=1)


def gh(*args, check=True):
    p = subprocess.run(["gh", *args], capture_output=True, text=True)
    if check and p.returncode != 0:
        raise SystemExit(f"gh {' '.join(args[:3])} failed: {p.stderr.strip()[:300]}")
    return p.stdout


def login(node):
    return (node.get("author") or {}).get("login") or "ghost"


def is_bot(name):
    return name in BOTS or name.endswith("[bot]")


def windows(since, today, days=6):
    while since <= today:
        until = min(since + datetime.timedelta(days=days), today)
        yield since, until
        since = until + datetime.timedelta(days=1)


def collect(args):
    state_file = os.path.join(args.state, "collect_state.json")
    baseline = load(os.path.join(HERE, "baseline.json"))
    first = datetime.date.fromisoformat(baseline["until"]) + datetime.timedelta(days=1)
    state = load(state_file, {"since": first.isoformat()})
    raw = os.path.join(args.state, "raw")
    os.makedirs(raw, exist_ok=True)
    today = datetime.date.fromisoformat(args.today) if args.today else datetime.datetime.now(datetime.timezone.utc).date()
    for since, until in windows(datetime.date.fromisoformat(state["since"]), today):
        tag = f"{since}_{until}"
        for stale in glob.glob(f"{raw}/{tag}_*.json"):
            os.remove(stale)
        after, page = None, 0
        while True:
            call = ["api", "graphql", "-f", f"query={GRAPHQL}", "-f", f"q=repo:{REPO} is:pr updated:{since}..{until}"]
            if after:
                call += ["-f", f"after={after}"]
            data = None
            for attempt in range(5):
                p = subprocess.run(["gh", *call], capture_output=True, text=True)
                if p.returncode == 0:
                    data = json.loads(p.stdout)
                    break
                time.sleep(20 * (attempt + 1))
            if data is None:
                raise SystemExit(f"collect: GitHub API failed for {tag}")
            save(f"{raw}/{tag}_{page:03d}.json", data)
            s = data["data"]["search"]
            if s["issueCount"] > 1000:
                raise SystemExit(f"collect: {tag} has {s['issueCount']} PRs, over the 1000 search cap")
            print(f"collect: {tag} page {page} PRs {len(s['nodes'])}/{s['issueCount']}")
            if not s["pageInfo"]["hasNextPage"]:
                break
            after, page = s["pageInfo"]["endCursor"], page + 1
        state["since"] = until.isoformat()
    state["last_run"] = datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")
    save(state_file, state)
    print(f"collect: next run starts at {state['since']}")


def classify(thread, pr_author):
    comments = thread["comments"]["nodes"]
    replies = " \n".join(c["body"] for c in comments[1:] if login(c) == pr_author)
    agreed = bool(AGREE.search(replies))
    if REBUT.search(replies) and not AGREE.search(replies[:60]):
        return "invalid"
    if thread["isOutdated"] or agreed:
        return "valid"
    return "likely" if thread["isResolved"] else "unknown"


def findings_from(prs, after):
    for pr in prs.values():
        author = login(pr)
        for t in pr["reviewThreads"]["nodes"]:
            cs = t["comments"]["nodes"]
            if not cs or login(cs[0]) == author or cs[0]["createdAt"][:10] <= after:
                continue
            first = cs[0]
            yield dict(pr=pr["number"], author=login(first), bot=is_bot(login(first)), path=t["path"],
                       line=t["line"] or t["originalLine"], created=first["createdAt"], body=first["body"],
                       url=first["url"], association=first.get("authorAssociation"), valid=classify(t, author))


def findings(args):
    prs = {}
    for fn in sorted(glob.glob(os.path.join(args.state, "raw", "*.json"))):
        for n in load(fn)["data"]["search"]["nodes"]:
            if n and "number" in n and (n["number"] not in prs or n["updatedAt"] >= prs[n["number"]]["updatedAt"]):
                prs[n["number"]] = n
    until = load(os.path.join(HERE, "baseline.json"))["until"]
    rows = list(findings_from(prs, until))
    with open(os.path.join(args.data, "findings.jsonl"), "w") as f:
        for r in rows:
            f.write(json.dumps(r) + "\n")
    print(f"findings: {len(prs)} PRs, {len(rows)} new inline review comments after {until}")


def counted_valid(r):
    return r["valid"] == "valid" if r["bot"] else r["valid"] in ("valid", "likely")


def cluster_stats(rows):
    return dict(n=len(rows), prs=len({r["pr"] for r in rows}), commenters=len({r["author"] for r in rows}),
                human=sum(not r["bot"] for r in rows), valid=sum(counted_valid(r) for r in rows))


def gate_cluster(base, new_rows):
    d = cluster_stats(new_rows)
    prs = set(base.get("pr_set", [])) | {r["pr"] for r in new_rows}
    authors = set(base.get("author_set", [])) | {r["author"] for r in new_rows}
    st = dict(n=base["n"] + d["n"], prs=len(prs), commenters=len(authors),
              human=base["human"] + d["human"], valid=base["valid"] + d["valid"], new=d)
    st["validity"] = round(st["valid"] / st["n"], 2) if st["n"] else 0.0
    st["fails"] = [g for g, t in GATES.items() if st[g] < t]
    st["pass"] = not st["fails"]
    return st


def gates(args):
    baseline = load(os.path.join(HERE, "baseline.json"))
    patterns = {k: re.compile(v, re.I) for k, v in load(os.path.join(HERE, "clusters.json")).items()}
    path = os.path.join(args.data, "findings.jsonl")
    rows = [json.loads(l) for l in open(path)] if os.path.exists(path) else []
    out = {}
    for k, rx in patterns.items():
        mine = [r for r in rows if rx.search(r["body"][:400])]
        base = baseline["clusters"].get(k, dict(n=0, valid=0, human=0))
        out[k] = gate_cluster(base, mine)
        out[k]["new_examples"] = [dict(url=r["url"], author=r["author"], path=r["path"], line=r["line"], body=r["body"][:500])
                                  for r in mine if counted_valid(r) and not r["bot"] and r.get("association") in TRUSTED_ASSOCIATIONS][:5]
    save(os.path.join(args.data, "gates.json"), out)
    for k, s in sorted(out.items(), key=lambda x: -x[1]["prs"]):
        print(f"gates: {k:11s} PRs {s['prs']:4d} commenters {s['commenters']:3d} human {s['human']:4d} "
              f"validity {s['validity']:.2f} (+{s['new']['n']} new) {'PASS' if s['pass'] else 'FAIL ' + ','.join(s['fails'])}")


def editorconfig_rules(text):
    section, rows = None, []
    for i, line in enumerate(text.splitlines(), 1):
        s = line.strip()
        if s.startswith("[") and s.endswith("]"):
            section = s[1:-1]
            continue
        m = SEVERITY.match(line)
        if m:
            rows.append(dict(id=m[1], severity=m[2], section=section, line=i))
    return rows


def enforced_on_master(rule_id, rows, sdk_defaults):
    general = [r for r in rows if r["id"] == rule_id and r["section"] in ("*", "*.cs", "*.{cs,vb}")]
    if general:
        return general[-1]["severity"] in ("warning", "error")
    return sdk_defaults.get(rule_id) in ("warning", "error")


def catalog(args):
    rows = editorconfig_rules(open(os.path.join(args.repo, ".editorconfig")).read())
    sdk_defaults = {}
    for g in glob.glob(os.path.join(args.sdk, "analyzers/build/config/analysislevel_*_default.globalconfig")):
        sdk_defaults.update({m[1]: m[2] for m in SEVERITY.finditer(open(g).read())})
    for g in glob.glob(os.path.join(args.repo, "**/*.globalconfig"), recursive=True):
        rows += [dict(id=m[1], severity=m[2], section="*", line=0) for m in SEVERITY.finditer(open(g).read())]
    routes = load(os.path.join(HERE, "routes.json"))
    ids = sorted({i for r in routes.values() for i in r["ids"]})
    out = {i: dict(enforced=enforced_on_master(i, rows, sdk_defaults)) for i in ids}
    save(os.path.join(args.data, "catalog.json"), out)
    print("catalog: already enforced on master:", " ".join(i for i in ids if out[i]["enforced"]) or "none")


def neth_ids(repo):
    ids = set()
    for fn in glob.glob(os.path.join(repo, "src/Nethermind/Nethermind.Analyzers/AnalyzerReleases.*.md")):
        ids |= {int(x) for x in re.findall(r"^NETH(\d{3})\b", open(fn).read(), re.M)}
    return ids


def next_neth_id(taken, reserved):
    n = max(taken | reserved | {0}) + 1
    return f"NETH{n:03d}"


def recently_tried(history, cluster, today):
    t = history.get(cluster)
    return bool(t) and (today - datetime.date.fromisoformat(t)).days < COOLDOWN_DAYS


def plan(args):
    g = load(os.path.join(args.data, "gates.json"))
    cat = load(os.path.join(args.data, "catalog.json"))
    routes = load(os.path.join(HERE, "routes.json"))
    history = load(os.path.join(args.state, "route_b_history.json"), {})
    today = datetime.datetime.now(datetime.timezone.utc).date()
    measure, queue = [], []
    for k, st in sorted(g.items(), key=lambda x: -x[1]["prs"]):
        r = routes.get(k, dict(route="?", ids=[]))
        if not st["pass"]:
            continue
        if r["route"] == "A":
            measure += [i for i in r["ids"] if i not in measure and not cat.get(i, {}).get("enforced")]
        elif r["route"] == "B" and not recently_tried(history, k, today):
            queue.append(k)
    bot_open = [] if args.offline else json.loads(gh("pr", "list", "-R", REPO, "--state", "open", "--label", BOT_LABEL, "--json", "number"))
    if bot_open:
        print(f"plan: no analyzer attempted, bot PR already open: #{bot_open[0]['number']}")
        queue = []
    queue = queue[:ROUTE_B_TRIES]
    route_b = queue[0] if queue else None
    reserved = set()
    if not args.offline:
        for pr in json.loads(gh("pr", "list", "-R", REPO, "--state", "open", "--search", "NETH0 in:title", "--json", "title", "--limit", "50")):
            reserved |= {int(x) for x in re.findall(r"NETH(\d{3})", pr["title"])}
    out = dict(measure_ids=measure, route_b=route_b, route_b_id=next_neth_id(neth_ids(args.repo), reserved) if route_b else None,
               route_b_queue=[dict(cluster=k, why=routes[k]["why"]) for k in queue])
    save(os.path.join(args.data, "plan.json"), out)
    with open(os.path.join(args.data, "measure_ids.txt"), "w") as f:
        f.write("\n".join(measure) + "\n")
    print(f"plan: route A ids to measure: {' '.join(measure) or 'none'}")
    print(f"plan: route B clusters to try: {' '.join(queue) or 'none'} {out['route_b_id'] or ''}")
    if args.github_output:
        with open(args.github_output, "a") as f:
            f.write(f"route_b={route_b or ''}\nroute_b_id={out['route_b_id'] or ''}\n")


def overlay(ids, exclude_tests=False):
    s = "\n[*.cs]\n" + "".join(f"dotnet_diagnostic.{i}.severity = warning\n" for i in ids)
    if exclude_tests:
        for sec in ("src/Nethermind/{*Test*,*Benchmark*}/**.cs", "tools/*/{*Test*,*Benchmark*}/**.cs"):
            s += f"\n[{sec}]\n" + "".join(f"dotnet_diagnostic.{i}.severity = none\n" for i in ids)
    return s


def apply_overlay(args):
    ids = [i for i in open(args.ids).read().split() if i] + list(args.extra or [])
    with open(os.path.join(args.repo, ".editorconfig"), "a") as f:
        f.write(overlay(ids, args.exclude_tests))
    print(f"overlay: {' '.join(ids)} at warning{' (tests and benchmarks excluded)' if args.exclude_tests else ''}")


def scope(rel):
    parts = rel.split("/")
    if "obj" in parts or "artifacts" in parts:
        return "generated"
    project = parts[2] if parts[0] in ("src", "tools") and len(parts) > 3 else ""
    if fnmatch.fnmatch(project, "*Test*") or fnmatch.fnmatch(project, "*Benchmark*"):
        return "test"
    return "prod"


def parse_diagnostics(lines, root, ids):
    root = root.rstrip("/") + "/"
    seen = {}
    for line in lines:
        m = DIAG.match(line.rstrip("\n"))
        if not m or m["id"] not in ids:
            continue
        path = m["path"].replace("\\", "/")
        rel = path[len(root):] if path.startswith(root) else path
        if rel.startswith("/"):
            continue
        key = (rel, int(m["line"]), int(m["col"]), m["id"])
        seen.setdefault(key, dict(id=m["id"], file=rel, line=key[1], col=key[2], scope=scope(rel), msg=m["msg"]))
    return sorted(seen.values(), key=lambda h: (h["id"], h["file"], h["line"], h["col"]))


def solution_of(log_name):
    return os.path.basename(log_name)[:-len(".log")].replace("__", "/")


def log_name_of(solution):
    return solution.replace("/", "__") + ".log"


def ci_solutions(workflows):
    out = []
    m = re.search(r"^\s*solution:\s*\[([^\]]*)\]", open(os.path.join(workflows, "build-solutions.yml")).read(), re.M)
    out += [f"src/Nethermind/{s.strip()}.slnx" for s in m[1].split(",")] if m else []
    out += [f"tools/{s}" for s in re.findall(r"^\s+-\s+([\w./-]+\.slnx)\s*$", open(os.path.join(workflows, "build-tools.yml")).read(), re.M)]
    return out


def solutions_cmd(args):
    s = ci_solutions(args.workflows)
    print(json.dumps(s))
    if args.github_output:
        with open(args.github_output, "a") as f:
            f.write(f"solutions={json.dumps(s)}\n")


def hits(args):
    ids = set(args.ids.split(","))
    found, logs = [], sorted(glob.glob(os.path.join(args.logs, "**", "*.log"), recursive=True))
    for fn in logs:
        for h in parse_diagnostics(open(fn, errors="replace").readlines(), args.root, ids):
            h["solution"] = solution_of(fn)
            found.append(h)
    uniq = {}
    for h in found:
        k = (h["file"], h["line"], h["col"], h["id"])
        uniq.setdefault(k, dict(h, solutions=[]))["solutions"].append(h["solution"])
    found = sorted(uniq.values(), key=lambda h: (h["id"], h["file"], h["line"], h["col"]))
    counts = {i: dict(prod=0, test=0, generated=0) for i in sorted(ids)}
    for h in found:
        counts[h["id"]][h["scope"]] += 1
    save(args.out, dict(counts=counts, hits=found, solutions=[solution_of(x) for x in logs]))
    print(f"hits: {len(logs)} build logs: {' '.join(solution_of(x) for x in logs)}")
    for i, c in counts.items():
        print(f"hits: {i:8s} prod {c['prod']:6d} test {c['test']:6d}")


def rebuild_set(hits_data, ids):
    return sorted({s for h in hits_data["hits"] if h["id"] in ids and h["scope"] == "prod" for s in h["solutions"]})


def package_cmd(args):
    c = load(os.path.join(args.data, "candidates.json"))["chosen"]
    h = load(os.path.join(args.data, "hits.json"))
    mine = [x for x in h["hits"] if x["id"] in c["ids"] and x["scope"] == "prod"]
    with open(os.path.join(args.data, "fix-hits.txt"), "w") as f:
        for x in mine:
            f.write(f"{x['id']}\t{x['file']}:{x['line']}:{x['col']}\t{x['msg']}\n")
    with open(os.path.join(args.data, "package-ids.txt"), "w") as f:
        f.write("\n".join(c["ids"]) + "\n")
    s = rebuild_set(h, c["ids"])
    print(f"package: {c['slug']} {len(mine)} prod hits to fix, rebuild {' '.join(s)}")
    if args.github_output:
        with open(args.github_output, "a") as f:
            f.write(f"rebuild={' '.join(s)}\nids={','.join(c['ids'])}\n")


def verdict(prod, enforced):
    if enforced:
        return "skip: already enforced on master"
    if prod < VALUE_MIN:
        return f"skip: {prod} prod hits < {VALUE_MIN}"
    if prod > SIZE_MAX:
        return f"defer: {prod} prod hits > {SIZE_MAX}"
    return "propose"


def group_by_cap(ids, counts):
    groups, group, total = [], [], 0
    for i in sorted(ids, key=lambda i: counts[i]):
        if total + counts[i] > SIZE_MAX and group:
            groups.append(group)
            group, total = [], 0
        group.append(i)
        total += counts[i]
    if group:
        groups.append(group)
    return groups


def open_prs_for(rule_id):
    out = gh("pr", "list", "-R", REPO, "--state", "open", "--search", f"{rule_id} in:title,body", "--json", "number", "--limit", "20", check=False)
    return sorted(x["number"] for x in json.loads(out or "[]"))


def propose(args):
    g = load(os.path.join(args.data, "gates.json"))
    cat = load(os.path.join(args.data, "catalog.json"))
    routes = load(os.path.join(HERE, "routes.json"))
    pl = load(os.path.join(args.data, "plan.json"))
    counts = load(os.path.join(args.data, "hits.json"))["counts"]
    rb = load(os.path.join(args.data, "route_b.json"), {})
    decisions, packages = [], []
    for k, st in sorted(g.items(), key=lambda x: -x[1]["prs"]):
        r = routes.get(k, {})
        if not st["pass"] or r.get("route") != "A":
            continue
        ok = []
        for i in r["ids"]:
            c = counts.get(i, {}).get("prod", 0)
            v = verdict(c, cat.get(i, {}).get("enforced"))
            decisions.append(dict(cluster=k, id=i, route="A", prod=c, test=counts.get(i, {}).get("test", 0), verdict=v))
            if v == "propose":
                ok.append(i)
        for grp in group_by_cap(ok, {i: counts[i]["prod"] for i in ok}):
            packages.append(dict(route="A", cluster=k, ids=grp, prod=sum(counts[i]["prod"] for i in grp),
                                 test=sum(counts[i]["test"] for i in grp)))
    if pl.get("route_b"):
        i, cluster = pl["route_b_id"], rb.get("cluster", pl["route_b"])
        c = counts.get(i, {}).get("prod", 0)
        fails = [f for f, ok in (("analyzer tests", rb.get("tests_ok")), ("red control", rb.get("red_ok")),
                                 ("path guard", rb.get("guard_ok")),
                                 ("precision sample", rb.get("precision", {}).get("ok"))) if not ok]
        v = verdict(c, False) if rb.get("analyzer") else "propose"
        if v == "propose" and fails:
            v = "skip: " + (rb.get("skip") or ", ".join(fails) + " failed")
        decisions.append(dict(cluster=cluster, id=i, route="B", prod=c, test=counts.get(i, {}).get("test", 0), verdict=v))
        if v == "propose":
            packages.append(dict(route="B", cluster=cluster, ids=[i], prod=c, test=counts.get(i, {}).get("test", 0),
                                 analyzer=rb.get("analyzer")))
    for p in packages:
        p["slug"] = f"{p['cluster']}-{'-'.join(i.lower() for i in p['ids'])}"
        p["open_prs"] = [] if args.offline or p["route"] == "B" else sorted({n for i in p["ids"] for n in open_prs_for(i)})
    bot_open = [] if args.offline else json.loads(gh("pr", "list", "-R", REPO, "--state", "open", "--label", BOT_LABEL, "--json", "number"))
    chosen = next((p for p in packages if not p["open_prs"] and p["route"] == "B"), None) or next((p for p in packages if not p["open_prs"]), None)
    if bot_open:
        chosen = None
    out = dict(decisions=decisions, packages=packages, bot_open_prs=[x["number"] for x in bot_open], chosen=chosen)
    save(os.path.join(args.data, "candidates.json"), out)
    for d in decisions:
        print(f"propose: route {d['route']} {d['cluster']:11s} {d['id']:8s} prod {d['prod']:6d} -> {d['verdict']}")
    for p in packages:
        dup = f" duplicate of open #{', #'.join(map(str, p['open_prs']))}" if p["open_prs"] else ""
        print(f"package: route {p['route']} {p['slug']} prod {p['prod']} test {p['test']}{dup}")
    if bot_open:
        print(f"package: none opened, bot PR already open: #{bot_open[0]['number']}")
    print(f"package: chosen {chosen['slug'] if chosen else 'none'}")
    if args.github_output:
        with open(args.github_output, "a") as f:
            f.write(f"slug={chosen['slug'] if chosen else ''}\nroute={chosen['route'] if chosen else ''}\n")


def overlay_only(diff, ids=()):
    section = None
    for line in diff:
        if line.startswith("-"):
            return False
        text = line[1:].strip()
        if not text:
            continue
        if text.startswith("["):
            if text not in OVERLAY_SECTIONS:
                return False
            section = text
        else:
            m = re.fullmatch(r"dotnet_diagnostic\.([A-Z]+\d+)\.severity = (\w+)", text)
            if not m or section is None or m[1] not in ids or m[2] != OVERLAY_SECTIONS[section]:
                return False
    return True


def fix_allowed(path):
    return path.startswith(FIX_ROOTS) and not path.startswith(FIX_DENIED_PREFIXES) and scope(path) == "prod"


def check_paths(changes, unshipped_diff, new_id, fix_files=(), editorconfig_diff=(), ids=()):
    bad = []
    for status, path in changes:
        if path == UNSHIPPED:
            removed = [l for l in unshipped_diff if l.startswith("-") and not l.startswith("---")]
            added = [l for l in unshipped_diff if l.startswith("+") and not l.startswith("+++")]
            if status != "M" or removed or not new_id or len(added) != 1 or not added[0][1:].startswith(f"{new_id} |"):
                bad.append(f"{path}: only one appended row for {new_id or 'the new rule'} is allowed")
        elif path == ".editorconfig":
            if status != "M" or not overlay_only(editorconfig_diff, ids):
                bad.append(f"{path}: only the rule overlay may be appended")
        elif not path.endswith(".cs") or path.startswith(GUARDED_PREFIXES):
            bad.append(f"{path}: outside the paths the bot may change")
        elif not (new_id and status == "A" and path.startswith(ANALYZER_DIRS)) and not (status == "M" and path in fix_files and fix_allowed(path)):
            bad.append(f"{path}: not a file with a listed hit{' or a new file under the analyzer projects' if new_id else ''}")
    return bad


def patch_changes(text):
    changes, udiff, edits, path, status, in_unshipped, in_editorconfig, old_left, new_left = [], [], [], None, None, False, False, 0, 0

    def flush():
        if path:
            changes.append((status, path))

    for line in text.splitlines():
        if old_left > 0 or new_left > 0:
            tag = line[:1]
            old_left -= tag in " -"
            new_left -= tag in " +"
            if in_unshipped and tag in "+-":
                udiff.append(line)
            if in_editorconfig and tag in "+-":
                edits.append(line)
            continue
        m = re.match(r"^@@ -\d+(?:,(\d+))? \+\d+(?:,(\d+))? @@", line)
        if m:
            old_left, new_left = int(m[1] or 1), int(m[2] or 1)
            continue
        m = re.match(r"^diff --git a/(\S+) b/(\S+)$", line)
        if m:
            flush()
            path, status = m[2], "M" if m[1] == m[2] else "R"
            in_unshipped = path == UNSHIPPED
            in_editorconfig = path == EDITORCONFIG
            continue
        m = re.match(r"^(?:---|\+\+\+) (?:[ab]/)?(\S+)", line)
        if m and m[1] != "/dev/null" and m[1] != path:
            changes.append(("X", m[1]))
            continue
        if path is None:
            continue
        if line.startswith("new file mode"):
            status = "A" if line.split()[-1] == "100644" else "X"
        elif line.startswith("deleted file mode"):
            status = "D"
        elif line.startswith(("old mode", "new mode", "rename ", "copy ", "similarity index", "GIT binary patch", "Binary files")):
            status = "X"
        elif re.match(r"^index [0-9a-f]+\.\.[0-9a-f]+ (?!100644)", line):
            status = "X"
    flush()
    return changes, udiff, edits


def fix_files_of(path):
    return {l.split("\t")[1].rsplit(":", 2)[0] for l in open(path) if l.count("\t") >= 2} if path else set()


def guard(args):
    if args.patch:
        changes, udiff, edits = patch_changes(open(args.patch, errors="replace").read())
    else:
        out = subprocess.run(["git", "diff", "--name-status", "--no-renames", args.base], capture_output=True, text=True, check=True).stdout
        changes = [tuple(l.split("\t", 1)) for l in out.splitlines() if l]
        udiff = subprocess.run(["git", "diff", "-U0", args.base, "--", UNSHIPPED], capture_output=True, text=True).stdout.splitlines()
        edits = [l for l in subprocess.run(["git", "diff", "-U0", args.base, "--", EDITORCONFIG], capture_output=True, text=True).stdout.splitlines() if l[:1] in "+-" and not l.startswith(("+++", "---"))]
        out = subprocess.run(["git", "diff", "--summary", args.base], capture_output=True, text=True, check=True).stdout
        changes += [("X", l.split()[-1]) for l in out.splitlines() if re.search(r"mode (120000|100755)|mode change", l)]
    bad = check_paths(changes, udiff, args.new_id, fix_files_of(args.files), edits, set(filter(None, (args.ids or "").split(","))))
    bad += [f"{p}: symlink, mode, rename, binary or unlisted header change" for st, p in changes if st == "X"]
    for b in bad:
        print(f"guard: FAIL {b}")
    print(f"guard: {'PASS' if not bad else 'FAIL'} ({len(changes)} files)")
    sys.exit(1 if bad else 0)


def sample(args):
    h = [x for x in load(args.hits)["hits"] if x["id"] == args.id and x["scope"] == "prod"]
    rnd = random.Random(args.seed)
    picked = rnd.sample(h, min(SAMPLE_SIZE, len(h)))
    for x in picked:
        lines = open(os.path.join(args.repo, x["file"]), errors="replace").read().splitlines()
        lo, hi = max(0, x["line"] - 8), min(len(lines), x["line"] + 7)
        x["context"] = "\n".join(f"{n + 1:5d}{'>' if n + 1 == x['line'] else ' '} {lines[n]}" for n in range(lo, hi))
    save(args.out, picked)
    print(f"sample: {len(picked)} of {len(h)} prod hits of {args.id}, seed {args.seed}")


def precision(labels, n_hits):
    need = min(SAMPLE_SIZE, n_hits)
    tp = sum(1 for l in labels if l.get("label") == "true_positive")
    ok = need > 0 and len(labels) == need and (tp >= SAMPLE_MIN_TRUE if need == SAMPLE_SIZE else tp == need)
    return tp, need, ok


def precision_cmd(args):
    labels = load(args.labels, [])
    n = len([x for x in load(args.hits)["hits"] if x["id"] == args.id and x["scope"] == "prod"])
    tp, need, ok = precision(labels, n)
    rb = load(args.route_b, {})
    rb.update(precision=dict(true_positive=tp, labelled=len(labels), needed=need, ok=ok))
    save(args.route_b, rb)
    print(f"precision: {tp}/{need} true positive -> {'PASS' if ok else 'FAIL'} (gate {SAMPLE_MIN_TRUE}/{SAMPLE_SIZE})")
    sys.exit(0 if ok else 1)


def strip_initialize(source):
    m = re.search(r"public\s+override\s+void\s+Initialize\s*\(\s*AnalysisContext\s+(\w+)\s*\)\s*(=>[^;]*;|\{)", source)
    if not m:
        return None
    c = m.group(1)
    stub = f"{{ {c}.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None); {c}.EnableConcurrentExecution(); }}"
    if m.group(2).startswith("=>"):
        return source[:m.start(2)] + stub + source[m.end(2):]
    depth, i = 1, m.end()
    while depth and i < len(source):
        depth += {"{": 1, "}": -1}.get(source[i], 0)
        i += 1
    return source[:m.end() - 1] + stub + source[i:]


def test_argv(test_class, warnings_as_errors=True):
    if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", test_class or ""):
        raise SystemExit(f"invalid test class name: {test_class!r}")
    argv = ["dotnet", "test", "--project", "src/Nethermind/Nethermind.Analyzers.Test/Nethermind.Analyzers.Test.csproj", "-c", "Release"]
    if not warnings_as_errors:
        argv.append("-p:TreatWarningsAsErrors=false")
    return argv + ["--", "--filter", f"FullyQualifiedName~{test_class}"]


def run_tests(args):
    sys.exit(subprocess.run(test_argv(args.test_class)).returncode)


def red(args):
    src = open(args.analyzer).read()
    stub = strip_initialize(src)
    if stub is None:
        raise SystemExit("red: Initialize(AnalysisContext) not found")
    open(args.analyzer, "w").write(stub)
    try:
        p = subprocess.run(test_argv(args.test_class, warnings_as_errors=False), capture_output=True, text=True)
    finally:
        open(args.analyzer, "w").write(src)
    out = p.stdout + p.stderr
    failed = re.search(r"[Ff]ailed:\s*(\d+)", out)
    n = int(failed[1]) if failed else 0
    ok = p.returncode != 0 and n > 0
    if not ok:
        print("\n".join([l for l in out.splitlines() if re.search(r": error |failed:|total:", l)][:10]))
    print(f"red: tests with a no-op analyzer exit {p.returncode}, failed {n} -> {'PASS' if ok else 'FAIL'}")
    sys.exit(0 if ok else 1)


def examples(cluster, gates_data, baseline, k=3):
    ex = [(e["url"], e["author"]) for e in gates_data.get(cluster, {}).get("new_examples", [])]
    ex += [(e[0], e[1]) for e in baseline["clusters"].get(cluster, {}).get("ex", [])]
    out, seen = [], set()
    for url, author in ex:
        if url not in seen:
            out.append((url, author))
            seen.add(url)
    return out[:k]


def body(args):
    c = load(os.path.join(args.data, "candidates.json"))
    p = c["chosen"]
    g = load(os.path.join(args.data, "gates.json"))[p["cluster"]]
    rb = load(os.path.join(args.data, "route_b.json"), {})
    baseline = load(os.path.join(HERE, "baseline.json"))
    head = args.head or "master"
    ids = ", ".join(f"`{i}`" for i in p["ids"])
    L = []
    if p["route"] == "A":
        L += [f"Enables {ids} as a build warning for production code and fixes the existing hits.", ""]
    else:
        L += [f"Adds analyzer `{p['ids'][0]}` ({routes_why(p['cluster'])}) and fixes the existing hits. No code fix provider is shipped.", ""]
    L += ["## Why", "",
          f"Reviewers keep asking for this by hand: {g['n']} review comments on {g['prs']} PRs from {g['commenters']} people "
          f"({g['human']} by humans) match this pattern; {round(g['validity'] * 100)}% were accepted as valid. Examples:", ""]
    L += [f"- {u} ({a})" for u, a in examples(p["cluster"], g, baseline)]
    L += ["", "## Hits", "", f"Full Release build of every CI solution on `{head}` with the rule at warning ({', '.join(f'`{s}`' for s in hits_solutions(args.data))}):", ""]
    L += [f"- Production code: {p['prod']}, all fixed in this PR."]
    L += [f"- Tests and benchmarks: {p['test']}, excluded through `.editorconfig` sections." if p["test"] else "- Tests and benchmarks: 0."]
    if p["route"] == "B":
        pr = rb.get("precision", {})
        L += ["", "## Analyzer checks", "",
              f"- Tests: {rb.get('tests_summary', 'pass')}. With `Initialize` replaced by a no-op the tests fail ({rb.get('red_summary', 'red control')}).",
              f"- Precision: {pr.get('true_positive')}/{pr.get('needed')} random production hits labelled true positive by a separate review pass (gate {SAMPLE_MIN_TRUE}/{SAMPLE_SIZE}).",
              f"- `{UNSHIPPED}` gets one appended row, so a `*.md` code owner has to approve.", "",
              "<details><summary>Precision sample</summary>", ""]
        for l in load(os.path.join(args.data, "labels.json"), []):
            L.append(f"- `{l.get('file')}:{l.get('line')}` {l.get('label')}: {l.get('reason', '')}")
        L += ["", "</details>"]
    L += ["", "## Revert", "", "Revert this PR, or set the rule to `none` in `.editorconfig`:", "",
          "```", "\n".join(f"dotnet_diagnostic.{i}.severity = none" for i in p["ids"]), "```", "",
          f"Opened by the review rule bot (`.github/workflows/rule-bot.yml`). Label `{BOT_LABEL}`; the bot keeps at most one such PR open."]
    open(args.out, "w").write("\n".join(L) + "\n")
    print(f"body: {args.out}")


def hits_solutions(data):
    return load(os.path.join(data, "hits.json"), {}).get("solutions", [])


def routes_why(cluster):
    return load(os.path.join(HERE, "routes.json")).get(cluster, {}).get("why", cluster)


def record_route_b(args):
    path = os.path.join(args.state, "route_b_history.json")
    h = load(path, {})
    for c in args.cluster:
        h[c] = datetime.datetime.now(datetime.timezone.utc).date().isoformat()
    save(path, h)


def main(argv=None):
    ap = argparse.ArgumentParser(prog="rule_bot")
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("collect"); s.add_argument("--state", required=True); s.add_argument("--today"); s.set_defaults(fn=collect)
    s = sub.add_parser("findings"); s.add_argument("--state", required=True); s.add_argument("--data", required=True); s.set_defaults(fn=findings)
    s = sub.add_parser("gates"); s.add_argument("--data", required=True); s.set_defaults(fn=gates)
    s = sub.add_parser("catalog"); s.add_argument("--repo", default="."); s.add_argument("--sdk", required=True); s.add_argument("--data", required=True); s.set_defaults(fn=catalog)
    s = sub.add_parser("plan"); s.add_argument("--repo", default="."); s.add_argument("--data", required=True); s.add_argument("--state", required=True)
    s.add_argument("--offline", action="store_true"); s.add_argument("--github-output"); s.set_defaults(fn=plan)
    s = sub.add_parser("overlay"); s.add_argument("--repo", default="."); s.add_argument("--ids", required=True); s.add_argument("--extra", nargs="*")
    s.add_argument("--exclude-tests", action="store_true"); s.set_defaults(fn=apply_overlay)
    s = sub.add_parser("hits"); s.add_argument("--logs", required=True); s.add_argument("--root", required=True); s.add_argument("--ids", required=True)
    s.add_argument("--out", required=True); s.set_defaults(fn=hits)
    s = sub.add_parser("propose"); s.add_argument("--data", required=True); s.add_argument("--offline", action="store_true"); s.add_argument("--github-output"); s.set_defaults(fn=propose)
    s = sub.add_parser("guard"); s.add_argument("--base", default="HEAD"); s.add_argument("--patch"); s.add_argument("--new-id"); s.add_argument("--files"); s.add_argument("--ids")
    s.set_defaults(fn=guard)
    s = sub.add_parser("sample"); s.add_argument("--hits", required=True); s.add_argument("--id", required=True); s.add_argument("--repo", default=".")
    s.add_argument("--seed", type=int, default=0); s.add_argument("--out", required=True); s.set_defaults(fn=sample)
    s = sub.add_parser("precision"); s.add_argument("--labels", required=True); s.add_argument("--hits", required=True); s.add_argument("--id", required=True)
    s.add_argument("--route-b", required=True); s.set_defaults(fn=precision_cmd)
    s = sub.add_parser("red"); s.add_argument("--analyzer", required=True); s.add_argument("--test-class", required=True); s.set_defaults(fn=red)
    s = sub.add_parser("test"); s.add_argument("--test-class", required=True); s.set_defaults(fn=run_tests)
    s = sub.add_parser("body"); s.add_argument("--data", required=True); s.add_argument("--head"); s.add_argument("--out", required=True); s.set_defaults(fn=body)
    s = sub.add_parser("solutions"); s.add_argument("--workflows", default=".github/workflows"); s.add_argument("--github-output"); s.set_defaults(fn=solutions_cmd)
    s = sub.add_parser("package"); s.add_argument("--data", required=True); s.add_argument("--github-output"); s.set_defaults(fn=package_cmd)
    s = sub.add_parser("tried"); s.add_argument("--state", required=True); s.add_argument("--cluster", nargs="+", required=True); s.set_defaults(fn=record_route_b)
    a = ap.parse_args(argv)
    a.fn(a)


if __name__ == "__main__":
    main()
