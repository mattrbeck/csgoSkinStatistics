#!/bin/bash

# Bash test runner script for Linux/macOS

set -e

echo "CS:GO Skin Statistics - Test Runner"
echo "===================================="

success=true

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Function to run command and check result
run_test() {
    local command="$1"
    local description="$2"

    echo -e "\n${YELLOW}$description...${NC}"

    if eval "$command"; then
        echo -e "${GREEN}PASSED: $description${NC}"
        return 0
    else
        echo -e "${RED}FAILED: $description${NC}"
        success=false
        return 1
    fi
}

# Check if dependencies are installed
echo -e "\n${YELLOW}Checking dependencies...${NC}"

if ! command -v dotnet &> /dev/null; then
    echo -e "${RED}ERROR: .NET CLI not found. Please install .NET 9.0 SDK${NC}"
    exit 1
fi

if ! command -v npm &> /dev/null; then
    echo -e "${RED}ERROR: npm not found. Please install Node.js${NC}"
    exit 1
fi

# Install npm dependencies if needed
if [ ! -d "node_modules" ]; then
    echo -e "${YELLOW}Installing npm dependencies...${NC}"
    if ! npm install; then
        echo -e "${RED}ERROR: Failed to install npm dependencies${NC}"
        exit 1
    fi
fi

# Run .NET backend tests
echo -e "\n${CYAN}$(printf '=%.0s' {1..50})${NC}"
echo -e "${CYAN}Running Backend Tests (.NET)${NC}"
echo -e "${CYAN}$(printf '=%.0s' {1..50})${NC}"

if [ -d "csgoSkinStatistics.Tests" ]; then
    run_test "dotnet test csgoSkinStatistics.Tests --verbosity normal --logger console" "Backend unit tests"
else
    echo -e "${YELLOW}WARNING: Backend test project not found${NC}"
fi

# Run JavaScript frontend tests
echo -e "\n${CYAN}$(printf '=%.0s' {1..50})${NC}"
echo -e "${CYAN}Running Frontend Tests (JavaScript)${NC}"
echo -e "${CYAN}$(printf '=%.0s' {1..50})${NC}"

run_test "npm test -- --passWithNoTests" "Frontend unit tests"

# Run linting
echo -e "\n${CYAN}$(printf '=%.0s' {1..50})${NC}"
echo -e "${CYAN}Running Code Quality Checks${NC}"
echo -e "${CYAN}$(printf '=%.0s' {1..50})${NC}"

if ls wwwroot/*.js 1> /dev/null 2>&1; then
    run_test "npm run lint:js" "JavaScript linting"
fi

if ls wwwroot/*.css 1> /dev/null 2>&1; then
    run_test "npm run lint:css" "CSS linting"
fi

# Build the application
echo -e "\n${CYAN}$(printf '=%.0s' {1..50})${NC}"
echo -e "${CYAN}Building Application${NC}"
echo -e "${CYAN}$(printf '=%.0s' {1..50})${NC}"

run_test "dotnet build --configuration Release" "Application build"

# Summary
echo -e "\n${CYAN}$(printf '=%.0s' {1..50})${NC}"
echo -e "${CYAN}Test Summary${NC}"
echo -e "${CYAN}$(printf '=%.0s' {1..50})${NC}"

if [ "$success" = true ]; then
    echo -e "${GREEN}All tests passed! ✅${NC}"
    exit 0
else
    echo -e "${RED}Some tests failed! ❌${NC}"
    exit 1
fi