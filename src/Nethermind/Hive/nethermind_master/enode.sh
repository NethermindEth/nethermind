#!/bin/bash

# Script to retrieve the enode
# 
# This is copied into the validator container by Hive
# and used to provide a client-specific enode id retriever
#

# Immediately abort the script on any error encountered
set -e

set -e
echo "Trying to get enode."

TARGET_RESPONSE=$(curl --data '{"method":"enode_info","params":[],"id":1,"jsonrpc":"2.0"}' -H "Content-Type: application/json" -X POST "$HIVE_CLIENT_IP:8545" )

echo "Got admin enode info response: $TARGET_RESPONSE"
TARGET_ENODE=$(echo ${TARGET_RESPONSE}| jq -r '.result')

echo "Target enode identified as $TARGET_ENODE"

