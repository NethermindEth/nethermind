#!/usr/bin/env bash
# Preview which Docker images would be selected for a retrospective benchmark run.
# Usage: ./scripts/preview-retrospective-images.sh [last] [step]
# Example: ./scripts/preview-retrospective-images.sh 500 10
set -euo pipefail

LAST="${1:-100}"
STEP="${2:-10}"

echo "=== Retrospective Image Preview ==="
echo "last=${LAST} (commits from master HEAD), step=${STEP}"
echo ""

# Get commit sha7s from master
echo "Getting last ${LAST} master commits..."
mapfile -t commit_shas < <(git log --format='%h' -n "${LAST}" origin/master)
echo "Got ${#commit_shas[@]} commits."

# Fetch master-* tags from Docker Hub
echo "Fetching master-* tags from Docker Hub..."
declare -A available_tags
page=1
page_size=100
fetched=0

while true; do
  url="https://hub.docker.com/v2/repositories/nethermindeth/nethermind/tags?page_size=${page_size}&page=${page}&name=master-&ordering=-last_updated"
  response=$(curl -sf "${url}" || echo '{"results":[]}')

  mapfile -t page_tags < <(echo "${response}" | jq -r '.results[].name // empty')

  if [[ "${#page_tags[@]}" -eq 0 ]]; then
    break
  fi

  for t in "${page_tags[@]}"; do
    if [[ "${t}" =~ ^master-[0-9a-f]{7}$ ]]; then
      available_tags["${t}"]=1
      fetched=$(( fetched + 1 ))
    fi
  done

  if [[ "${fetched}" -ge "${LAST}" ]]; then
    break
  fi

  has_next=$(echo "${response}" | jq -r '.next // empty')
  if [[ -z "${has_next}" ]]; then
    break
  fi

  page=$(( page + 1 ))
done

echo "Fetched ${fetched} master-* tags from Docker Hub."
echo ""

# Intersect commits with Docker images
images_in_window=()
for sha7 in "${commit_shas[@]}"; do
  tag="master-${sha7}"
  if [[ -n "${available_tags[${tag}]+x}" ]]; then
    images_in_window+=("${tag}")
  fi
done

echo "Found ${#images_in_window[@]} Docker images within the last ${LAST} commits."

# Apply step
selected_tags=()
for (( i=0; i<${#images_in_window[@]}; i+=STEP )); do
  selected_tags+=("${images_in_window[$i]}")
done

echo "Selected ${#selected_tags[@]} images after applying step=${STEP}."
echo ""
echo "=== Selected Images (newest first) ==="
printf "%-4s %-20s %-12s %s\n" "#" "Tag" "Date" "Commit message"
printf "%-4s %-20s %-12s %s\n" "---" "---" "---" "---"

idx=1
for t in "${selected_tags[@]}"; do
  sha="${t#master-}"
  date=$(git log --format='%cs' -n 1 "${sha}" 2>/dev/null || echo "unknown")
  msg=$(git log --format='%s' -n 1 "${sha}" 2>/dev/null | head -c 60 || echo "")
  printf "%-4s %-20s %-12s %s\n" "${idx}" "${t}" "${date}" "${msg}"
  idx=$(( idx + 1 ))
done
