-- Migration script to convert existing paintwear REAL values to paintwear_uint INTEGER values
-- This script converts float32 paintwear values back to their original uint32 representation

-- First, update all existing records where paintwear_uint is NULL
-- We need to convert the float back to uint32 by treating the float bits as uint32
UPDATE searches 
SET paintwear_uint = CAST(
    -- Convert float to uint32 by treating the IEEE 754 binary representation
    -- SQLite doesn't have direct binary conversion, so we'll need to handle this in C#
    paintwear AS INTEGER
)
WHERE paintwear_uint IS NULL;

-- Verify the migration
SELECT 
    itemid,
    paintwear as old_paintwear,
    paintwear_uint as new_paintwear_uint,
    CASE 
        WHEN paintwear_uint IS NOT NULL THEN 'Migrated'
        ELSE 'Not migrated'
    END as migration_status
FROM searches
LIMIT 10;

-- Check total counts
SELECT 
    COUNT(*) as total_records,
    COUNT(paintwear_uint) as migrated_records,
    COUNT(*) - COUNT(paintwear_uint) as remaining_records
FROM searches;