#!/bin/bash
# SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

knownFailingTests=$(cat nethermind/scripts/known-failing-hive-tests.txt)
# in some test suits this test is a client setup and in some it's a master test. So just ignore it
launchTestName='client launch (nethermind)'

shouldNotPass=()
shouldPass=()

for passed in "true" "false"
do
  tmp=()
  mapfile tmp < <(jq '.testCases | map_values(select(.summaryResult.pass == $p)) | map(.name) | .[]' --argjson p "$passed" -r $1)
  IFS=$'\n' results=($(sort -f <<<"${tmp[*]}")); unset IFS

  if ($passed == "true")
  then
    echo -e "\nPassed: ${#results[@]}\n"

    for each in "${results[@]}";
    do
      echo -e "\033[0;32m\u2714\033[0m $each"
      if [ grep -qx "$each" <<< "$knownFailingTests" ] -a [ "$each" -eq "$launchTestName" ]; then
        shouldNotPass+=("$each")
      fi
    done
  else
    echo -e "\nFailed: ${#results[@]}\n"

    for each in "${results[@]}";
    do
      echo -e "\033[0;31m\u2716\033[0m $each"
      if [ ! grep -qx "$each" <<< "$knownFailingTests"] -a [ "$each" -eq "$launchTestName" ]; then
        shouldPass+=("$each")
      fi
    done
  fi
done

if [ ${#shouldPass[@]} -gt 0 ]; then
  echo -e "\nTests expected to pass but failed ${#shouldPass[@]}\n"
  for each in "${shouldPass[@]}"; do echo -e "$each"; done
fi

if [ ${#shouldNotPass[@]} -gt 0 ]; then
  echo -e "\nTests expected to fail but passed ${#shouldNotPass[@]}\n"
  for each in "${shouldNotPass[@]}"; do echo -e "$each"; done
fi

if [[ ${#shouldNotPass[@]} -gt 0 || ${#shouldPass[@]} -gt 0 ]]; then
  exit 1
fi
