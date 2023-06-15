#!/bin/bash
# SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

for passed in "true" "false"
do
  tmp=()
  mapfile tmp < <(jq '.testCases | map_values(select(.summaryResult.pass == $p)) | map(.name) | .[]' --argjson p "$passed" -r $1)
  IFS=$'\n' results=($(sort -f <<<"${tmp[*]}")); unset IFS

  if ($passed == "true")
  then
    echo -e "\nPassed: ${#results[@]}\n"

    for each in "${results[@]}"; do echo -e "\033[0;32m\u2714\033[0m $each"; done
  else
    echo -e "\nFailed: ${#results[@]}\n"

    for each in "${results[@]}"; do echo -e "\033[0;31m\u2716\033[0m $each"; done

    if [ ${#results[@]} -gt 0 ]; then exit 1; fi
  fi
done
