#!/bin/bash
set -e
export WORKDIR=$HOME
RPC_PORT=8545
mkdir -p $WORKDIR/.nnm/logs

cli_help() {
  cli_name=${0##*/}
  echo "
Nethermind Node Management CLI for docker-compose
   _  __ _  __ __  ___  _____ __    ____
  / |/ // |/ //  |/  / / ___// /   /  _/
 /    //    // /|_/ / / /__ / /__ _/ /  
/_/|_//_/|_//_/  /_/  \___//____//___/  

Version: 0.0.1
Usage: $cli_name [command]

Commands:
  pullandbuild|pb    Pulls latest changes from Nethermind repository and builds the app
  restart|r          Restarts docker-compose
  shutdown|sd        Shutdowns the Nethermind docker-compose
  status|st          Displays the current status of Nethermind docker-compose
  tail|t             Tails the latest Nethermind live logs
  up|u               Starts the Nethermind docker-compose stack
  version            Checks the current Node version by sending RPC request to the Node
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
pullandbuild() {
  cli_log "Pulling and restarting docker-compose stack..."
  cd $HOME
  docker-compose pull
  docker-compose down
  docker-compose up -d
}
# End of pullandbuild

# Start of restart
restart() {
  cli_log "Restarting docker-compose stack..."
  docker-compose restart
}
# End of restart

# Start of shutdown
shutdown() {
  cli_log "Shutting down docker-compose stack..."
  docker-compose down
}
# End of shutdown

# Start of status
status() {
  cli_log "Checking status of docker-compose stack..."
  docker-compose ps
}
# End of status

# Start of up
up() {
  cli_log "Starting docker-compose stack..."
  docker-compose up -d
}
# End of up

# Start of tail
tail() {
  cli_log "Getting live logs from docker-compose stack..."
  docker-compose logs -f
}
# End of tail

# Start of version
version() {
  cli_log "Checking the current version of Nethermind..."
  curl --silent --data '{"method":"web3_clientVersion","params":[],"id":1,"jsonrpc":"2.0"}' -H "Content-Type: application/json" -X POST localhost:$RPC_PORT | tr { '\n' | tr , '\n' | tr } '\n' | grep "result" | awk  -F'"' '{print $4}'
}
# End of version

case "$1" in
  pullndbuild|pb)
    pullandbuild | save_log $1
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
    tail
    ;;
  version|v)
    version | save_log $1
    ;;
  *)
    cli_help
    ;;
esac