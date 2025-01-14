#!/bin/bash

# Function to extract and validate version
get_version() {
    local file_path=$1
    grep -o '<VersionPrefix>.*</VersionPrefix>' "$file_path" | sed 's/<VersionPrefix>\(.*\)<\/VersionPrefix>/\1/'
}

# Function to update version in file
update_version() {
    local file_path=$1
    local new_version=$2
    sed -i.bak "s/<VersionPrefix>.*<\/VersionPrefix>/<VersionPrefix>$new_version<\/VersionPrefix>/" "$file_path"
    rm -f "${file_path}.bak"
}

# Main function to handle version bumping
bump_version() {
    local branch_name=$1
    local props_path=$2
    
    # Validate inputs
    if [ -z "$branch_name" ] || [ -z "$props_path" ]; then
        echo "Usage: $0 <branch-name> <props-path>"
        echo "Example: $0 refs/heads/release/1.40.0 src/Nethermind/Directory.Build.props"
        return 1
    fi
    
    if [[ ! $branch_name =~ ^refs/heads/release/[0-9]+\.[0-9]+\.0$ ]]; then
        echo "error: Invalid branch name format. Expected: refs/heads/release/X.Y.0" >&2
        return 1
    fi
    
    if [ ! -f "$props_path" ]; then
        echo "error: Directory.Build.props not found at $props_path" >&2
        return 1
    fi
    
    # Extract release version from branch name
    local release_version=$(echo "$branch_name" | sed 's/.*release\///')
    
    # Extract version components
    local major=$(echo $release_version | cut -d. -f1)
    local minor=$(echo $release_version | cut -d. -f2)
    
    # Calculate new version
    local new_minor=$((minor + 1))
    local new_version="$major.$new_minor.0"
    
    # Get current version from the file
    local current_version=$(get_version "$props_path")
    
    # Check if update is needed
    if [ "$current_version" = "$new_version" ]; then
        echo "Version $new_version is already set in $props_path"
        echo "needs_update=false"
        echo "current_version=$current_version"
        echo "new_version=$new_version"
        return 0
    fi
    
    # Update the file
    update_version "$props_path" "$new_version"
    
    echo "Updated version from $current_version to $new_version"
    echo "needs_update=true"
    echo "current_version=$current_version"
    echo "new_version=$new_version"
}

# If script is run directly (not sourced), execute with provided arguments
if [ "${BASH_SOURCE[0]}" -ef "$0" ]; then
    bump_version "$@"
    exit $?
fi
