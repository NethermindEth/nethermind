#!/bin/bash

# Source the script to test
source ./scripts/bump-version.sh

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

# Run test cases
echo "🧪 Running tests..."

# Test successful cases
run_test "Regular minor version bump" \
    "refs/heads/release/1.31.0" "1.31.0" "1.32.0"

run_test "Major version bump" \
    "refs/heads/release/2.0.0" "2.0.0" "2.1.0"

run_test "Skip when version already set" \
    "refs/heads/release/1.31.0" "1.32.0" "1.32.0"

# Test failure cases
run_test "Invalid branch name (patch version)" \
    "refs/heads/release/1.31.1" "1.31.1" "1.32.0" false

run_test "Invalid branch name (wrong format)" \
    "refs/heads/releases/1.31.0" "1.31.0" "1.32.0" false

run_test "Invalid branch name (feature branch)" \
    "refs/heads/feature/something" "1.31.0" "1.32.0" false

# Check if any test failed
if [ "${PIPESTATUS[0]}" -ne 0 ]; then
    echo "❌ Some tests failed!"
    exit 1
else
    echo "✅ All tests passed!"
fi
