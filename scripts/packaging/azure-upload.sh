#!/bin/bash

DATE=`date +%Y%m%d`
GIT_RC="$(tail -c 8 ./nethermind-packages/git-tag.txt | tr "." "-")"
export container_name=$GIT_RC-$DATE-public
export linux_folder=~/repo_pub/nethermind-packages/nethermind-lin-x64/nethermind-*
export ppa_folder=~/repo_pub/nethermind-packages/nethermind-ppa/nethermind-*
export windows_folder=~/repo_pub/nethermind-packages/nethermind-win-x64/nethermind-*
export darwin_folder=~/repo_pub/nethermind-packages/nethermind-osx-x64/nethermind-*

echo "Creating the container..."
az storage container create --name $container_name

echo "Uploading linux package..."
az storage blob upload --container-name $container_name --file $linux_folder --name $(basename $linux_folder)

echo "Uploading the ppa package"
az storage blob upload --container-name $container_name --file $ppa_folder --name $(basename $ppa_folder)

echo "Uploading windows package..."
az storage blob upload --container-name $container_name --file $windows_folder --name $(basename $windows_folder)

echo "Uploading darwin package..."
az storage blob upload --container-name $container_name --file $darwin_folder --name $(basename $darwin_folder)

echo "Listing the blobs..."
az storage blob list --container-name $container_name --output table

echo "Done"
