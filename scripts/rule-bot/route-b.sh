#!/usr/bin/env bash
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
data=$1
id=$(jq -r .route_b_id "$data/plan.json")
out="$data/route_b.json"
jq -n --arg i "$id" --arg c "$(jq -r '.route_b_queue[0].cluster' "$data/plan.json")" '{id:$i, cluster:$c, attempts:[]}' > "$out"

attempt() {
  local cluster=$1 why=$2
  jq --arg c "$cluster" '.[$c].examples // []' "$data/gates.json" > "$data/route-b-examples.json"
  rm -f "$data/analyzer-result.json"
  "$here/llm.sh" "$here/prompts/analyzer.txt" "Read,Glob,Grep,Edit(src/Nethermind/Nethermind.Analyzers/**),Write(src/Nethermind/Nethermind.Analyzers/**),Edit(src/Nethermind/Nethermind.Analyzers.Test/**),Write(src/Nethermind/Nethermind.Analyzers.Test/**),Write($data/analyzer-result.json),Bash(dotnet build:*),Bash(dotnet test:*)" \
    "ID=$id" "CLUSTER=$cluster" "WHY=$why" "EXAMPLES=$data/route-b-examples.json" "RESULT=$data/analyzer-result.json" > "$data/analyzer-llm-$cluster.log" 2>&1
  echo "route-b: $cluster generation exit $?"
  local test_class analyzer skip
  test_class=$(jq -r '.test_class // empty' "$data/analyzer-result.json" 2>/dev/null)
  analyzer=$(jq -r '.analyzer // empty' "$data/analyzer-result.json" 2>/dev/null)
  if ! [[ $test_class =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || [[ $analyzer != src/Nethermind/Nethermind.Analyzers/*.cs ]] || [ ! -f "$analyzer" ]; then
    skip=$(jq -r '.skip // "no analyzer produced"' "$data/analyzer-result.json" 2>/dev/null || echo "no analyzer produced")
    echo "route-b: $cluster skipped: $skip"
    jq --arg c "$cluster" --arg s "$skip" '.attempts += [{cluster:$c, result:("skipped by the generator: " + $s)}]' "$out" > "$out.tmp" && mv "$out.tmp" "$out"
    return 1
  fi
  git add -N .
  local guard_ok tests_ok red_ok summary red_summary
  "$here/rule-bot" guard --new-id "$id" && guard_ok=true || guard_ok=false
  env -u CLAUDE_CODE_OAUTH_TOKEN "$here/rule-bot" test --test-class "$test_class" > "$data/analyzer-tests.log" 2>&1 && tests_ok=true || tests_ok=false
  summary=$(grep -E 'total: [0-9]+|Passed!|Failed!' "$data/analyzer-tests.log" | tail -1)
  env -u CLAUDE_CODE_OAUTH_TOKEN "$here/rule-bot" red --analyzer "$analyzer" --test-class "$test_class" > "$data/analyzer-red.log" 2>&1 && red_ok=true || red_ok=false
  red_summary=$(grep '^red:' "$data/analyzer-red.log" | tail -1)
  echo "route-b: $id ($cluster) guard=$guard_ok tests=$tests_ok red=$red_ok :: $summary"
  jq --arg c "$cluster" --arg w "$why" --arg a "$analyzer" --argjson g $guard_ok --argjson t $tests_ok --argjson r $red_ok \
    --arg ts "$summary" --arg rs "$red_summary" --arg sm "$(jq -r '.summary // ""' "$data/analyzer-result.json")" \
    '.attempts += [{cluster:$c, result:"guard \($g), tests \($t), red control \($r)"}]
     | . + {cluster:$c, why:$w, analyzer:$a, summary:$sm, guard_ok:$g, tests_ok:$t, red_ok:$r, tests_summary:$ts, red_summary:$rs}' \
    "$out" > "$out.tmp" && mv "$out.tmp" "$out"
  $guard_ok && $tests_ok && $red_ok
}

n=$(jq '.route_b_queue | length' "$data/plan.json")
for k in $(seq 0 $((n - 1))); do
  cluster=$(jq -r ".route_b_queue[$k].cluster" "$data/plan.json")
  why=$(jq -r ".route_b_queue[$k].why" "$data/plan.json")
  if attempt "$cluster" "$why"; then
    git diff HEAD > "$data/analyzer.patch"
    exit 0
  fi
  git diff HEAD > "$data/analyzer-$cluster.patch"
  git reset -q --hard HEAD
  git clean -qfd -e /rule-bot
done
jq '.skip = ([.attempts[] | "\(.cluster): \(.result)"] | join("; "))' "$out" > "$out.tmp" && mv "$out.tmp" "$out"
echo "route-b: no cluster produced an analyzer that passed the gates"
