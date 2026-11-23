#!/usr/bin/env bash
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
cp -r "$here" "$RUNNER_TEMP/rule-bot"
dotnet publish "$here/rule-bot.cs" -c Release -o "$RUNNER_TEMP/rule-bot" --artifacts-path "$RUNNER_TEMP/rule-bot-build"
