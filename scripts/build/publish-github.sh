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

if [[ -z "$release_id" ]]; then
  echo "Drafting release $GIT_TAG"

  relnotes=$(cat <<'EOF'
# Release notes

### [CONTENT PLACEHOLDER]

#### Build signatures

The packages are signed with the following OpenPGP key: `AD12 7976 5093 C675 9CD8 A400 24A7 7461 6F1E 617E`
EOF
)

  body=$(jq -n \
    --arg tag "$GIT_TAG" \
    --arg hash "$GITHUB_SHA" \
    --arg name "v$GIT_TAG" \
    --arg body "$relnotes" \
    --argjson prerelease "$PRERELEASE" \
    '{
      tag_name: $tag,
      target_commitish: $hash,
      name: $name,
      body: $body,
      draft: true,
      prerelease: $prerelease
    }')

  release_id=$(curl https://api.github.com/repos/$GITHUB_REPOSITORY/releases \
    -X POST \
    --fail-with-body \
    -H "Accept: application/vnd.github+json" \
    -H "Authorization: Bearer $GITHUB_TOKEN" \
    -d "$body" | jq -r '.id')

  should_publish=false
fi

cd $GITHUB_WORKSPACE/$PACKAGE_DIR

for file_name in *.zip *.zip.asc; do
  echo "Uploading $file_name"

  curl https://uploads.github.com/repos/$GITHUB_REPOSITORY/releases/$release_id/assets?name=$file_name \
    -X POST \
    --fail-with-body \
    -H "Accept: application/vnd.github+json" \
    -H "Authorization: Bearer $GITHUB_TOKEN" \
    -H "Content-Type: application/octet-stream" \
    --data-binary @"$file_name"
done

if [[ "$should_publish" == "true" ]]; then
  echo "Publishing release $GIT_TAG"

  make_latest=$([[ "$PRERELEASE" == "true" ]] && echo "false" || echo "true")

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
