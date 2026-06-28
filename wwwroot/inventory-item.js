// Web Component for Inventory Items
class InventoryItem extends HTMLElement {
  constructor() {
    super();
    this.attachShadow({ mode: 'open' });
    this.itemData = {};
    this.itemIndex = 0;
    this.needsUpdate = false;
  }

  connectedCallback() {
    // Cards are reused across re-renders (filter/sort moves re-connect them), so only
    // build the shadow DOM on the first connection.
    if (!this.shadowRoot.firstChild) {
      this.render();
    }
    // If data was set before connection, update display now
    if (this.needsUpdate) {
      this.updateDisplay();
      this.needsUpdate = false;
    }
  }

  render() {
    const template = document.getElementById('inventory-item-template');
    const clone = template.content.cloneNode(true);
    
    // Build the component stylesheet once and share it across every card via
    // adoptedStyleSheets, instead of injecting (and re-parsing) this CSS into all
    // ~200 shadow roots individually.
    if (!InventoryItem._styleSheet && !InventoryItem._styleText) {
      const css = `
      :host {
        display: block;
        background-color: var(--dark, #0f1d2a);
        border-radius: 8px;
        padding: 15px;
        /* Only the properties that actually animate: hover lift/shadow, the rarity edge color,
           and the loading fade. (Avoids 'all', which would also watch layout properties.) */
        transition: transform 0.3s ease, box-shadow 0.3s ease, border-left-color 0.3s ease, opacity 0.3s ease;
        border-left: 4px solid var(--gray, #1f2d3a);
        position: relative;
      }
      :host(:hover) {
        transform: translateY(-2px);
        box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
      }
      :host(.loading) {
        opacity: 0.6;
        overflow: hidden;
      }
      :host(.loading)::before {
        content: "";
        position: absolute;
        left: -100%;
        width: 100%;
        height: 100%;
        background: linear-gradient(90deg, transparent, var(--light, #2f3d4a), transparent);
        animation: shimmer 2s infinite;
        top: 0;
      }
      :host(.loaded:not([style*="border-left-color"])) {
        border-left-color: var(--pop, #2ecc71);
      }
      :host(.error) {
        border-left-color: var(--error, #cc492f);
      }
      
      .item-content {
        display: flex;
        gap: 12px;
        align-items: flex-start;
      }
      
      .item-left {
        flex-shrink: 0;
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 8px;
      }
      
      .item-image-container {
        width: 64px;
        height: 48px;
        background-color: var(--gray, #34495e);
        border-radius: 4px;
        overflow: hidden;
        display: flex;
        align-items: center;
        justify-content: center;
      }
      
      .item-image {
        max-width: 100%;
        max-height: 48px;
        width: auto;
        height: auto;
        object-fit: contain;
      }
      
      .item-info {
        flex: 1;
        min-width: 0;
      }

      .item-name {
        font-weight: bold;
        font-size: 15px;
        color: var(--text, #ecf0f1);
        margin: 0 0 8px 0;
        word-wrap: break-word;
      }
      
      .item-name.knife::before {
        content: "\\2605  ";
        color: var(--pop, #2ecc71);
        line-height: 0;
      }
      
      .item-name.souvenir::before {
        content: "Souvenir  ";
        color: var(--souvenir, #ccb22f);
      }

      .item-name.genuine::before {
        content: "Genuine  ";
        color: var(--genuine, #4D7455);
      }

      .item-name.vintage::before {
        content: "Vintage  ";
        color: var(--vintage, #476291);
      }

      .item-name.valve::before {
        content: "Valve  ";
        color: var(--valve, #A050CF);
      }

      .item-name.selfmade::before {
        content: "Self-Made  ";
        color: var(--selfmade, #70B04A);
      }
      
      .stattrak-badge {
        display: inline-flex;
        align-items: center;
        vertical-align: middle;
        margin-left: 9px;
        padding: 2px 7px;
        background-color: var(--stattrak, #e0843a);
        color: #16100a;
        font-family: var(--display, "Rajdhani", "Lato", sans-serif);
        font-size: 11px;
        font-weight: 700;
        letter-spacing: 0.5px;
        white-space: nowrap;
        cursor: default;
        transform: skewX(-12deg);
      }

      /* The kill count slides out of the ST badge on hover; :focus covers taps
         (the badge is focusable) and keyboard users. */
      .st-detail {
        display: inline-block;
        max-width: 0;
        overflow: hidden;
        white-space: pre;
        transition: max-width 0.25s ease;
      }

      .stattrak-badge:is(:hover, :focus) .st-detail {
        max-width: 110px;
      }

      .item-details {
        display: flex;
        flex-direction: column;
        gap: 6px;
        font-size: 13px;
      }

      .detail-line {
        display: flex;
        align-items: center;
        gap: 6px;
        min-height: 18px;
      }

      .detail-label {
        color: var(--text, #ecf0f1);
        font-family: var(--display, "Rajdhani", "Lato", sans-serif);
        font-size: 12px;
        text-transform: uppercase;
        letter-spacing: 0.6px;
        opacity: 0.6;
      }

      .detail-value {
        color: var(--text, #ecf0f1);
        font-weight: 500;
      }

      /* Hovering (or tapping/focusing) the float slides out the remaining precision
         digits (the shown value is truncated, not rounded, so the full float is
         short + rest); the bar yields its space as the digits expand. */
      .float-value {
        display: inline-flex;
        align-items: baseline;
        white-space: nowrap;
      }

      .float-rest {
        display: inline-block;
        max-width: 0;
        overflow: hidden;
        white-space: pre;
        transition: max-width 0.25s ease;
      }

      .float-value:is(:hover, :focus) .float-rest {
        max-width: 100px;
      }

      .float-value:is(:hover, :focus) ~ .float-bar {
        min-width: 0;
      }

      .wear-pill {
        font-family: var(--display, "Rajdhani", "Lato", sans-serif);
        font-size: 11px;
        font-weight: 700;
        letter-spacing: 0.5px;
        line-height: 1.25;
        padding: 1px 6px;
        margin-right: 2px;
        border: 1px solid currentColor;
        flex-shrink: 0;
        transform: skewX(-12deg);
      }
      .wear-pill.fn { color: #2ecc71; }
      .wear-pill.mw { color: #82c91e; }
      .wear-pill.ft { color: #f1c40f; }
      .wear-pill.ww { color: #e67e22; }
      .wear-pill.bs { color: #e74c3c; }

      /* 0-1 float scale with the five wear zones; the marker shows where this item sits. */
      .float-bar {
        position: relative;
        flex: 1;
        min-width: 36px;
        height: 5px;
        border-radius: 3px;
        opacity: 0.85;
        background: linear-gradient(90deg,
          #2ecc71 0 7%, #82c91e 7% 15%, #f1c40f 15% 38%, #e67e22 38% 45%, #e74c3c 45% 100%);
      }

      /* Invisible halo so the 5px bar doesn't demand pixel-perfect hovering for its
         tooltip. */
      .float-bar::after {
        content: '';
        position: absolute;
        left: 0;
        right: 0;
        top: -7px;
        bottom: -7px;
      }

      /* Instant styled tooltip with the skin's possible wear range (native title
         tooltips are too slow and small to discover). Shown on focus too, so it is
         reachable by tap and keyboard. */
      .float-bar[data-range]::before {
        content: attr(data-range);
        position: absolute;
        bottom: calc(100% + 7px);
        left: 50%;
        /* --tt-shift nudges the tooltip back on-screen near a viewport edge (set by enableTooltip). */
        transform: translateX(calc(-50% + var(--tt-shift, 0px)));
        padding: 3px 8px;
        border-radius: 4px;
        border: 1px solid var(--light, #2f3d4a);
        background-color: var(--gray, #1f2d3a);
        color: var(--text, #ecf0f1);
        font-size: 11px;
        font-weight: 600;
        white-space: nowrap;
        opacity: 0;
        pointer-events: none;
        transition: opacity 0.1s ease;
        z-index: 3;
      }

      .float-bar[data-range]:is(:hover, :focus)::before {
        opacity: 1;
      }

      /* Wear values this skin's paint kit can't roll, dimmed like the float filter
         slider's out-of-range portions. */
      .float-bar-dim {
        position: absolute;
        top: 0;
        height: 100%;
        background-color: rgb(15 29 42 / 70%);
      }

      .float-bar-dim:first-child {
        left: 0;
        border-radius: 3px 0 0 3px;
      }

      .float-bar-dim:nth-child(2) {
        right: 0;
        border-radius: 0 3px 3px 0;
      }

      .float-marker {
        position: absolute;
        top: -2.5px;
        width: 2px;
        height: 10px;
        margin-left: -1px;
        border-radius: 1px;
        background-color: var(--text, #ecf0f1);
      }

      .rarity-text {
        margin-left: auto;
        padding-left: 8px;
        font-family: var(--display, "Rajdhani", "Lato", sans-serif);
        font-size: 12px;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.8px;
        white-space: nowrap;
        opacity: 0.9;
      }

      .loading-placeholder {
        color: var(--text, #ecf0f1);
        opacity: 0.5;
        font-style: italic;
      }

      .error-message {
        color: var(--error, #cc492f) !important;
      }

      .item-actions {
        display: flex;
        gap: 4px;
        width: 100%;
        box-sizing: border-box;
      }

      .icon-btn {
        flex: 1;
        display: flex;
        align-items: center;
        justify-content: center;
        height: 22px;
        border-radius: 3px;
        border: 1px solid var(--light, #2f3d4a);
        background-color: var(--gray, #1f2d3a);
        color: var(--pop, #2ecc71);
        text-decoration: none;
        transition: background-color 0.2s ease, color 0.2s ease;
      }

      .icon-btn svg {
        width: 13px;
        height: 13px;
      }

      .icon-btn:hover {
        background-color: var(--pop, #2ecc71);
        color: var(--gray, #1f2d3a);
      }

      /* The inspect link is a steam:// URL; suppress iOS Safari's native long-press menu (which
         can't copy a custom-scheme link) so enableLongPressCopy can handle the hold instead. */
      .icon-btn[data-field="inspect-link"] {
        -webkit-touch-callout: none;
      }

      /* Held past the copy threshold, awaiting release. */
      .icon-btn.arming {
        background-color: var(--pop, #2ecc71);
        color: var(--gray, #1f2d3a);
      }

      /* Brief confirmation after a long-press copy (mobile has no hover state to borrow). */
      .icon-btn.copied {
        position: relative;
        background-color: var(--pop, #2ecc71);
        color: var(--gray, #1f2d3a);
      }

      .icon-btn.copied::after {
        content: 'Copied';
        position: absolute;
        bottom: calc(100% + 6px);
        left: 50%;
        transform: translateX(-50%);
        padding: 3px 8px;
        border-radius: 4px;
        border: 1px solid var(--light, #2f3d4a);
        background-color: var(--gray, #1f2d3a);
        color: var(--text, #ecf0f1);
        font-size: 11px;
        font-weight: 600;
        white-space: nowrap;
        pointer-events: none;
        z-index: 4;
      }

      /* Applied stickers / charms: a compact thumbnail row that wraps if an item is
         loaded out (max 5 stickers + a charm). Hidden entirely when the item has none. */
      .item-stickers {
        display: flex;
        flex-wrap: wrap;
        gap: 4px;
        margin-top: 8px;
      }
      .item-stickers[hidden] {
        display: none;
      }

      .sticker-chip {
        position: relative;
        width: 28px;
        height: 28px;
        flex-shrink: 0;
        display: flex;
        align-items: center;
        justify-content: center;
        border-radius: 4px;
        border: 1px solid var(--light, #2f3d4a);
        background-color: var(--gray, #1f2d3a);
      }

      /* Charms get the accent border so they read as a different kind of decal even
         though the prefix is stripped from their name. */
      .sticker-chip.charm {
        border-color: var(--pop, #2ecc71);
      }

      /* A Sticker Slab sits in the charm slot but shows a sealed sticker; its own accent
         marks it as neither a plain sticker nor a plain charm. */
      .sticker-chip.charm.slab {
        border-color: #5e98d9;
      }

      .sticker-chip img {
        max-width: 100%;
        max-height: 100%;
        display: block;
      }

      .sticker-chip.placeholder {
        font-size: 13px;
        font-weight: 700;
        color: var(--text, #ecf0f1);
        opacity: 0.5;
      }

      /* Name (and scrape level) on hover/focus, mirroring the float-bar tooltip so it is
         reachable by tap and keyboard, not just mouse. */
      .sticker-chip[data-label]::after {
        content: attr(data-label);
        position: absolute;
        bottom: calc(100% + 6px);
        left: 50%;
        /* --tt-shift nudges the tooltip back on-screen near a viewport edge (set by enableTooltip). */
        transform: translateX(calc(-50% + var(--tt-shift, 0px)));
        padding: 3px 8px;
        border-radius: 4px;
        border: 1px solid var(--light, #2f3d4a);
        background-color: var(--gray, #1f2d3a);
        color: var(--text, #ecf0f1);
        font-size: 11px;
        font-weight: 600;
        white-space: nowrap;
        opacity: 0;
        pointer-events: none;
        transition: opacity 0.1s ease;
        z-index: 4;
      }
      .sticker-chip[data-label]:is(:hover, :focus)::after {
        opacity: 1;
      }

      @keyframes shimmer {
        0% { left: -100%; }
        25% { left: -100%; }
        100% { left: 100%; }
      }

      @media (max-width: 768px) {
        .item-content {
          gap: 10px;
        }

        .item-left {
          gap: 6px;
        }

        .item-image-container {
          width: 80px;
          height: 60px;
        }

        .item-image {
          max-height: 60px;
        }
      }
    `;
      if ('adoptedStyleSheets' in Document.prototype &&
          typeof CSSStyleSheet !== 'undefined' && 'replaceSync' in CSSStyleSheet.prototype) {
        InventoryItem._styleSheet = new CSSStyleSheet();
        InventoryItem._styleSheet.replaceSync(css);
      } else {
        InventoryItem._styleText = css; // fallback for browsers without constructable stylesheets
      }
    }

    if (InventoryItem._styleSheet) {
      this.shadowRoot.adoptedStyleSheets = [InventoryItem._styleSheet];
    } else {
      const style = document.createElement('style');
      style.textContent = InventoryItem._styleText;
      this.shadowRoot.appendChild(style);
    }
    this.shadowRoot.appendChild(clone);
  }

  setItemData(item, index) {
    this.itemData = item;
    this.itemIndex = index;
    
    // If component is connected and rendered, update immediately
    if (this.isConnected && this.shadowRoot && this.shadowRoot.children.length > 0) {
      this.updateDisplay();
    } else {
      // Mark that we need to update when connected
      this.needsUpdate = true;
    }
  }

  updateDisplay() {
    if (!this.shadowRoot) {
      return;
    }

    const nameElement = this.shadowRoot.querySelector('[data-field="name"]');
    const rarityElement = this.shadowRoot.querySelector('[data-field="rarity"]');
    const inspectElement = this.shadowRoot.querySelector('[data-field="inspect-link"]');
    const steamLinkElement = this.shadowRoot.querySelector('[data-field="steam-link"]');
    const imageElement = this.shadowRoot.querySelector('[data-field="image"]');


    if (nameElement) {
      // Determine if this is a knife or souvenir from the item info
      nameElement.className = 'item-name';
      if (this.itemData.quality === 'Souvenir') {
        nameElement.classList.add('souvenir');
      }
      if (this.itemData.type && this.itemData.type.includes('★')) {
        nameElement.classList.add('knife');
      }
      let itemName = this.itemData.name || this.itemData.market_name || 'Unknown Item';
      // Remove leading ★ characters since CSS will add them back for knives/gloves
      itemName = itemName.replace(/^★\s*/, '');
      nameElement.textContent = itemName;
    }

    this.updateWearPill(this.itemData.wear);

    if (rarityElement) {
      const rarity = this.itemData.rarity;
      rarityElement.textContent = (rarity && rarity !== 'Unknown') ? rarity : '';
      rarityElement.style.color = rarityColorOf(rarity);
    }

    if (inspectElement) {
      inspectElement.href = this.itemData.inspect_link || '#';
      enableLongPressCopy(inspectElement); // long-press to copy the steam:// link (iOS Safari)
    }

    // Deep link to this exact item in the owner's Steam inventory page (730_2 is CS2's
    // app/context id). The owner id comes from the resolved inventory response; classic
    // S...A... inspect links carry both ids and serve as a fallback.
    if (steamLinkElement) {
      const match = (this.itemData.inspect_link || '').match(/S(\d+)A(\d+)/);
      const steamid = currentOwnerSteamId || (match && match[1]);
      const assetid = this.itemData.assetid || (match && match[2]);
      if (steamid && assetid) {
        steamLinkElement.href = `https://steamcommunity.com/profiles/${steamid}/inventory#730_2_${assetid}`;
        steamLinkElement.style.display = '';
      } else {
        steamLinkElement.style.display = 'none';
      }
    }

    // Set item image
    if (imageElement) {
      // community.cloudflare.steamstatic.com is the CDN Steam itself serves economy
      // images from; the old steamcommunity-a.akamaihd.net host is deprecated.
      if (this.itemData.icon_url_large) {
        imageElement.src = `https://community.cloudflare.steamstatic.com/economy/image/${this.itemData.icon_url_large}`;
      } else if (this.itemData.icon_url) {
        imageElement.src = `https://community.cloudflare.steamstatic.com/economy/image/${this.itemData.icon_url}`;
      } else {
        // Fallback placeholder image
        imageElement.src = 'data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iODAiIGhlaWdodD0iNjAiIHZpZXdCb3g9IjAgMCA4MCA2MCIgZmlsbD0ibm9uZSIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj4KPHJlY3Qgd2lkdGg9IjgwIiBoZWlnaHQ9IjYwIiBmaWxsPSIjMkYzRDRBIi8+CjxwYXRoIGQ9Ik0zNSAyNUg0NVYzNUgzNVYyNVoiIGZpbGw9IiM1RTk4RDkiLz4KPHBhdGggZD0iTTQwIDIwVjQwIiBzdHJva2U9IiM1RTk4RDkiIHN0cm9rZS13aWR0aD0iMiIvPgo8cGF0aCBkPSJNMzAgMzBINTAiIHN0cm9rZT0iIzVFOThEOSIgc3Ryb2tlLXdpZHRoPSIyIi8+Cjwvc3ZnPgo=';
      }
      imageElement.alt = this.itemData.name || 'CS2 Item';
    }

    // Set border color based on item rarity
    const rarityColor = this.getRarityColor(this.itemData.rarity);
    if (rarityColor) {
      this.style.setProperty('border-left-color', rarityColor);
    }

    this.classList.add('loading');
  }

  getRarityColor(rarity) {
    return rarityColorOf(rarity);
  }

  // Dim the parts of the float bar this skin can't roll. Each paint kit remaps the raw
  // 0-1 float into its own wear interval (commonly 0.06-0.80); the unreachable ends are
  // dimmed the same way the float filter slider dims its unselected range.
  applyFloatRange() {
    if (!floatRanges || this.paintIndex == null) return;
    const range = floatRanges[this.paintIndex];
    const bar = this.shadowRoot.querySelector('[data-field="float-bar"]');
    const left = this.shadowRoot.querySelector('[data-field="float-dim-left"]');
    const right = this.shadowRoot.querySelector('[data-field="float-dim-right"]');
    if (!range || !bar || !left || !right) return;
    const [min, max] = range;
    left.hidden = !(min > 0);
    left.style.width = `${min * 100}%`;
    right.hidden = !(max < 1);
    right.style.width = `${(1 - max) * 100}%`;

    // Feeds the instant styled tooltip (.float-bar::before shows attr(data-range)) with
    // the skin's possible wear range at the data's natural precision (0-0.672, 0.06-0.8).
    // Focusable so the tooltip is reachable by tap and keyboard, not just hover.
    bar.dataset.range = `Range: ${min}-${max}`;
    bar.tabIndex = 0;
    enableTooltip(bar); // keep the tooltip on-screen near a grid edge (shared with decals.js)
  }

  // Compact wear badge (FN/MW/FT/WW/BS), colored to match the float bar zones.
  updateWearPill(wearName) {
    const pill = this.shadowRoot.querySelector('[data-field="wear-pill"]');
    if (!pill) return;
    const abbrev = WEAR_ABBREVIATIONS[wearName];
    if (abbrev) {
      pill.textContent = abbrev;
      pill.className = `wear-pill ${abbrev.toLowerCase()}`;
      pill.title = wearName;
      pill.hidden = false;
    } else {
      pill.hidden = true;
    }
  }

  updateWithDetails(itemData, inspectLink) {
    const floatElement = this.shadowRoot.querySelector('[data-field="float"]');
    const floatLabel = this.shadowRoot.querySelector('[data-field="float-label"]');
    const floatBar = this.shadowRoot.querySelector('[data-field="float-bar"]');
    const floatMarker = this.shadowRoot.querySelector('[data-field="float-marker"]');
    const floatLine = this.shadowRoot.querySelector('[data-field="float-line"]');
    const patternLine = this.shadowRoot.querySelector('[data-field="pattern-line"]');
    const rarityElement = this.shadowRoot.querySelector('[data-field="rarity"]');
    const patternElement = this.shadowRoot.querySelector('[data-field="pattern"]');
    const nameElement = this.shadowRoot.querySelector('[data-field="name"]');
    const inspectElement = this.shadowRoot.querySelector('[data-field="inspect-link"]');

    if (itemData.error) {
      // Keep existing basic info, just mark as error and update loading fields
      this.classList.remove('loading');
      this.classList.add('error');
      
      // Only update the fields that were showing "Analyzing..."
      if (floatElement) {
        floatElement.innerHTML = 'Analysis Failed';
        floatElement.classList.remove('loading-placeholder');
        floatElement.classList.add('error-message');
      }
      
      if (patternElement) {
        patternElement.innerHTML = 'Analysis Failed';
        patternElement.classList.remove('loading-placeholder');
        patternElement.classList.add('error-message');
      }
      
      // Add small error indicator but don't replace the whole item
      let existingError = this.shadowRoot.querySelector('.error-message-small');
      if (!existingError) {
        const errorDiv = document.createElement('div');
        errorDiv.className = 'error-message-small';
        errorDiv.textContent = 'Detailed analysis failed';
        errorDiv.style.cssText = 'color: var(--error, #cc492f); font-size: 10px; font-style: italic; margin-top: 8px; text-align: center; opacity: 0.8;';
        this.shadowRoot.appendChild(errorDiv);
      }
      return;
    }

    this.classList.remove('loading');
    this.classList.add('loaded');

    // Items with no paint (paintindex 0) - medals, coins, pins, music kits, graffiti,
    // vanilla knives, etc. - have no float, pattern, or skin. "Float: 0", "Pattern: 0",
    // "Factory New" and "| Vanilla" are all meaningless for them, so we drop those rows.
    const hasSkin = Number(itemData.paintindex) > 0;
    if (floatLine) floatLine.style.display = hasSkin ? '' : 'none';
    if (patternLine && !hasSkin) {
      // Keep the line itself when there's a rarity to show; just drop the pattern part.
      const hasRarity = rarityElement && rarityElement.textContent;
      patternLine.style.display = hasRarity ? '' : 'none';
      if (patternElement) patternElement.style.display = 'none';
      const patternLabel = patternLine.querySelector('.detail-label');
      if (patternLabel) patternLabel.style.display = 'none';
    }
    if (!hasSkin) this.updateWearPill(null);

    // Enhance the name with detailed info if we got weapon/skin data
    if (itemData.weapon && itemData.skin && nameElement) {
      nameElement.className = 'item-name';
      // Skinned weapons show "Weapon | Skin". For skinless items (stickers, music kits,
      // graffiti, etc.) the GC only returns a generic category word, so keep Steam's full
      // name (the ★ is stripped here and re-added via CSS for knives/gloves).
      // Built as DOM nodes, never innerHTML: the Steam name is remote data.
      const nameText = hasSkin
        ? `${itemData.weapon} | ${itemData.skin}`
        : (this.itemData.name || this.itemData.market_name || itemData.weapon || '').replace(/^★\s*/, '');
      nameElement.textContent = nameText;
      if (itemData.special) {
        const special = document.createElement('span');
        special.className = 'item-special';
        special.style.cssText = 'color: var(--pop, #2ecc71); font-weight: bold; margin-left: 5px;';
        special.textContent = itemData.special;
        nameElement.appendChild(special);
      }
      if (itemData.stattrak) {
        const badge = document.createElement('span');
        badge.className = 'stattrak-badge';
        badge.textContent = 'ST';
        // The kill count slides out of the badge on hover or focus (see .st-detail);
        // the tabindex makes taps focus the badge, so it works on touch too.
        const kills = this.itemData?.stattrak_kills;
        if (kills != null) {
          const detail = document.createElement('span');
          detail.className = 'st-detail';
          detail.textContent = `: ${kills.toLocaleString()} Kills`;
          badge.appendChild(detail);
          badge.tabIndex = 0;
        }
        nameElement.appendChild(badge);
      }
      // Check for knife/glove using defindex (500-600 for knives, 5000+ for gloves)
      // This is more reliable than quality === 3, since StatTrak knives have quality 9
      if (isKnifeOrGlove(itemData.defindex)) {
        nameElement.classList.add('knife');
      }
      // Handle special qualities (quality field defines provenance/category)
      if (itemData.quality === 1) {
        nameElement.classList.add('genuine');
      } else if (itemData.quality === 2) {
        nameElement.classList.add('vintage');
      } else if (itemData.quality === 6) {
        nameElement.classList.add('valve');
      } else if (itemData.quality === 7) {
        nameElement.classList.add('selfmade');
      } else if (itemData.quality === 12) {
        nameElement.classList.add('souvenir');
      }
    }

    // Update float value - 6 decimal places at rest; hovering slides out the remaining
    // precision digits (the bar shrinks to make room - see .float-value CSS). Truncate
    // rather than round so the rest of the digits are a pure continuation.
    if (floatElement && hasSkin) {
      const paintwearFloat = uint32ToFloat32(itemData.paintwear);
      const fullFloat = paintwearFloat.toString();
      const dot = fullFloat.indexOf('.');
      const splitAt = (dot !== -1 && !fullFloat.includes('e')) ? dot + 7 : fullFloat.length;
      const floatMarkup =
        `<span class="float-short">${fullFloat.slice(0, splitAt)}</span>` +
        `<span class="float-rest">${fullFloat.slice(splitAt)}</span>`;
      floatElement.innerHTML = floatMarkup;
      floatElement.classList.add('float-value');
      floatElement.style.cursor = 'copy';
      floatElement.classList.remove('loading-placeholder');

      // The wear pill + value + position bar say it all; drop the "Float:" label.
      if (floatLabel) floatLabel.style.display = 'none';
      if (floatBar && floatMarker) {
        floatMarker.style.left = `${Math.min(100, Math.max(0, paintwearFloat * 100))}%`;
        floatBar.hidden = false;
      }
      this.paintIndex = Number(itemData.paintindex);
      this.applyFloatRange();
      this.updateWearPill(getWearFromFloat(paintwearFloat));

      // Click/Enter/Space to copy. The tabindex also makes taps focus the value,
      // which slides out the full precision on touch (see .float-value:focus).
      const copyFloat = () => {
        navigator.clipboard.writeText(fullFloat).then(() => {
          floatElement.textContent = 'Copied!';
          setTimeout(() => {
            floatElement.innerHTML = floatMarkup;
          }, 1000);
        }).catch(() => { /* clipboard blocked (insecure context / denied); leave the value as-is */ });
      };
      floatElement.onclick = copyFloat;
      floatElement.tabIndex = 0;
      floatElement.setAttribute('role', 'button');
      floatElement.setAttribute('aria-label', `Copy float value ${fullFloat}`);
      floatElement.onkeydown = (event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          copyFloat();
        }
      };
    }


    // Keep Steam's rarity (set during the basic render) - it is correct for every item
    // category. Only fall back to the numeric GC rarity when Steam gave us nothing, since
    // getRarityFromNumber is a weapon-only ladder and mislabels medals, stickers, agents, etc.
    if (rarityElement && !rarityElement.textContent) {
      const detailedRarity = getRarityFromNumber(itemData.rarity);
      if (detailedRarity !== 'Unknown') {
        rarityElement.textContent = detailedRarity;
        rarityElement.style.color = rarityColorOf(detailedRarity);
      }
    }

    // Update pattern seed
    if (patternElement && hasSkin) {
      patternElement.textContent = itemData.paintseed;
      patternElement.classList.remove('loading-placeholder');
    }

    // Ensure inspect link is set
    if (inspectElement) {
      inspectElement.href = inspectLink || '#';
    }

    this.renderStickers(itemData);
  }

  // Applied stickers and charms, as a compact row of thumbnails (shared with the item page
  // via buildStickerChips). Sort/filter re-creates cards and re-calls updateWithDetails, so
  // this fully rebuilds the row each time.
  renderStickers(itemData) {
    const container = this.shadowRoot.querySelector('[data-field="stickers"]');
    if (!container) return;

    const hasDecals = (itemData.stickers || []).length > 0 || (itemData.keychains || []).length > 0;
    if (!hasDecals) {
      container.replaceChildren();
      container.hidden = true;
      return;
    }
    container.replaceChildren(buildStickerChips(itemData.stickers, itemData.keychains));
    container.hidden = false;
  }
}

// Register the custom element
customElements.define('inventory-item', InventoryItem);
