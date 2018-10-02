#!/bin/bash
rm -rf out _ && ./scripts/publish-linux-x64.sh && ./scripts/publish-osx-x64.sh && ./scripts/publish-win-x64.sh && rm -rf _