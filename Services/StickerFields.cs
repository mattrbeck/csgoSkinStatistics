namespace CSGOSkinAPI.Services
{
    // protobuf-net models the item proto's optional scalar fields as plain non-nullable properties
    // plus a generated ShouldSerialize<field>() that reports whether the field was actually on the
    // wire. Reading a property straight through collapses "absent" into 0 - and 0 is a real value
    // for rotation, the offsets and pattern - so every read has to pair the property with its
    // ShouldSerialize check. These helpers do that pairing once and hand back a plain nullable, so
    // the DTO builders and the persistence layer stop repeating the ternary at every field.
    public static class StickerFields
    {
        public static float? Scale(this CEconItemPreviewDataBlock.Sticker s)
            => s.ShouldSerializescale() ? s.scale : null;

        public static float? Rotation(this CEconItemPreviewDataBlock.Sticker s)
            => s.ShouldSerializerotation() ? s.rotation : null;

        public static uint? TintId(this CEconItemPreviewDataBlock.Sticker s)
            => s.ShouldSerializetint_id() ? s.tint_id : null;

        public static float? OffsetX(this CEconItemPreviewDataBlock.Sticker s)
            => s.ShouldSerializeoffset_x() ? s.offset_x : null;

        public static float? OffsetY(this CEconItemPreviewDataBlock.Sticker s)
            => s.ShouldSerializeoffset_y() ? s.offset_y : null;

        public static float? OffsetZ(this CEconItemPreviewDataBlock.Sticker s)
            => s.ShouldSerializeoffset_z() ? s.offset_z : null;

        public static uint? Pattern(this CEconItemPreviewDataBlock.Sticker s)
            => s.ShouldSerializepattern() ? s.pattern : null;

        public static uint? HighlightReel(this CEconItemPreviewDataBlock.Sticker s)
            => s.ShouldSerializehighlight_reel() ? s.highlight_reel : null;

        // The live StatTrak kill count, or null on a non-StatTrak item. Presence of the field is
        // itself the StatTrak flag, which callers still read via ShouldSerializekilleatervalue().
        public static uint? StatTrakKills(this CEconItemPreviewDataBlock item)
            => item.ShouldSerializekilleatervalue() ? item.killeatervalue : null;
    }
}
