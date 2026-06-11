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
        transition: all 0.3s ease;
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
        margin-left: 8px;
        padding: 2px 6px;
        border-radius: 3px;
        background-color: var(--pop, #2ecc71);
        color: var(--gray, #1f2d3a);
        font-size: 10px;
        font-weight: bold;
        white-space: nowrap;
        cursor: default;
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
        opacity: 0.7;
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
        font-size: 10px;
        font-weight: bold;
        line-height: 1.3;
        padding: 1px 5px;
        border-radius: 3px;
        border: 1px solid currentColor;
        flex-shrink: 0;
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
        transform: translateX(-50%);
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
        font-size: 11px;
        font-weight: 600;
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
        });
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
  }
}

// Register the custom element
customElements.define('inventory-item', InventoryItem);

// Per-paint-kit wear intervals (paint index -> [min, max]) used to dim the unreachable
// parts of each card's float bar. Generated by scripts/update-skin-data.mjs. Cards that
// render before the fetch resolves are back-filled once it lands.
let floatRanges = null;
fetch('float-ranges.json')
  .then(r => (r.ok ? r.json() : null))
  .then(data => {
    floatRanges = data;
    document.querySelectorAll('inventory-item').forEach(el => el.applyFloatRange());
  })
  .catch(() => { /* cosmetic enhancement; bars simply stay undimmed */ });

let elements;
let analysisController = null; // AbortController for canceling requests
let isCancelled = false;
const conversionBuffer = new ArrayBuffer(4);
const conversionView = new DataView(conversionBuffer);

// Inventory data and controls
let currentOwnerSteamId = null; // Resolved SteamId64 of the inventory being viewed
let inventoryItems = []; // Store all items with their data
let filteredItems = []; // Store currently filtered/sorted items
// One card element per item, keyed by originalIndex, built once when the inventory
// loads and reused (moved, attached, detached) across every re-render. Detached cards
// keep their shadow DOM state, so analysis results applied while an item is filtered
// out are still there when it reappears.
let itemElements = new Map();
let currentSort = { field: 'rarity', order: 'desc' };

function defaultFilters() {
  return {
    rarity: '', wear: '', floatMin: null, floatMax: null, hideCommemorative: true,
    search: '', type: '', star: false, stattrak: false, souvenir: false, special: false
  };
}
let currentFilters = defaultFilters();

// Analysis queue management
let itemsNeedingAnalysis = []; // Items that need detailed analysis
let analyzedCount = 0; // Track how many items have been analyzed

function uint32ToFloat32(uint32Value) {
  conversionView.setUint32(0, uint32Value);
  return conversionView.getFloat32(0);
}

// Loose name matching: lowercase, fold accents (Mjölnir -> mjolnir), and treat every run
// of punctuation/whitespace (|, ★, ™, spaces) as one separator, so "gloves crimson"
// finds "★ Specialist Gloves | Crimson Kimono".
function normalizeSearchText(text) {
  return (text || '')
    .toLowerCase()
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .replace(/[^a-z0-9]+/g, ' ')
    .trim();
}

// Sort key for name sorting: drop the provenance prefixes (★, StatTrak™, Souvenir) so
// e.g. a StatTrak™ AK-47 sorts under A with the other AK-47s, not under S.
function nameSortKey(steamData) {
  return (steamData.name || '')
    .replace(/^★\s*/, '')
    .replace(/^StatTrak™\s*/, '')
    .replace(/^Souvenir\s+/, '')
    .toLowerCase();
}

// The item's float, or null when it doesn't have one: unanalyzed items, and paint-less
// items (medals, coins, music kits, ...; paintindex 0) whose paintwear of 0 from the GC
// is not a real float.
function getItemFloat(item) {
  if (!item.detailedData || item.detailedData.paintwear == null ||
      Number(item.detailedData.paintindex) === 0) {
    return null;
  }
  return uint32ToFloat32(item.detailedData.paintwear);
}

function matchesSearch(item, normalizedQuery) {
  if (!normalizedQuery) return true;
  if (item.searchText === undefined) {
    item.searchText = normalizeSearchText(item.steamData.name);
  }
  return normalizedQuery.split(' ').every(token => item.searchText.includes(token));
}

const WEAR_ABBREVIATIONS = {
  'Factory New': 'FN',
  'Minimal Wear': 'MW',
  'Field-Tested': 'FT',
  'Well-Worn': 'WW',
  'Battle-Scarred': 'BS'
};

function getWearFromFloat(float) {
  if (float < 0.07) return "Factory New";
  if (float < 0.15) return "Minimal Wear";
  if (float < 0.38) return "Field-Tested";
  if (float < 0.45) return "Well-Worn";
  return "Battle-Scarred";
}

function getRarityFromNumber(rarityNumber) {
  const rarities = [
    "Default",
    "Consumer Grade",
    "Industrial Grade",
    "Mil-Spec Grade",
    "Restricted",
    "Classified",
    "Covert",
    "Contraband",
  ];
  return rarities[rarityNumber] || "Unknown";
}

// Knives and gloves (and only they) carry the ★ marker in their Steam name/type.
// Rarity can't tell them apart: knives are Covert and high-tier stickers share the
// gloves' Extraordinary rarity.
function isStarItem(steamData) {
  return ((steamData && steamData.name) || '').includes('★') ||
         ((steamData && steamData.type) || '').includes('★');
}

// Check if defindex belongs to knife/glove category (500+ for knives, 5000+ for gloves)
function isKnifeOrGlove(defindex) {
  // Knives typically have defindex 500-600
  // Gloves typically have defindex 5000+
  return (defindex >= 500 && defindex < 600) || defindex >= 5000;
}

function createItemElement(item, index) {
  const itemElement = document.createElement('inventory-item');
  itemElement.id = `item-${index}`;
  itemElement.setItemData(item, index);
  return itemElement;
}

function updateItemWithDetails(itemData, index, inspectLink) {
  // Look the card up in the element map, not the document: a card filtered out of the
  // grid is detached but must still receive its analysis results.
  const itemElement = itemElements.get(index);
  if (itemElement && itemElement.updateWithDetails) {
    itemElement.updateWithDetails(itemData, inspectLink);
    return true;
  }
  return false;
}

function updateProgress(completed, total) {
  const progressFill = elements.progressFill;
  const progressText = elements.progressText;
  
  const percentage = total > 0 ? (completed / total) * 100 : 0;
  progressFill.style.width = `${percentage}%`;
  progressText.textContent = `${completed} / ${total} detailed analyses complete`;
}

function updateSummary(inventoryData, processedItems) {
  elements.inventorySummary.style.display = 'block';
  renderRarityBar();
  renderHighlights(processedItems);
  // Profile header (avatar/persona/trade-ban) is populated separately by the parallel
  // /api/profile fetch kicked off in analyzeInventory.
}

// Stacked bar + legend showing the rarity makeup of the inventory. Rarity comes from the
// basic Steam data, so this is fully populated from the first render (no GC lookup needed).
function renderRarityBar() {
  // Group by Steam color rather than rarity name: players read an inventory by color
  // tier, and several names share one color (Covert/Extraordinary are both red, etc.).
  const byColor = new Map(); // color -> { count, value, names }
  for (const it of inventoryItems) {
    const rarity = it.steamData && it.steamData.rarity;
    if (!rarity || rarity === 'Unknown') continue;
    const color = rarityColorOf(rarity);
    const group = byColor.get(color) || { count: 0, value: 0, names: new Set() };
    group.count += 1;
    group.value = Math.max(group.value, getRarityValue(rarity));
    group.names.add(rarity);
    byColor.set(color, group);
  }

  // Highest tier first so the bar reads rare -> common, left to right.
  const groups = [...byColor.entries()].sort((a, b) => b[1].value - a[1].value);
  const total = groups.reduce((sum, [, g]) => sum + g.count, 0);

  elements.rarityBar.innerHTML = '';
  elements.rarityLegend.innerHTML = '';
  if (total === 0) return;

  for (const [color, group] of groups) {
    // Several rarity names can share one Steam color (e.g. Covert + Extraordinary).
    const tierName = [...group.names].sort((a, b) => getRarityValue(b) - getRarityValue(a)).join(' / ');

    const segment = document.createElement('div');
    segment.className = 'rarity-segment';
    segment.style.width = `${(group.count / total) * 100}%`;
    segment.style.backgroundColor = color;
    segment.title = `${group.count} ${tierName}`;
    elements.rarityBar.appendChild(segment);

    const item = document.createElement('span');
    item.className = 'rarity-legend-item';
    item.title = tierName;
    const dot = document.createElement('span');
    dot.className = 'rarity-dot';
    dot.style.backgroundColor = color;
    const label = document.createElement('span');
    label.innerHTML = `<strong>${group.count}</strong>`;
    item.append(dot, label);
    elements.rarityLegend.appendChild(item);
  }
}

// Quick "what's notable in here" chips. The item count is known up front; StatTrak and
// special-pattern counts come from the GC analysis, so they grow as items resolve.
function renderHighlights(processedItems) {
  const knives = inventoryItems.filter(it => isStarItem(it.steamData)).length;
  const stattrak = processedItems.filter(item => item && item.stattrak).length;
  const special = processedItems.filter(item => item && item.special).length;

  const chips = [`${inventoryItems.length} CS2 items`];
  if (knives) chips.push(`${knives} knives/gloves`);
  if (stattrak) chips.push(`${stattrak} StatTrak`);
  if (special) chips.push({ text: `${special} special`, cls: 'special' });

  elements.summaryHighlights.innerHTML = '';
  for (const chip of chips) {
    const el = document.createElement('span');
    el.className = typeof chip === 'string' ? 'highlight-chip' : `highlight-chip ${chip.cls}`;
    el.textContent = typeof chip === 'string' ? chip : chip.text;
    elements.summaryHighlights.appendChild(el);
  }
}

function updateProfileSummary(inventoryData) {
  const persona = inventoryData.persona_name;
  const avatar = inventoryData.avatar;
  const profileUrl = inventoryData.profile_url;

  // Show the persona name (and link) only when we actually have a name.
  if (persona) {
    elements.summaryPersona.textContent = persona;
    elements.summaryProfile.href = profileUrl || '#';
    elements.summaryProfile.style.display = 'inline-flex';
  } else {
    elements.summaryPersona.textContent = '';
    elements.summaryProfile.style.display = 'none';
  }

  // Show the avatar only when we have a valid URL; never render a broken image.
  if (avatar) {
    elements.summaryAvatar.src = avatar;
    elements.summaryAvatar.alt = persona ? `${persona}'s avatar` : 'Profile avatar';
    elements.summaryAvatar.style.display = 'inline-block';
  } else {
    elements.summaryAvatar.removeAttribute('src');
    elements.summaryAvatar.style.display = 'none';
  }

  // Warn if this account cannot trade. tradeBanState is "None"/"Probation"/"Banned";
  // a limited account is likewise blocked from trading and the market.
  if (elements.banAlert) {
    const tradeBan = inventoryData.trade_ban_state;
    const warnings = [];
    if (tradeBan && tradeBan !== 'None') {
      warnings.push(tradeBan === 'Probation'
        ? '⚠️ This user is on trade probation and cannot trade.'
        : '⚠️ This user is trade banned and cannot trade or use the market.');
    }
    if (inventoryData.limited_account) {
      warnings.push('⚠️ This account is limited and cannot trade or use the market.');
    }
    if (warnings.length > 0) {
      elements.banAlert.textContent = warnings.join(' ');
      elements.banAlert.style.display = 'block';
    } else {
      elements.banAlert.textContent = '';
      elements.banAlert.style.display = 'none';
    }
  }
}

function sortItems(items, field, order) {
  return [...items].sort((a, b) => {
    let valueA, valueB;
    
    switch (field) {
      case 'name':
        if (a.nameSortKey === undefined) a.nameSortKey = nameSortKey(a.steamData);
        if (b.nameSortKey === undefined) b.nameSortKey = nameSortKey(b.steamData);
        valueA = a.nameSortKey;
        valueB = b.nameSortKey;
        break;
      case 'rarity':
        valueA = getRarityValue(a.steamData.rarity || '');
        valueB = getRarityValue(b.steamData.rarity || '');
        break;
      case 'float':
        valueA = getItemFloat(a);
        valueB = getItemFloat(b);
        // Items without a float sink to the end in both directions; they'd otherwise
        // lead the descending sort on a sentinel value.
        if (valueA === null || valueB === null) {
          if (valueA === valueB) return 0;
          return valueA === null ? 1 : -1;
        }
        break;
      case 'date':
      default:
        valueA = a.originalIndex;
        valueB = b.originalIndex;
        break;
    }
    
    if (valueA < valueB) return order === 'asc' ? -1 : 1;
    if (valueA > valueB) return order === 'asc' ? 1 : -1;
    return 0;
  });
}

// Rarity -> Steam color, shared by the item cards and the inventory summary bar.
const RARITY_COLORS = {
  // Standard weapon skin rarities (CS2/CS:GO)
  'Consumer Grade': '#B0C3D9',       // Light Gray/White
  'Industrial Grade': '#5E98D9',     // Light Blue
  'Mil-Spec Grade': '#4B69FF',       // Blue
  'Restricted': '#8847FF',           // Purple
  'Classified': '#D32CE6',           // Pink/Magenta
  'Covert': '#EB4B4B',               // Red (weapons)
  'Extraordinary': '#EB4B4B',        // Red (knives/gloves) - same tier as Covert; Steam tags it eb4b4b
  'Contraband': '#E4AE39',           // Gold/Orange (e.g. M4A4 Howl) - the only gold rarity

  // Agent rarities (based on Operation rewards)
  'Base Grade': '#B0C3D9',           // Light Gray/White
  'Distinguished': '#4B69FF',        // Blue (28 stars)
  'Exceptional': '#8847FF',          // Purple (52 stars)
  'Superior': '#D32CE6',             // Pink (76 stars)
  'Master': '#EB4B4B',               // Red (89 stars)

  // Stickers, charms, graffiti, etc. (same rarity-value colors as above)
  'High Grade': '#4B69FF',           // Blue (value 3, == Mil-Spec/Distinguished)
  'Remarkable': '#8847FF',           // Purple (value 4, == Restricted/Exceptional)
  'Exotic': '#D32CE6',               // Pink (value 5, == Classified/Superior)

  // Default weapon (no skin)
  'Stock': '#DED6CC'                 // Off-white/gray (Steam Rarity_Default_Weapon)
};

function rarityColorOf(rarity) {
  return RARITY_COLORS[rarity] || '#B0C3D9'; // Default to light gray if not found
}

// Rarity -> sort tier. Tiers follow the Steam rarity colors (see RARITY_COLORS), so a
// "High Grade" music kit sorts with the blue Mil-Spec weapons, not the light-blue
// Industrial ones. Knives/gloves (Extraordinary) stay above Contraband on top.
function getRarityValue(rarity) {
  const rarityOrder = {
    'Consumer Grade': 1,   // gray
    'Base Grade': 1,
    'Industrial Grade': 2, // light blue
    'Mil-Spec Grade': 3,   // blue
    'High Grade': 3,
    'Distinguished': 3,
    'Restricted': 4,       // purple
    'Remarkable': 4,
    'Exceptional': 4,
    'Classified': 5,       // pink
    'Exotic': 5,
    'Superior': 5,
    'Covert': 6,           // red
    'Master': 6,
    'Contraband': 7,       // gold
    'Extraordinary': 8     // knives/gloves
  };
  return rarityOrder[rarity] || 0;
}

function filterItems(items) {
  return items.filter(item => {
    // Hide commemorative items filter (paintindex 0)
    if (currentFilters.hideCommemorative &&
        item.detailedData &&
        item.detailedData.paintindex === 0) {
      return false;
    }

    // Text search on the item name (normalized, order-independent tokens)
    if (!matchesSearch(item, currentFilters.search)) {
      return false;
    }

    // Attribute chips (independent AND filters - a StatTrak knife matches ★ and ST).
    // ★ and StatTrak are recognizable from the Steam name (★ prefix / StatTrak™), so
    // they work before the GC analysis resolves; special patterns (fade %, fire & ice,
    // blue gem, etc.) come only from the GC data, so unanalyzed items stay hidden until
    // their lookup resolves.
    if (currentFilters.star && !isStarItem(item.steamData)) {
      return false;
    }
    if (currentFilters.stattrak &&
        !(item.steamData.name || '').includes('StatTrak™') &&
        !(item.detailedData && item.detailedData.stattrak)) {
      return false;
    }
    if (currentFilters.souvenir && item.steamData.quality !== 'Souvenir') {
      return false;
    }
    if (currentFilters.special && !(item.detailedData && item.detailedData.special)) {
      return false;
    }

    // Item type filter (Steam's "Type" tag: Rifle, Knife, Sticker, Agent, ...)
    if (currentFilters.type && (item.steamData.item_type || 'Other') !== currentFilters.type) {
      return false;
    }

    // Rarity filter
    if (currentFilters.rarity && item.steamData.rarity !== currentFilters.rarity) {
      return false;
    }
    
    // Wear filter
    if (currentFilters.wear && item.steamData.wear !== currentFilters.wear) {
      return false;
    }
    
    // Float range filter. Items without a float (unanalyzed, paint-less) pass through.
    if (currentFilters.floatMin !== null || currentFilters.floatMax !== null) {
      const itemFloat = getItemFloat(item);
      if (itemFloat !== null) {
        if (currentFilters.floatMin !== null && itemFloat < currentFilters.floatMin) {
          return false;
        }
        if (currentFilters.floatMax !== null && itemFloat > currentFilters.floatMax) {
          return false;
        }
      }
    }
    
    return true;
  });
}

// Steam "Type" tag values in display order: weapons first, then ★ items, then
// collectibles. Types Steam adds later sort after these, alphabetically.
const TYPE_ORDER = [
  'Pistol', 'SMG', 'Rifle', 'Sniper Rifle', 'Shotgun', 'Machinegun', 'Equipment',
  'Knife', 'Gloves', 'Agent', 'Sticker', 'Charm', 'Patch', 'Music Kit', 'Graffiti',
  'Container', 'Key', 'Pass', 'Gift', 'Collectible', 'Tool'
];

// Fill the type dropdown with the types present in this inventory, so it never offers
// an option that matches nothing. Hidden entirely when there is only one type.
function populateTypeFilter() {
  const types = new Set(inventoryItems.map(it => it.steamData.item_type || 'Other'));
  const rank = t => { const i = TYPE_ORDER.indexOf(t); return i === -1 ? TYPE_ORDER.length : i; };
  const sorted = [...types].sort((a, b) => rank(a) - rank(b) || a.localeCompare(b));
  elements.filterType.length = 1; // keep "All Types"
  for (const t of sorted) elements.filterType.add(new Option(t, t));
  elements.filterTypeGroup.style.display = sorted.length > 1 ? '' : 'none';
}

function displayItems(items) {
  const inventoryGrid = elements.inventoryGrid;

  // Reconcile the grid against the existing card elements instead of rebuilding it:
  // drop cards that no longer match, then walk the grid with a cursor, moving each
  // card into place only if it isn't already there. Reusing nodes keeps their shadow
  // DOM (no re-clone, no image flash) and makes re-sorting ~free for unmoved cards.
  // Everything stays synchronous, which view transitions require: the DOM must be in
  // its final state inside the transition callback, before the "new" snapshot.
  const wanted = new Set(items.map(itemData => itemData.originalIndex));
  for (const el of [...inventoryGrid.children]) {
    if (!wanted.has(el.itemIndex)) {
      el.remove();
    }
  }
  let cursor = inventoryGrid.firstElementChild;
  for (const itemData of items) {
    let el = itemElements.get(itemData.originalIndex);
    const isNew = !el;
    if (isNew) {
      // Normally all cards exist from the initial render; this is just a safety net.
      el = createItemElement(itemData.steamData, itemData.originalIndex);
      itemElements.set(itemData.originalIndex, el);
    }
    if (el === cursor) {
      cursor = cursor.nextElementSibling;
    } else {
      inventoryGrid.insertBefore(el, cursor);
    }
    if (isNew && itemData.detailedData) {
      updateItemWithDetails(itemData.detailedData, itemData.originalIndex, itemData.steamData.inspect_link);
    }
  }

  // The grid speaks for itself; only surface a message when filters hide everything.
  if (elements.gridStatus) {
    elements.gridStatus.textContent =
      (inventoryItems.length > 0 && items.length === 0) ? 'No items match your filters.' : '';
  }
}

// Give cards near the viewport stable view-transition names (keyed by their element
// id, which is the item's original index) so the browser can pair them across a
// re-render and glide them to their new grid positions. Capped so the per-element
// snapshot cost stays low on large inventories; unnamed cards simply appear in place,
// which is invisible offscreen anyway.
function nameCardsForTransition() {
  const margin = 300;
  // Read every position first, then write every name: interleaving the rect reads
  // with style writes would invalidate layout between cards and recompute it per read.
  const children = [...elements.inventoryGrid.children];
  const names = [];
  let named = 0;
  for (const el of children) {
    let name = 'none';
    if (named < 60) {
      const r = el.getBoundingClientRect();
      if (r.bottom > -margin && r.top < window.innerHeight + margin) {
        name = `vt-${el.id}`;
        named++;
      }
    }
    names.push(name);
  }
  children.forEach((el, i) => { el.style.viewTransitionName = names[i]; });
}

function applySortAndFilter(animate = true) {
  filteredItems = filterItems(inventoryItems);
  filteredItems = sortItems(filteredItems, currentSort.field, currentSort.order);

  // Animated reorder: surviving cards glide to their new spots, removed ones fade out.
  // Falls back to an instant re-render without browser support, for reduced-motion
  // users, and for callers that re-render on every keystroke (search).
  if (animate && document.startViewTransition &&
      !window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    nameCardsForTransition();
    document.startViewTransition(() => {
      displayItems(filteredItems);
      nameCardsForTransition();
    });
  } else {
    displayItems(filteredItems);
  }

  // If analysis is in progress, reorder the remaining items in the queue
  if (itemsNeedingAnalysis.length > 0 && analyzedCount < itemsNeedingAnalysis.length) {
    reorderAnalysisQueue();
  }
}

// Reorder the analysis queue based on the current sort order
// This ensures we analyze items in the order they appear on screen
function reorderAnalysisQueue() {
  if (analyzedCount >= itemsNeedingAnalysis.length) {
    return; // All items analyzed
  }

  // Get the unanalyzed items
  const unanalyzed = itemsNeedingAnalysis.slice(analyzedCount);

  // Map each item's originalIndex to its position in the current sorted view
  const positionByIndex = new Map(filteredItems.map((item, pos) => [item.originalIndex, pos]));

  // Separate items into visible (in current filtered view) and not visible
  const visibleItems = [];
  const notVisibleItems = [];

  for (const item of unanalyzed) {
    const positionInSort = positionByIndex.get(item.index);
    if (positionInSort !== undefined) {
      visibleItems.push({ item, position: positionInSort });
    } else {
      notVisibleItems.push(item);
    }
  }

  // Sort visible items by their position in the current sort
  visibleItems.sort((a, b) => a.position - b.position);

  // Extract just the items (without position metadata)
  const sortedVisibleItems = visibleItems.map(x => x.item);

  // Replace the unanalyzed portion with reordered items (visible first, then not visible)
  itemsNeedingAnalysis.splice(analyzedCount, unanalyzed.length, ...sortedVisibleItems, ...notVisibleItems);
}

async function analyzeInventory(userInput, resolvedSteamId = null) {
  try {
    // Reset cancellation state and create new AbortController
    isCancelled = false;
    analysisController = new AbortController();
    
    // Show cancel button, hide analyze button
    elements.button.style.display = 'none';
    elements.cancelButton.style.display = 'inline-block';
    
    elements.errorDisplay.style.display = 'none';
    elements.status.textContent = '';
    elements.inventoryStatus.style.display = 'block';
    elements.inventoryContainer.style.display = 'none';
    elements.inventorySummary.style.display = 'none';

    elements.loadingMessage.textContent = 'Fetching inventory data...';
    updateProgress(0, 0);

    // Fetch profile info (avatar, persona, trade-ban) in parallel and populate the header when
    // it arrives. This is intentionally not awaited so item rendering never waits on Steam's
    // profile feed; the summary block is hidden until items load, so early/late arrival is fine.
    fetch(`/api/profile?steamid=${encodeURIComponent(userInput)}`, { signal: analysisController.signal })
      .then(r => r.json())
      .then(profile => { if (profile && profile.success) updateProfileSummary(profile); })
      .catch(() => { /* profile is non-critical; ignore failures and aborts */ });

    const response = await fetch(`/api/inventory?steamid=${encodeURIComponent(userInput)}`, {
      signal: analysisController.signal
    });
    const inventoryData = await response.json();
    
    if (inventoryData.error) {
      throw new Error(inventoryData.error);
    }

    // Check if cancelled after fetching inventory
    if (isCancelled) {
      throw new Error('Analysis was cancelled');
    }

    // Clear the input field after successful server response
    elements.textbox.value = '';

    const csgoItems = inventoryData.csgo_items || [];

    if (csgoItems.length === 0) {
      throw new Error('No CS2 items found in inventory or inventory is private');
    }

    // Determine the canonical SteamId64 for the share hash.
    // Prefer the resolved id from the response (the backend now returns it),
    // then any id we extracted from the input, then fall back to the legacy
    // inspect-link regex (kept only as a last resort).
    let actualSteamId = null;
    if (inventoryData.steamid && validateSteamId(inventoryData.steamid)) {
      actualSteamId = inventoryData.steamid;
    } else if (resolvedSteamId && validateSteamId(resolvedSteamId)) {
      actualSteamId = resolvedSteamId;
    } else if (csgoItems.length > 0 && csgoItems[0].inspect_link) {
      const inspectMatch = csgoItems[0].inspect_link.match(/S(\d+)A/);
      if (inspectMatch && validateSteamId(inspectMatch[1])) {
        actualSteamId = inspectMatch[1];
      }
    }

    // Remember the owner id before rendering items: the cards use it to build their
    // "view in Steam inventory" links.
    currentOwnerSteamId = actualSteamId;

    // Update the URL hash with the resolved SteamId64 if we have it and it's different from the input
    if (actualSteamId && validateSteamId(actualSteamId)) {
      // Only update hash if it's different from the current hash (to replace the URL-encoded input)
      const currentHash = decodeURIComponent(window.location.hash.substring(1));
      if (currentHash !== actualSteamId) {
        window.location.hash = actualSteamId;
      }
    }

    // Show the inventory container and render all items immediately
    elements.inventoryContainer.style.display = 'block';
    elements.inventorySummary.style.display = 'block';
    
    const inventoryGrid = elements.inventoryGrid;
    inventoryGrid.innerHTML = '';
    itemElements.clear();

    // Create and render all items immediately with basic Steam inventory data
    elements.loadingMessage.textContent = `Found ${csgoItems.length} CS2 items - rendering now...`;
    const processedItems = [];
    let preloadedCount = 0;
    itemsNeedingAnalysis = []; // Reset the global analysis queue
    analyzedCount = 0; // Reset the counter

    // Initialize inventory data structure
    inventoryItems = [];

    for (let i = 0; i < csgoItems.length; i++) {
      const item = csgoItems[i];
      const itemElement = createItemElement(item, i);
      itemElements.set(i, itemElement);
      inventoryGrid.appendChild(itemElement);

      // Store item data in our structure
      const itemData = {
        originalIndex: i,
        steamData: item,
        detailedData: null
      };

      // Check if we have existing data for this item
      if (item.existing_data) {
        // Item already exists in database - update it immediately
        itemData.detailedData = item.existing_data;
        processedItems[i] = item.existing_data;
        updateItemWithDetails(item.existing_data, i, item.inspect_link);
        preloadedCount++;
      } else {
        // Item needs analysis - add to queue
        processedItems[i] = null;
        itemsNeedingAnalysis.push({ item, index: i });
      }

      inventoryItems.push(itemData);
    }
    
    console.log(`Resolved ${preloadedCount} items from the inventory response, ${itemsNeedingAnalysis.length} need a Game Coordinator lookup`);

    // Update summary with initial data including pre-loaded items
    updateSummary(inventoryData, processedItems);

    // Show sidebar controls immediately after rendering items
    // This allows users to interact with sort/filter while analysis continues
    populateTypeFilter();
    elements.sidebar.style.display = 'block';

    // Initialize filtered items with all items and apply initial sort/filter
    filteredItems = [...inventoryItems];
    applySortAndFilter(false);

    if (itemsNeedingAnalysis.length === 0) {
      // Every item came fully resolved from the inventory response - nothing left to do.
      elements.inventoryStatus.style.display = 'none';
      return;
    }

    // Reorder the analysis queue based on the current sort order
    // This ensures we analyze items in the order they appear on screen
    reorderAnalysisQueue();

    // Now start the detailed analysis phase for remaining items
    elements.loadingMessage.textContent = `Getting precise float values for ${itemsNeedingAnalysis.length} new items...`;
    updateProgress(preloadedCount, csgoItems.length);
    
    // Process remaining items asynchronously for detailed analysis
    for (let i = 0; i < itemsNeedingAnalysis.length; i++) {
      // Check for cancellation before processing each item
      if (isCancelled) {
        throw new Error('Analysis was cancelled');
      }

      // Mark this item as being processed BEFORE fetching
      // This prevents reorderAnalysisQueue from moving it while we're fetching
      analyzedCount = i + 1;

      const { item, index } = itemsNeedingAnalysis[i];

      try {
        const itemResponse = await fetch(`/api?${new URLSearchParams({url: item.inspect_link})}`, {
          signal: analysisController.signal
        });
        const itemData = await itemResponse.json();

        processedItems[index] = itemData.error ? null : itemData;
        updateItemWithDetails(itemData, index, item.inspect_link);

        // Update our item data structure
        if (inventoryItems[index] && !itemData.error) {
          inventoryItems[index].detailedData = itemData;
        }

      } catch (error) {
        console.error(`Error loading item ${index}:`, error);
        processedItems[index] = null;
        updateItemWithDetails({ error: 'Failed to load item details' }, index, item.inspect_link);
      }

      updateProgress(preloadedCount + i + 1, csgoItems.length);
      // The summary rescans the whole inventory and rebuilds its DOM; every 10th item
      // (plus the final one) keeps the highlight chips feeling live without doing that
      // work ten times a second on large inventories.
      if ((i + 1) % 10 === 0 || i === itemsNeedingAnalysis.length - 1) {
        updateSummary(inventoryData, processedItems);
      }

      // Small delay to prevent overwhelming the API
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    
    elements.inventoryStatus.style.display = 'none';
    const totalItems = csgoItems.length;
    const analyzedItems = itemsNeedingAnalysis.length;
    elements.status.textContent = `Loaded ${totalItems} items (${analyzedItems} via Game Coordinator)`;

    // Re-apply sort and filter now that all items have detailed data
    applySortAndFilter(false);
    
  } catch (error) {
    console.error('Error analyzing inventory:', error);
    elements.inventoryStatus.style.display = 'none';
    
    // Handle different types of errors
    if (error.name === 'AbortError' || error.message === 'Analysis was cancelled') {
      elements.errorDisplay.textContent = 'Analysis was cancelled';
      elements.status.textContent = 'Analysis cancelled by user';
    } else {
      // textContent, not innerHTML: the message can echo server-provided strings
      elements.errorDisplay.textContent = error.message;
    }
    elements.errorDisplay.style.display = 'block';
  } finally {
    // Always restore button states
    elements.button.style.display = 'inline-block';
    elements.cancelButton.style.display = 'none';
    analysisController = null;
  }
}

// Dim the parts of the float slider's wear gradient outside the selected range, the
// same way the per-card float bars dim the wear values a skin can't roll. Widths are
// percentages, so the visual needs no layout reads and survives resizes untouched.
function updateSliderVisual() {
  const minVal = parseFloat(elements.floatSliderMin.value);
  const maxVal = parseFloat(elements.floatSliderMax.value);
  elements.floatSliderDimLeft.style.width = `${minVal * 100}%`;
  elements.floatSliderDimRight.style.width = `${(1 - maxVal) * 100}%`;
}

// Reset every filter control to its default state, matching defaultFilters(). Used by
// the Clear filters button and when a new analysis starts (resetInterface), so the
// controls never show stale selections that no longer apply.
function resetFilterControls() {
  elements.searchInput.value = '';
  elements.filterType.value = '';
  elements.filterRarity.value = '';
  elements.filterWear.value = '';
  elements.filterFloatMin.value = '';
  elements.filterFloatMax.value = '';
  elements.floatSliderMin.value = 0;
  elements.floatSliderMax.value = 1;
  elements.hideCommemorative.checked = true;
  elements.attrChips.forEach(chip => chip.setAttribute('aria-pressed', 'false'));
  updateSliderVisual();
}

function cancelAnalysis() {
  if (analysisController && !isCancelled) {
    isCancelled = true;
    analysisController.abort();
    console.log('Analysis cancelled by user');
  }
}

function resetInterface() {
  elements.errorDisplay.style.display = 'none';
  elements.inventoryStatus.style.display = 'none';
  elements.inventoryContainer.style.display = 'none';
  elements.inventorySummary.style.display = 'none';
  elements.sidebar.style.display = 'none';
  elements.inventoryGrid.innerHTML = '';
  itemElements.clear();
  elements.status.textContent = '';

  // Reset button states
  elements.button.style.display = 'inline-block';
  elements.cancelButton.style.display = 'none';

  // Reset data
  currentOwnerSteamId = null;
  inventoryItems = [];
  filteredItems = [];
  itemsNeedingAnalysis = [];
  analyzedCount = 0;
  currentSort = { field: 'rarity', order: 'desc' };
  currentFilters = defaultFilters();

  // Sync the controls so they don't show selections that no longer apply
  elements.sortSelect.value = currentSort.field;
  elements.sortOrder.value = currentSort.order;
  resetFilterControls();

  // Cancel any ongoing analysis
  if (analysisController) {
    cancelAnalysis();
  }

  // Note: We don't clear the hash here to preserve shareable links
}

function validateSteamId(steamId) {
  const steamId64Regex = /^7656119\d{10}$/;
  return steamId64Regex.test(steamId);
}

function extractSteamIdFromInput(input) {
  // Check if it's already a valid SteamId64
  if (validateSteamId(input)) {
    return input;
  }

  // Try to extract from Steam profile URL
  const profileMatch = input.match(/steamcommunity\.com\/profiles\/(\d+)/);
  if (profileMatch && validateSteamId(profileMatch[1])) {
    return profileMatch[1];
  }

  // Return null for custom URLs or invalid input - let the server handle it
  return null;
}

window.addEventListener("load", function () {
  elements = {
    textbox: document.getElementById("textbox"),
    button: document.getElementById("button"),
    cancelButton: document.getElementById("cancel-button"),
    errorDisplay: document.getElementById("error-display"),
    inventoryStatus: document.getElementById("inventory-status"),
    loadingMessage: document.getElementById("loading-message"),
    progressFill: document.getElementById("progress-fill"),
    progressText: document.getElementById("progress-text"),
    inventorySummary: document.getElementById("inventory-summary"),
    summaryProfile: document.getElementById("summary-profile"),
    summaryAvatar: document.getElementById("summary-avatar"),
    summaryPersona: document.getElementById("summary-persona"),
    banAlert: document.getElementById("ban-alert"),
    rarityBar: document.getElementById("rarity-bar"),
    rarityLegend: document.getElementById("rarity-legend"),
    summaryHighlights: document.getElementById("summary-highlights"),
    inventoryContainer: document.getElementById("inventory-container"),
    sidebar: document.getElementById("sidebar"),
    filterToggle: document.getElementById("filter-toggle"),
    sidebarSections: document.getElementById("sidebar-sections"),
    inventoryGrid: document.getElementById("inventory-grid"),
    status: document.getElementById("status"),
    gridStatus: document.getElementById("grid-status"),

    // Control elements
    searchInput: document.getElementById("search-input"),
    sortSelect: document.getElementById("sort-select"),
    sortOrder: document.getElementById("sort-order"),
    filterType: document.getElementById("filter-type"),
    filterTypeGroup: document.getElementById("filter-type-group"),
    attrChips: Array.from(document.querySelectorAll(".attr-chip")),
    filterRarity: document.getElementById("filter-rarity"),
    filterWear: document.getElementById("filter-wear"),
    filterFloatMin: document.getElementById("filter-float-min"),
    filterFloatMax: document.getElementById("filter-float-max"),
    hideCommemorative: document.getElementById("hide-commemorative"),
    clearFilters: document.getElementById("clear-filters"),
    // Float slider elements
    floatSliderMin: document.getElementById("float-slider-min"),
    floatSliderMax: document.getElementById("float-slider-max"),
    floatSliderDimLeft: document.getElementById("float-slider-dim-left"),
    floatSliderDimRight: document.getElementById("float-slider-dim-right")
  };

  elements.textbox.addEventListener("keydown", function (event) {
    if (event.code === "Enter") {
      event.preventDefault();
      elements.button.click();
    }
  });

  elements.button.addEventListener("click", function (element) {
    element.target.blur();

    const userInput = elements.textbox.value.trim();

    if (!userInput) {
      elements.errorDisplay.textContent = 'Please enter a Steam profile URL';
      elements.errorDisplay.style.display = 'block';
      return;
    }

    // Extract SteamId64 if possible, otherwise let server handle resolution
    const extractedSteamId = extractSteamIdFromInput(userInput);

    // Immediately set the hash to the user's input (URL encoded)
    window.location.hash = encodeURIComponent(userInput);

    resetInterface();
    analyzeInventory(userInput, extractedSteamId);
  });

  elements.cancelButton.addEventListener("click", function (element) {
    element.target.blur();
    cancelAnalysis();
  });

  // Sorting and filtering controls
  elements.sortSelect.addEventListener("change", function() {
    currentSort.field = this.value;
    
    // Set default order based on field type
    switch (this.value) {
      case 'float':
      case 'date':
      case 'name':
        currentSort.order = 'asc';
        break;
      case 'rarity':
        currentSort.order = 'desc';
        break;
    }
    
    // Update the order dropdown to reflect the change
    elements.sortOrder.value = currentSort.order;
    
    applySortAndFilter();
  });

  elements.sortOrder.addEventListener("change", function() {
    currentSort.order = this.value;
    applySortAndFilter();
  });

  // Auto-apply filters when rarity changes
  elements.filterRarity.addEventListener("change", function() {
    currentFilters.rarity = this.value;
    applySortAndFilter();
  });

  // Wear ranges for each condition
  const wearRanges = {
    'Factory New': { min: 0.00, max: 0.07 },
    'Minimal Wear': { min: 0.07, max: 0.15 },
    'Field-Tested': { min: 0.15, max: 0.38 },
    'Well-Worn': { min: 0.38, max: 0.45 },
    'Battle-Scarred': { min: 0.45, max: 1.00 }
  };

  // Auto-apply filters when wear changes and update float controls
  elements.filterWear.addEventListener("change", function() {
    currentFilters.wear = this.value;

    // Update float range based on wear selection
    if (this.value && wearRanges[this.value]) {
      const range = wearRanges[this.value];
      elements.filterFloatMin.value = range.min.toFixed(3);
      elements.filterFloatMax.value = range.max.toFixed(3);
      elements.floatSliderMin.value = range.min;
      elements.floatSliderMax.value = range.max;
      currentFilters.floatMin = range.min;
      currentFilters.floatMax = range.max;
      updateSliderVisual();
    } else if (!this.value) {
      // Clear float filters when "All Wear Levels" is selected
      elements.filterFloatMin.value = '';
      elements.filterFloatMax.value = '';
      elements.floatSliderMin.value = 0;
      elements.floatSliderMax.value = 1;
      currentFilters.floatMin = null;
      currentFilters.floatMax = null;
      updateSliderVisual();
    }

    applySortAndFilter();
  });

  // Check if current float range matches a wear level and update dropdown
  function syncWearFromFloat() {
    const minVal = currentFilters.floatMin;
    const maxVal = currentFilters.floatMax;

    // If both are null, clear wear filter
    if (minVal === null && maxVal === null) {
      elements.filterWear.value = '';
      currentFilters.wear = '';
      return;
    }

    // Check if current range matches a wear level (with small tolerance for floating point)
    const tolerance = 0.001;
    for (const [wearName, range] of Object.entries(wearRanges)) {
      if (minVal !== null && maxVal !== null &&
          Math.abs(minVal - range.min) < tolerance &&
          Math.abs(maxVal - range.max) < tolerance) {
        elements.filterWear.value = wearName;
        currentFilters.wear = wearName;
        return;
      }
    }

    // No exact match - clear wear filter
    elements.filterWear.value = '';
    currentFilters.wear = '';
  }

  // Update input boxes and visual immediately on slider drag
  elements.floatSliderMin.addEventListener("input", function() {
    let minVal = parseFloat(this.value);
    let maxVal = parseFloat(elements.floatSliderMax.value);

    // Prevent min from exceeding max
    if (minVal > maxVal) {
      minVal = maxVal;
      this.value = minVal;
    }

    elements.filterFloatMin.value = minVal.toFixed(3);
    updateSliderVisual();
  });

  elements.floatSliderMax.addEventListener("input", function() {
    let maxVal = parseFloat(this.value);
    let minVal = parseFloat(elements.floatSliderMin.value);

    // Prevent max from going below min
    if (maxVal < minVal) {
      maxVal = minVal;
      this.value = maxVal;
    }

    elements.filterFloatMax.value = maxVal.toFixed(3);
    updateSliderVisual();
  });

  // Apply filter only when slider is released
  elements.floatSliderMin.addEventListener("change", function() {
    const minVal = parseFloat(this.value);
    currentFilters.floatMin = minVal;
    syncWearFromFloat();
    applySortAndFilter();
  });

  elements.floatSliderMax.addEventListener("change", function() {
    const maxVal = parseFloat(this.value);
    currentFilters.floatMax = maxVal;
    syncWearFromFloat();
    applySortAndFilter();
  });

  // Update sliders when typing in input boxes
  elements.filterFloatMin.addEventListener("input", function() {
    const value = this.value ? parseFloat(this.value) : 0;
    elements.floatSliderMin.value = value;
    updateSliderVisual();
    currentFilters.floatMin = this.value ? parseFloat(this.value) : null;
    syncWearFromFloat();
    applySortAndFilter(false); // fires per typed character
  });

  elements.filterFloatMax.addEventListener("input", function() {
    const value = this.value ? parseFloat(this.value) : 1;
    elements.floatSliderMax.value = value;
    updateSliderVisual();
    currentFilters.floatMax = this.value ? parseFloat(this.value) : null;
    syncWearFromFloat();
    applySortAndFilter(false); // fires per typed character
  });

  // Auto-apply filter when checkbox is toggled
  elements.hideCommemorative.addEventListener("change", function() {
    currentFilters.hideCommemorative = this.checked;
    applySortAndFilter();
  });

  // Initialize slider visual
  updateSliderVisual();

  // Text search filter (no view transition: this re-renders on every keystroke)
  elements.searchInput.addEventListener("input", function() {
    currentFilters.search = normalizeSearchText(this.value);
    applySortAndFilter(false);
  });

  // Item type filter (options are populated per inventory by populateTypeFilter)
  elements.filterType.addEventListener("change", function() {
    currentFilters.type = this.value;
    applySortAndFilter();
  });

  // Attribute chips (★ / ST / Souvenir / Special) toggle independent boolean filters.
  // StatTrak and Souvenir can't coexist on one item, so pressing one releases the
  // other instead of leaving an empty intersection selected.
  const exclusiveWith = { stattrak: 'souvenir', souvenir: 'stattrak' };
  elements.attrChips.forEach(chip => {
    chip.addEventListener("click", function() {
      const pressed = this.getAttribute("aria-pressed") !== "true";
      this.setAttribute("aria-pressed", String(pressed));
      currentFilters[this.dataset.attr] = pressed;
      const other = pressed && exclusiveWith[this.dataset.attr];
      if (other && currentFilters[other]) {
        currentFilters[other] = false;
        elements.attrChips.find(c => c.dataset.attr === other)
          .setAttribute("aria-pressed", "false");
      }
      applySortAndFilter();
    });
  });

  // Clear all filters (keeps the current sort) and reset the filter controls
  elements.clearFilters.addEventListener("click", function() {
    currentFilters = defaultFilters();
    resetFilterControls();
    applySortAndFilter();
  });

  // Filters toggle (mobile) - expands the collapsible filter panel under the sticky bar
  elements.filterToggle.addEventListener("click", function() {
    const expanded = elements.sidebarSections.classList.toggle("expanded");
    this.setAttribute("aria-expanded", expanded ? "true" : "false");
  });

  if (window.location.hash) {
    const hashValue = decodeURIComponent(window.location.hash.substring(1));
    const extractedId = extractSteamIdFromInput(hashValue);

    if (extractedId) {
      // If we can extract a SteamId64, use it directly
      elements.textbox.value = extractedId;
      elements.button.click();
    } else if (hashValue && (hashValue.includes('steamcommunity.com') || !hashValue.match(/^\d+$/))) {
      // If it looks like a URL or custom ID, try it
      elements.textbox.value = hashValue;
      elements.button.click();
    }
  }
});