namespace CSGOSkinAPI.Services
{
    // Bound from the "InventorySnapshots" configuration section. The snapshots are the last good
    // /api/inventory payload per Steam ID, kept on disk so a Steam outage or throttle degrades to
    // "here is what we had" instead of an error page. Disk on the host is small, so both knobs
    // bound the table: rows older than RetentionDays go on every write, and so do the oldest rows
    // past MaxRows. Payloads are gzip-compressed; a large inventory is a few hundred KB stored.
    public sealed class InventorySnapshotOptions
    {
        public const string SectionName = "InventorySnapshots";

        public int RetentionDays { get; set; } = 7;

        public int MaxRows { get; set; } = 2000;
    }
}
