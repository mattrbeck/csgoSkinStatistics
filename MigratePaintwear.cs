using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace csgoSkinStatistics
{
    public class PaintwearMigration
    {
        private static readonly string ConnectionString = $"Data Source={GetDatabasePath()};foreign keys=true;";
        
        private static string GetDatabasePath()
        {
            // Try current directory first
            var currentDir = Environment.CurrentDirectory;
            var dbPath = Path.Combine(currentDir, "searches.db");
            
            if (File.Exists(dbPath))
            {
                Console.WriteLine($"Found database at: {dbPath}");
                return dbPath;
            }
            
            // Try the directory where the executable is located
            var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (exeDir != null)
            {
                dbPath = Path.Combine(exeDir, "searches.db");
                if (File.Exists(dbPath))
                {
                    Console.WriteLine($"Found database at: {dbPath}");
                    return dbPath;
                }
            }
            
            // Fallback to absolute path
            var absolutePath = "/Users/matt/code/csgoSkinStatistics/searches.db";
            if (File.Exists(absolutePath))
            {
                Console.WriteLine($"Found database at: {absolutePath}");
                return absolutePath;
            }
            
            throw new FileNotFoundException("Could not locate searches.db database file");
        }

        public static async Task Main()
        {
            Console.WriteLine("Starting paintwear migration from REAL to INTEGER...");
            
            using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            // First, get count of records that need migration
            var countQuery = "SELECT COUNT(*) FROM searches WHERE paintwear_uint IS NULL";
            using var countCommand = new SqliteCommand(countQuery, connection);
            var recordsToMigrate = Convert.ToInt32(await countCommand.ExecuteScalarAsync());
            
            Console.WriteLine($"Found {recordsToMigrate} records to migrate");

            if (recordsToMigrate == 0)
            {
                Console.WriteLine("No records need migration. Exiting.");
                return;
            }

            // Get all records that need migration
            var selectQuery = "SELECT itemid, paintwear FROM searches WHERE paintwear_uint IS NULL";
            using var selectCommand = new SqliteCommand(selectQuery, connection);
            using var reader = await selectCommand.ExecuteReaderAsync();

            var migrationData = new List<(long itemid, double paintwear)>();
            while (await reader.ReadAsync())
            {
                migrationData.Add((reader.GetInt64(0), reader.GetDouble(1)));
            }

            // Close the reader before starting updates
            reader.Close();

            Console.WriteLine($"Processing {migrationData.Count} records...");

            int updated = 0;
            foreach (var (itemid, paintwear) in migrationData)
            {
                // Convert float back to uint32
                var floatBytes = BitConverter.GetBytes((float)paintwear);
                var uint32Value = BitConverter.ToUInt32(floatBytes, 0);

                // Update the record
                var updateQuery = "UPDATE searches SET paintwear_uint = @paintwear_uint WHERE itemid = @itemid";
                using var updateCommand = new SqliteCommand(updateQuery, connection);
                updateCommand.Parameters.AddWithValue("@paintwear_uint", (long)uint32Value);
                updateCommand.Parameters.AddWithValue("@itemid", itemid);

                await updateCommand.ExecuteNonQueryAsync();
                updated++;

                if (updated % 100 == 0)
                {
                    Console.WriteLine($"Migrated {updated}/{migrationData.Count} records...");
                }
            }

            Console.WriteLine($"Migration completed! Updated {updated} records.");

            // Verify the migration
            var verifyQuery = @"
                SELECT 
                    COUNT(*) as total_records,
                    COUNT(paintwear_uint) as migrated_records,
                    COUNT(*) - COUNT(paintwear_uint) as remaining_records
                FROM searches";
            
            using var verifyCommand = new SqliteCommand(verifyQuery, connection);
            using var verifyReader = await verifyCommand.ExecuteReaderAsync();
            
            if (await verifyReader.ReadAsync())
            {
                var total = verifyReader.GetInt32(0);
                var migrated = verifyReader.GetInt32(1);
                var remaining = verifyReader.GetInt32(2);
                
                Console.WriteLine($"\nVerification:");
                Console.WriteLine($"Total records: {total}");
                Console.WriteLine($"Migrated records: {migrated}");
                Console.WriteLine($"Remaining records: {remaining}");
                
                if (remaining == 0)
                {
                    Console.WriteLine("✓ All records have been migrated successfully!");
                }
                else
                {
                    Console.WriteLine($"⚠ Warning: {remaining} records still need migration.");
                }
            }
        }
    }
}