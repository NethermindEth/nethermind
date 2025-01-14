#!/bin/bash

# Create a temporary Directory.Build.props for testing
create_test_file() {
    local version=$1
    local test_file="test_Directory.Build.props"
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

# Function to test version bump with branch name and file modification
test_version_bump() {
    local branch_name=$1
    local current_version=$2
    local expected_version=$3
    local should_update=${4:-true}
    
    echo "Testing branch: $branch_name"
    echo "Current version: $current_version"
    echo "Expected new version: $expected_version"
    echo "Should update: $should_update"
    
    # Test branch name extraction
    RELEASE_VERSION=$(echo "$branch_name" | sed 's/.*release\///')
    echo "Extracted version: $RELEASE_VERSION"
    
    # Extract version components
    MAJOR=$(echo $RELEASE_VERSION | cut -d. -f1)
    MINOR=$(echo $RELEASE_VERSION | cut -d. -f2)
    
    # Calculate new version
    NEW_MINOR=$((MINOR + 1))
    NEW_VERSION="$MAJOR.$NEW_MINOR.0"
    
    echo "Calculated new version: $NEW_VERSION"
    
    # Test file modification
    local test_file=$(create_test_file "$current_version")
    
    # Get current version from the file
    CURRENT_VERSION=$(grep -o '<VersionPrefix>.*</VersionPrefix>' "$test_file" | sed 's/<VersionPrefix>\(.*\)<\/VersionPrefix>/\1/')
    
    # Only proceed if versions are different
    if [ "$CURRENT_VERSION" = "$NEW_VERSION" ]; then
        echo "Version $NEW_VERSION is already set in Directory.Build.props, skipping..."
        NEEDS_UPDATE=false
    else
        echo "Updating version from $CURRENT_VERSION to $NEW_VERSION"
        NEEDS_UPDATE=true
        sed -i.bak "s/<VersionPrefix>.*<\/VersionPrefix>/<VersionPrefix>$NEW_VERSION<\/VersionPrefix>/" "$test_file"
    fi
    
    # Verify file modification
    local modified_version=$(grep -o '<VersionPrefix>.*</VersionPrefix>' "$test_file" | sed 's/<VersionPrefix>\(.*\)<\/VersionPrefix>/\1/')
    echo "Final file version: $modified_version"
    
    # Check version calculation
    if [ "$NEW_VERSION" = "$expected_version" ]; then
        echo "✅ Version calculation test passed!"
    else
        echo "❌ Version calculation test failed! Expected $expected_version but got $NEW_VERSION"
    fi
    
    # Check update decision
    if [ "$NEEDS_UPDATE" = "$should_update" ]; then
        echo "✅ Update decision test passed!"
    else
        echo "❌ Update decision test failed! Expected update=$should_update but got update=$NEEDS_UPDATE"
    fi
    
    # Check file modification
    if [ "$NEEDS_UPDATE" = true ]; then
        if [ "$modified_version" = "$NEW_VERSION" ]; then
            echo "✅ File modification test passed!"
        else
            echo "❌ File modification test failed! Expected $NEW_VERSION but got $modified_version"
        fi
    else
        if [ "$modified_version" = "$CURRENT_VERSION" ]; then
            echo "✅ File not modified as expected!"
        else
            echo "❌ File modification test failed! File was modified when it shouldn't have been"
        fi
    fi
    
    # Cleanup
    rm -f "$test_file" "$test_file.bak"
    echo "-------------------"
}

# Test cases
echo "🧪 Running tests..."
# Regular version bump tests
test_version_bump "refs/heads/release/1.31.0" "1.31.0" "1.32.0" true
test_version_bump "refs/heads/release/2.0.0" "2.0.0" "2.1.0" true
test_version_bump "refs/heads/release/1.30.3" "1.30.3" "1.31.0" true

# Test when version is already at target
test_version_bump "refs/heads/release/1.31.0" "1.32.0" "1.32.0" false
test_version_bump "refs/heads/release/2.0.0" "2.1.0" "2.1.0" false

# Test invalid branch names
test_invalid_branch() {
    local branch_name=$1
    echo "Testing invalid branch: $branch_name"
    
    if [[ $branch_name =~ ^refs/heads/release/.*\.0$ ]]; then
        echo "❌ Test failed! Branch $branch_name should not match release pattern"
    else
        echo "✅ Test passed! Branch $branch_name correctly does not match release pattern"
    fi
    echo "-------------------"
}

echo "🧪 Testing invalid branch names..."
test_invalid_branch "refs/heads/main"
test_invalid_branch "refs/heads/feature/something"
test_invalid_branch "refs/heads/releases/1.31.0"  # note the 's' in releases
test_invalid_branch "refs/heads/release/1.31.1"   # not a .0 version
