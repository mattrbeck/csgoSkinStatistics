#!/bin/bash

# Script to run the paintwear migration
echo "Running paintwear migration using dotnet script..."

# First try to use dotnet-script if available
if command -v dotnet-script &> /dev/null; then
    echo "Using dotnet-script to run migration..."
    dotnet script MigratePaintwear.cs
elif command -v dotnet &> /dev/null; then
    echo "Creating temporary console app for migration..."
    
    # Create a temporary directory for the migration app
    mkdir -p temp_migration
    cd temp_migration
    
    # Create a new console app
    dotnet new console -n PaintwearMigration --force
    cd PaintwearMigration
    
    # Add required package
    dotnet add package Microsoft.Data.Sqlite
    
    # Copy our migration file over the Program.cs
    cp ../../MigratePaintwear.cs Program.cs
    
    # Run the migration
    dotnet run
    
    # Clean up
    cd ../..
    rm -rf temp_migration
else
    echo "dotnet not found. Please install .NET SDK or run the migration manually."
    echo "You can add the migration code temporarily to your main Program.cs file."
fi