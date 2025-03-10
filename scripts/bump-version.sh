#!/bin/bash
# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

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

# Create a temporary Directory.Build.props for testing
create_test_file() {
    local version=$1
    local test_file=$(mktemp)
    cat > "$test_file" << EOF
<Project>
  <PropertyGroup>
    <VersionPrefix>$version</VersionPrefix>
    <VersionSuffix>unstable</VersionSuffix>
  </PropertyGroup>
</Project>
EOF
    echo "$test_file"
}

# Function to run a test case
run_test() {
    local test_name=$1
    local branch_name=$2
    local current_version=$3
    local expected_version=$4
    local should_succeed=${5:-true}
    
    echo "🧪 Testing: $test_name"
    echo "Branch: $branch_name"
    echo "Current version: $current_version"
    echo "Expected version: $expected_version"
    echo "Should succeed: $should_succeed"
    
    # Create test file
    local test_file=$(create_test_file "$current_version")
    
    # Run bump_version and capture output
    if output=$(bump_version "$branch_name" "$test_file" 2>&1); then
        if [ "$should_succeed" = true ]; then
            echo "✅ Command succeeded as expected"
        else
            echo "❌ Command succeeded but should have failed"
            rm -f "$test_file"
            return 1
        fi
    else
        if [ "$should_succeed" = false ]; then
            echo "✅ Command failed as expected"
            rm -f "$test_file"
            return 0
        else
            echo "❌ Command failed but should have succeeded"
            echo "Error: $output"
            rm -f "$test_file"
            return 1
        fi
    fi
    
    # Verify the results
    if [ "$should_succeed" = true ]; then
        # Get final version from file
        local final_version=$(get_version "$test_file")
        
        if [ "$final_version" = "$expected_version" ]; then
            echo "✅ Version updated correctly"
        else
            echo "❌ Version mismatch. Expected: $expected_version, Got: $final_version"
            rm -f "$test_file"
            return 1
        fi
    fi
    
    rm -f "$test_file"
    echo "-------------------"
    return 0
}

# Function to run all tests
run_tests() {
    echo "🧪 Running tests..."
    local failed=0

    # Test successful cases
    run_test "Regular minor version bump" \
        "refs/heads/release/1.31.0" "1.31.0" "1.32.0" || failed=1

    run_test "Major version bump" \
        "refs/heads/release/2.0.0" "2.0.0" "2.1.0" || failed=1

    run_test "Skip when version already set" \
        "refs/heads/release/1.31.0" "1.32.0" "1.32.0" || failed=1

    # Test failure cases
    run_test "Invalid branch name (patch version)" \
        "refs/heads/release/1.31.1" "1.31.1" "1.32.0" false || failed=1

    run_test "Invalid branch name (wrong format)" \
        "refs/heads/releases/1.31.0" "1.31.0" "1.32.0" false || failed=1

    run_test "Invalid branch name (feature branch)" \
        "refs/heads/feature/something" "1.31.0" "1.32.0" false || failed=1

    # Additional test cases
    run_test "Skip when higher version already set" \
        "refs/heads/release/1.31.0" "1.40.0" "1.32.0" || failed=1

    run_test "Skip when same minor version already set" \
        "refs/heads/release/1.32.0" "1.32.0" "1.33.0" || failed=1

    run_test "Invalid branch name (no patch zero)" \
        "refs/heads/release/1.31.1" "1.31.0" "1.32.0" false || failed=1

    if [ $failed -eq 0 ]; then
        echo "✅ All tests passed!"
        return 0
    else
        echo "❌ Some tests failed!"
        return 1
    fi
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

# Show usage if no arguments provided
show_usage() {
    echo "Usage:"
    echo "  $0 <branch-name> <props-path>    # Run version bump"
    echo "  $0 --test                        # Run tests"
    echo ""
    echo "Examples:"
    echo "  $0 refs/heads/release/1.40.0 src/Nethermind/Directory.Build.props"
    echo "  $0 --test"
}

# Main script execution
if [ "${BASH_SOURCE[0]}" -ef "$0" ]; then
    if [ "$1" = "--test" ]; then
        run_tests
        exit $?
    elif [ $# -eq 0 ]; then
        show_usage
        exit 1
    else
        bump_version "$@"
        exit $?
    fi
fi
