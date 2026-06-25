namespace CSGOSkinAPI.Services
{
    // A Sticker Slab is a charm that seals a sticker inside it. The sealed sticker's id
    // rides in the item proto's `wrapped_sticker` field (tag 12). SteamKit2 3.3.1 does not
    // model that field, so protobuf-net keeps it as extension data - verified against a real
    // applied slab (sticker_id=37 slab container, wrapped_sticker=4352 sealed sticker). The
    // tag and its read/write live here so decode, persistence and cache-reload stay in lockstep.
    //
    // TODO: when a SteamKit2 bump models `wrapped_sticker` as a generated property, switch to
    // it and delete the extension plumbing. Once the field is "known", Extensible.TryGetValue
    // returns false for it, which would silently blank every slab - so this is a breaking bump
    // to watch for (the StickerSlabTests pin the current extension behaviour).
    public static class StickerSlab
    {
        private const int WrappedStickerTag = 12;

        // Sealed sticker id for a slab, or 0 for an ordinary charm/sticker.
        public static uint GetWrappedStickerId(CEconItemPreviewDataBlock.Sticker sticker)
            => Extensible.TryGetValue<uint>(sticker, WrappedStickerTag, out var id) ? id : 0u;

        // Re-attach a persisted slab id so a cache-reloaded keychain matches a fresh decode.
        public static void SetWrappedStickerId(CEconItemPreviewDataBlock.Sticker sticker, uint id)
            => Extensible.AppendValue<uint>(sticker, WrappedStickerTag, id);
    }
}
