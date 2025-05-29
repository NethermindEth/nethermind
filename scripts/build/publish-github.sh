#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

echo "Publishing packages to GitHub"

release_id=$(curl https://api.github.com/repos/$GITHUB_REPOSITORY/releases \
  -X GET \
  -H "Accept: application/vnd.github+json" \
  -H "Authorization: Bearer $GITHUB_TOKEN" | jq -r '.[] | select(.tag_name == "'$GIT_TAG'") | .id')

should_publish=true

if [ "$release_id" == "" ]; then
  echo "Drafting release $GIT_TAG"

  body=$(printf \
    '{"tag_name": "%s", "target_commitish": "%s", "name": "v%s", "body": "# Release notes\\n\\n", "draft": true, "prerelease": %s}' \
    $GIT_TAG $GITHUB_SHA $GIT_TAG $PRERELEASE)

  release_id=$(curl https://api.github.com/repos/$GITHUB_REPOSITORY/releases \
    -X POST \
    --fail-with-body \
    -H "Accept: application/vnd.github+json" \
    -H "Authorization: Bearer $GITHUB_TOKEN" \
    -d "$body" | jq -r '.id')

  should_publish=false
fi

cd $GITHUB_WORKSPACE/$PACKAGE_DIR

for rid in "linux-x64" "linux-arm64" "windows-x64" "macos-x64" "macos-arm64" "ref-assemblies"; do
  file_name=$(basename *$rid*)

  echo "Uploading $file_name"

  curl https://uploads.github.com/repos/$GITHUB_REPOSITORY/releases/$release_id/assets?name=$file_name \
    -X POST \
    --fail-with-body \
    -H "Accept: application/vnd.github+json" \
    -H "Authorization: Bearer $GITHUB_TOKEN" \
    -H "Content-Type: application/octet-stream" \
    --data-binary @"$file_name"
done

if [ "$should_publish" == "true" ]; then
  echo "Publishing release $GIT_TAG"

  make_latest=$([ $PRERELEASE = 'true' ] && echo "false" || echo "true")

  body=$(printf \
    '{"target_commitish": "%s", "name": "v%s", "draft": false, "make_latest": "%s", "prerelease": %s}' \
    $GITHUB_SHA $GIT_TAG $make_latest $PRERELEASE)

  curl https://api.github.com/repos/$GITHUB_REPOSITORY/releases/$release_id \
    -X PATCH \
    --fail-with-body \
    -H "Accept: application/vnd.github+json" \
    -H "Authorization: Bearer $GITHUB_TOKEN" \
    -d "$body"
fi

echo "Publishing completed"
