#!/bin/bash
sudo apt-get update

echo =======================================================
echo Installing required dependencies
echo =======================================================

wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update; \
  sudo apt-get install -y apt-transport-https && \
  sudo apt-get update && \
  sudo apt-get install -y dotnet-sdk-5.0
sudo apt-get install -y jq libsnappy-dev libc6-dev libc6 moreutils

echo =======================================================
echo Cloning repository / setting up scripts and folders
echo =======================================================

git clone https://github.com/NethermindEth/nethermind.git --recursive
cp nethermind/scripts/pullandbuild.sh ~
cp nethermind/scripts/infra.sh ~
mkdir src
mv nethermind/ src/
chmod +x pullandbuild.sh infra.sh
./pullandbuild.sh

echo =======================================================
echo Running Nethermind
echo =======================================================

echo -e "\033[32m";
read -e -p "Which configuration/s (space separated) you wish to run? " -i "mainnet" config
for cfg in $config; do cp nethermind/configs/$cfg.cfg ~; done
echo -e "\033[00m";

echo =======================================================
echo To run the node type: 
echo 1. screen -S node
echo 2. ./infra.sh config-name.cfg
echo =======================================================