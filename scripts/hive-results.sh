#!/bin/bash
# SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -euo pipefail

known_fails=$(cat ./nethermind/scripts/known-failing-hive-tests.txt)
launch_test='client launch (nethermind)' # The launch test to ignore
should_pass=()
should_not_pass=()

for passed in "true" "false"; do
  tmp=()
  mapfile tmp < <(jq '.testCases
    | map_values(select(.summaryResult.pass == $p))
    | map(.name)
    | .[]' \
    --argjson p "$passed" -r $1)
  IFS=$'\n' results=($(sort -f <<<"${tmp[*]}")); unset IFS

  if [[ "$passed" == "true" ]]; then
    echo -e "\nPassed ${#results[@]}:\n"

    for each in "${results[@]}"; do
      if grep -Fqx "$each" <<< "$known_fails" && [[ "$each" != "$launch_test" ]]; then
        should_not_pass+=("$each")
        echo -e "\e[90m\u2714 $each\e[0m"
      else
        echo -e "\e[32m\u2714\e[0m $each"
      fi
    done
  else
    echo -e "\nFailed ${#results[@]}:\n"

    for each in "${results[@]}"; do
      if ! grep -Fqx "$each" <<< "$known_fails" && [[ "$each" != "$launch_test" ]]; then
        should_pass+=("$each")
        echo -e "\e[31m\u2716\e[0m $each"
      else
        echo -e "\e[90m\u2716 $each\e[0m"
      fi
    done
  fi
done

echo -e "\n  Unexpected passes: ${#should_not_pass[@]}"
echo "  Unexpected fails: ${#should_pass[@]}"

(( ${#should_pass[@]} + ${#should_not_pass[@]} > 0 )) && exit 1 || exit 0
