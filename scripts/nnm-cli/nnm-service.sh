#!/bin/bash
set -e
export WORKDIR=$HOME
BRANCH=$2
RPC_PORT=8545
BUILD_DIR="build"
BUILD_NEW_DIR="build_new"
NETHERMIND_DIR="nethermind"

mkdir -p $WORKDIR/.nnm/logs

cli_help() {
  cli_name=${0##*/}
  echo "
Nethermind Node Management CLI for Systemd Service
   _  __ _  __ __  ___  _____ __    ____
  / |/ // |/ //  |/  / / ___// /   /  _/
 /    //    // /|_/ / / /__ / /__ _/ /
/_/|_//_/|_//_/  /_/  \___//____//___/

Version: 0.0.1
Usage: $cli_name [command]

Commands:
  pullandbuild|pb    Pulls latest changes from Nethermind repository and builds the app
  restart|r          Restarts the Nethermind service
  shutdown|sd        Shutdowns the Nethermind service
  status|st          Displays the current status of Nethermind service
  tail|t             Tails the latest Nethermind live logs
  up|u               Starts the Nethermind service
  version|v          Checks the current Node version by sending RPC request to the Node
  *                  Help
"
  exit 1
}

cli_log() {
  script_name=${0##*/}
  function_name=${1}
  timestamp=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
  echo "==> $script_name $timestamp $function_name"
}

save_log() {
  timestamp=$(date -u +"%Y-%m-%d")
  tee -ia $WORKDIR/.nnm/logs/$timestamp.log
}

# start of pullandbuild
gitPullOrigin() {
	cli_log "Pulling master from Github..."
  git fetch origin
  git reset --hard origin/master
}

gitCheckout() {
  cli_log "Checking out branch/tag: ${BRANCH}"
  git checkout $BRANCH
}

git_ref_type() {
    [ -n "$1" ] || die "Missing ref name"

    if git show-ref -q --verify "refs/heads/$1" 2>/dev/null; then
        echo "branch"
    elif git show-ref -q --verify "refs/tags/$1" 2>/dev/null; then
        echo "tag"
    elif git show-ref -q --verify "refs/remote/$1" 2>/dev/null; then
        echo "remote"
    elif git rev-parse --verify "$1^{commit}" >/dev/null 2>&1; then
        echo "hash"
    else
        echo "unknown"
    fi
    return 0
}

gitPull() {
  cli_log "Pulling latest changes from Github..."
  ref_type=$(git_ref_type $BRANCH)
  if [ ! $ref_type = "tag" ]; then git pull; fi
}

gitSubmoduleUpdate() {
	cli_log "Updating submodules..."
	git submodule update --init
}

buildNethermind() {
	cli_log "Building Nethermind..."
	cd $HOME/nethermind/src/Nethermind/Nethermind.Runner
	dotnet build -c Release -o $HOME/$BUILD_NEW_DIR
}

replaceOldBuild() {
	cli_log "Replacing old build dir..."
	rm -rf $HOME/$BUILD_DIR
	mv $HOME/$BUILD_NEW_DIR $BUILD_DIR
}

pullandbuild() {
  cli_log "Pulling and rebuilding nethermind.service..."
  cd $HOME/$NETHERMIND_DIR
  gitPullOrigin
  gitCheckout $BRANCH
  gitPull
  gitSubmoduleUpdate
  buildNethermind
  cd $HOME
  shutdown
  replaceOldBuild
  up
}
# End of pullandbuild

# Start of restart
restart() {
  cli_log "Restarting nethermind.service..."
  sudo systemctl restart nethermind
}
# End of restart

# Start of shutdown
shutdown() {
  cli_log "Shutting down nethermind.service..."
  sudo systemctl stop nethermind
}
# End of shutdown

# Start of status
status() {
  cli_log "Shutting down nethermind.service..."
  sudo systemctl status nethermind
}
# End of status

# Start of up
up() {
  cli_log "Starting nethermind.service..."
  sudo systemctl start nethermind
}
# End of up

# Start of tail
tailLogs() {
  cli_log "Getting live logs from nethermind.service..."
  sudo journalctl -u nethermind -f
}
# End of tail

# Start of version
version() {
  cli_log "Checking the current version of Nethermind..."
  curl --silent --data '{"method":"web3_clientVersion","params":[],"id":1,"jsonrpc":"2.0"}' -H "Content-Type: application/json" -X POST localhost:$RPC_PORT | tr { '\n' | tr , '\n' | tr } '\n' | grep "result" | awk  -F'"' '{print $4}'
}
# End of version

case "$1" in
  pullandbuild|pb)
    pullandbuild "$BRANCH" | save_log $1
    ;;
  up|u)
    up | save_log $1
    ;;
  restart|r)
    restart | save_log $1
    ;;
  shutdown|sd)
    shutdown | save_log $1
    ;;
  status|st)
    status | save_log $1
    ;;
  tail|t)
    tailLogs
    ;;
  version|v)
    version | save_log $1
    ;;
  *)
    cli_help
    ;;
esac