-- Migration script to drop old paintwear column and rename paintwear_uint to paintwear
-- Run this script against your SQLite database
-- Requires SQLite 3.35.0+ for DROP COLUMN support

BEGIN TRANSACTION;

-- Drop the old paintwear column
ALTER TABLE searches DROP COLUMN paintwear;

-- Rename paintwear_uint to paintwear
ALTER TABLE searches RENAME COLUMN paintwear_uint TO paintwear;

COMMIT;

-- Verify the migration worked
SELECT COUNT(*) as total_records FROM searches;
SELECT * FROM searches LIMIT 5;