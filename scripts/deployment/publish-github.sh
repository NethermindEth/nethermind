#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

echo "Publishing packages to GitHub"

PACKAGE_PATH=$GITHUB_WORKSPACE/$PACKAGE_DIR

BODY=$(printf \
  '{"tag_name": "%s", "target_commitish": "%s", "name": "v%s", "body": "## Release notes\\n\\n", "draft": true, "prerelease": %s}' \
  $GIT_TAG $GIT_COMMIT $GIT_TAG $PRERELEASE)

echo "Drafting release $GIT_TAG"

RELEASE_ID=$(curl https://api.github.com/repos/$GITHUB_REPOSITORY/releases \
  -X POST \
  -H "Accept: application/vnd.github+json" \
  -H "Authorization: Bearer $GITHUB_TOKEN" \
  -d "$BODY" | jq -r '.id')

cd $PACKAGE_PATH

for rid in "linux-x64" "linux-arm64" "windows-x64" "macos-x64" "macos-arm64"
do
  FILE_NAME=$(basename *$rid*)

  echo "Uploading $FILE_NAME"

  curl https://uploads.github.com/repos/$GITHUB_REPOSITORY/releases/$RELEASE_ID/assets?name=$FILE_NAME \
    -X POST \
    -H "Accept: application/vnd.github+json" \
    -H "Authorization: Bearer $GITHUB_TOKEN" \
    -H "Content-Type: application/octet-stream" \
    --data-binary @"$FILE_NAME"
done

echo "Publishing completed"
