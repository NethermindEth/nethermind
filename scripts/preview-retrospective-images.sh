#!/usr/bin/env bash
# Preview which Docker images would be selected for a retrospective benchmark run.
# Fetches directly from Docker Hub — no git needed.
# Usage: ./scripts/preview-retrospective-images.sh [last] [step]
# Example: ./scripts/preview-retrospective-images.sh 500 10
set -euo pipefail

LAST="${1:-100}"
STEP="${2:-10}"

echo "=== Retrospective Image Preview ==="
echo "last=${LAST} (Docker images from Docker Hub), step=${STEP}"
echo ""

# Fetch master-* tags from Docker Hub, newest first
echo "Fetching last ${LAST} master-* tags from Docker Hub..."

all_tags=()
all_dates=()
page=1
page_size=100
remaining="${LAST}"

while [[ "${remaining}" -gt 0 ]]; do
  fetch_size="${page_size}"
  if [[ "${remaining}" -lt "${page_size}" ]]; then
    fetch_size="${remaining}"
  fi

  url="https://hub.docker.com/v2/repositories/nethermindeth/nethermind/tags?page_size=${fetch_size}&page=${page}&name=master-&ordering=-last_updated"
  response=$(curl -sf "${url}" || echo '{"results":[]}')

  mapfile -t page_tags < <(echo "${response}" | jq -r '.results[] | select(.name | test("^master-[0-9a-f]{7}$")) | .name')
  mapfile -t page_dates < <(echo "${response}" | jq -r '.results[] | select(.name | test("^master-[0-9a-f]{7}$")) | .last_updated[:10]')

  if [[ "${#page_tags[@]}" -eq 0 ]]; then
    echo "No more tags found at page ${page}."
    break
  fi

  for i in "${!page_tags[@]}"; do
    all_tags+=("${page_tags[$i]}")
    all_dates+=("${page_dates[$i]}")
  done

  remaining=$(( remaining - page_size ))
  page=$(( page + 1 ))
done

echo "Found ${#all_tags[@]} master-* images."

# Apply step
selected_tags=()
selected_dates=()
for (( i=0; i<${#all_tags[@]}; i+=STEP )); do
  selected_tags+=("${all_tags[$i]}")
  selected_dates+=("${all_dates[$i]}")
done

echo "Selected ${#selected_tags[@]} images after applying step=${STEP}."
echo ""
echo "=== Selected Images (newest first) ==="
printf "%-4s %-20s %-12s\n" "#" "Tag" "Date"
printf "%-4s %-20s %-12s\n" "---" "---" "---"

for i in "${!selected_tags[@]}"; do
  idx=$(( i + 1 ))
  printf "%-4s %-20s %-12s\n" "${idx}" "${selected_tags[$i]}" "${selected_dates[$i]}"
done
