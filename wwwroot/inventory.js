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
    this.render();
    // If data was set before connection, update display now
    if (this.needsUpdate) {
      this.updateDisplay();
      this.needsUpdate = false;
    }
  }

  render() {
    const template = document.getElementById('inventory-item-template');
    const clone = template.content.cloneNode(true);
    
    // Apply existing styles by importing the main stylesheet
    const style = document.createElement('style');
    style.textContent = `
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
      
      .item-actions {
        margin: 0;
      }
      
      .item-header {
        margin-bottom: 10px;
      }
      
      .item-name {
        font-weight: bold;
        font-size: 16px;
        color: var(--text, #ecf0f1);
        margin: 0 0 5px 0;
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
      
      .item-details {
        display: grid;
        grid-template-columns: 2fr 3fr;
        gap: 8px;
        font-size: 14px;
      }
      
      .detail-row {
        display: flex;
        justify-content: space-between;
        align-items: center;
      }
      
      .detail-label {
        color: var(--text, #ecf0f1);
        opacity: 0.8;
      }
      
      .detail-value {
        color: var(--text, #ecf0f1);
        font-weight: 500;
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
        margin-top: 15px;
        display: flex;
        justify-content: center;
      }
      
      .inspect-link {
        display: inline-block;
        padding: 4px 8px;
        background-color: var(--pop, #2ecc71);
        color: var(--gray, #1f2d3a);
        border-radius: 3px;
        font-size: 10px;
        font-weight: bold;
        text-decoration: none;
        transition: all 0.3s ease;
        width: 64px;
        text-align: center;
        box-sizing: border-box;
      }
      
      .inspect-link:hover {
        background-color: #27ae60;
        transform: translateY(-1px);
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
        
        .inspect-link {
          width: 80px;
          font-size: 10px;
          padding: 4px 8px;
        }
        
        .item-details {
          grid-template-columns: 1fr;
          gap: 4px;
        }
      }
    `;
    
    this.shadowRoot.appendChild(style);
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
    const wearElement = this.shadowRoot.querySelector('[data-field="wear"]');
    const rarityElement = this.shadowRoot.querySelector('[data-field="rarity"]');
    const inspectElement = this.shadowRoot.querySelector('[data-field="inspect-link"]');
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

    if (wearElement) {
      const wear = this.itemData.wear || 'Unknown';
      wearElement.textContent = wear;
    }

    if (rarityElement) {
      const rarity = this.itemData.rarity || 'Unknown';
      rarityElement.textContent = rarity;
    }

    if (inspectElement) {
      inspectElement.href = this.itemData.inspect_link || '#';
    }

    // Set item image
    if (imageElement) {
      if (this.itemData.icon_url_large) {
        imageElement.src = `https://steamcommunity-a.akamaihd.net/economy/image/${this.itemData.icon_url_large}`;
      } else if (this.itemData.icon_url) {
        imageElement.src = `https://steamcommunity-a.akamaihd.net/economy/image/${this.itemData.icon_url}`;
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
    const rarityColors = {
      // Standard weapon skin rarities (CS2/CS:GO)
      'Consumer Grade': '#B0C3D9',       // Light Gray/White
      'Industrial Grade': '#5E98D9',     // Light Blue  
      'Mil-Spec Grade': '#4B69FF',       // Blue
      'Mil-Spec': '#4B69FF',             // Blue (alternative naming)
      'Restricted': '#8847FF',           // Purple
      'Classified': '#D32CE6',           // Pink/Magenta
      'Covert': '#EB4B4B',              // Red
      'Contraband': '#E4AE39',           // Orange
      'Extraordinary': '#FFD700',        // Gold (knives/gloves)
      
      // Agent rarities (based on Operation rewards)
      'Base Grade': '#B0C3D9',          // Light Gray/White
      'Distinguished': '#4B69FF',       // Blue (28 stars)
      'Exceptional': '#8847FF',         // Purple (52 stars)
      'Superior': '#D32CE6',            // Pink (76 stars)
      'Master': '#EB4B4B',              // Red (89 stars)
      
      // Stickers and Patches (High Grade to Extraordinary)
      'High Grade': '#5E98D9',          // Light Blue
      'Remarkable': '#4B69FF',          // Blue
      'Exotic': '#8847FF',              // Purple
      
      // Default for unrecognized types
      'Stock': '#B0C3D9'                // Light Gray/White
    };
    
    return rarityColors[rarity] || '#B0C3D9'; // Default to light gray if not found
  }

  updateWithDetails(itemData, inspectLink) {
    const floatElement = this.shadowRoot.querySelector('[data-field="float"]');
    const wearElement = this.shadowRoot.querySelector('[data-field="wear"]');
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
    
    // Enhance the name with detailed info if we got weapon/skin data
    if (itemData.weapon && itemData.skin && nameElement) {
      nameElement.className = 'item-name';
      let nameText = `${itemData.weapon} | ${itemData.skin}`;
      if (itemData.special) {
        nameText += ` <span class="item-special" style="color: var(--pop, #2ecc71); font-weight: bold; margin-left: 5px;">${itemData.special}</span>`;
      }
      if (itemData.stattrak) {
        nameText += '<span class="stattrak-badge" style="display: inline-block; background-color: var(--pop, #2ecc71); color: var(--gray, #1f2d3a); font-size: 10px; font-weight: bold; padding: 2px 6px; border-radius: 3px; margin-left: 8px; vertical-align: middle;">ST</span>';
      }
      if (itemData.quality === 3) {
        nameElement.classList.add('knife');
      }
      if (itemData.quality === 12) {
        nameElement.classList.add('souvenir');
      }
      nameElement.innerHTML = nameText;
    }

    // Update float value
    if (floatElement) {
      const paintwearFloat = uint32ToFloat32(itemData.paintwear);
      floatElement.textContent = paintwearFloat.toFixed(6);
      floatElement.classList.remove('loading-placeholder');
    }
    
    // Update wear if we got better data, otherwise keep existing
    if (wearElement) {
      const detailedWear = getWearFromFloat(uint32ToFloat32(itemData.paintwear));
      if (detailedWear !== 'Unknown' && detailedWear !== wearElement.textContent) {
        wearElement.textContent = detailedWear;
      }
    }
    
    // Update rarity if we got better data, otherwise keep existing  
    if (rarityElement) {
      const detailedRarity = getRarityFromNumber(itemData.rarity);
      if (detailedRarity !== 'Unknown' && detailedRarity !== rarityElement.textContent) {
        rarityElement.textContent = detailedRarity;
      }
    }
    
    // Update pattern seed
    if (patternElement) {
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

let elements;
let analysisController = null; // AbortController for canceling requests
let isCancelled = false;
const conversionBuffer = new ArrayBuffer(4);
const conversionView = new DataView(conversionBuffer);

// Inventory data and controls
let inventoryItems = []; // Store all items with their data
let filteredItems = []; // Store currently filtered/sorted items
let currentSort = { field: 'rarity', order: 'desc' };
let currentFilters = { rarity: '', quality: '', floatMin: null, floatMax: null, hideCommemorative: true };

function uint32ToFloat32(uint32Value) {
  conversionView.setUint32(0, uint32Value);
  return conversionView.getFloat32(0);
}

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
    "Mil-Spec",
    "Restricted",
    "Classified",
    "Covert",
    "Contraband",
  ];
  return rarities[rarityNumber] || "Unknown";
}

function createItemElement(item, index) {
  const itemElement = document.createElement('inventory-item');
  itemElement.id = `item-${index}`;
  itemElement.setItemData(item, index);
  return itemElement;
}

function updateItemWithDetails(itemData, index, inspectLink) {
  const itemElement = document.getElementById(`item-${index}`);
  if (itemElement && itemElement.updateWithDetails) {
    itemElement.updateWithDetails(itemData, inspectLink);
  }
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
  
  const totalItems = inventoryData.total || 0;
  const csgoItems = inventoryData.csgo_items ? inventoryData.csgo_items.length : 0;
  const stattrakItems = processedItems.filter(item => item && item.stattrak).length;
  
  elements.totalItems.textContent = totalItems;
  elements.csgoItems.textContent = csgoItems;
  elements.stattrakItems.textContent = stattrakItems;
}

function sortItems(items, field, order) {
  return [...items].sort((a, b) => {
    let valueA, valueB;
    
    switch (field) {
      case 'name':
        valueA = (a.steamData.name || '').toLowerCase();
        valueB = (b.steamData.name || '').toLowerCase();
        break;
      case 'rarity':
        valueA = getRarityValue(a.steamData.rarity || '');
        valueB = getRarityValue(b.steamData.rarity || '');
        break;
      case 'quality':
        valueA = getQualityValue(a.steamData.wear || '');
        valueB = getQualityValue(b.steamData.wear || '');
        break;
      case 'float':
        valueA = a.detailedData && a.detailedData.paintwear ? uint32ToFloat32(a.detailedData.paintwear) : 999;
        valueB = b.detailedData && b.detailedData.paintwear ? uint32ToFloat32(b.detailedData.paintwear) : 999;
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

function getRarityValue(rarity) {
  const rarityOrder = {
    'Consumer Grade': 1,
    'Base Grade': 1,
    'Industrial Grade': 2,
    'High Grade': 2,
    'Mil-Spec Grade': 3,
    'Mil-Spec': 3,
    'Remarkable': 3,
    'Distinguished': 4,
    'Restricted': 4,
    'Exceptional': 5,
    'Classified': 5,
    'Superior': 6,
    'Covert': 6,
    'Master': 7,
    'Contraband': 8,
    'Extraordinary': 9
  };
  return rarityOrder[rarity] || 0;
}

function getQualityValue(quality) {
  const qualityOrder = {
    'Factory New': 1,
    'Minimal Wear': 2,
    'Field-Tested': 3,
    'Well-Worn': 4,
    'Battle-Scarred': 5
  };
  return qualityOrder[quality] || 0;
}

function filterItems(items) {
  return items.filter(item => {
    // Hide commemorative items filter (paintindex 0)
    if (currentFilters.hideCommemorative && 
        item.detailedData && 
        item.detailedData.paintindex === 0) {
      return false;
    }
    
    // Rarity filter
    if (currentFilters.rarity && item.steamData.rarity !== currentFilters.rarity) {
      return false;
    }
    
    // Quality filter
    if (currentFilters.quality && item.steamData.wear !== currentFilters.quality) {
      return false;
    }
    
    // Float range filter
    if ((currentFilters.floatMin !== null || currentFilters.floatMax !== null) && 
        item.detailedData && item.detailedData.paintwear) {
      const itemFloat = uint32ToFloat32(item.detailedData.paintwear);
      if (currentFilters.floatMin !== null && itemFloat < currentFilters.floatMin) {
        return false;
      }
      if (currentFilters.floatMax !== null && itemFloat > currentFilters.floatMax) {
        return false;
      }
    }
    
    return true;
  });
}

function displayItems(items) {
  const inventoryGrid = elements.inventoryGrid;
  inventoryGrid.innerHTML = '';
  
  items.forEach((itemData, displayIndex) => {
    const itemElement = createItemElement(itemData.steamData, itemData.originalIndex);
    inventoryGrid.appendChild(itemElement);
    
    // If we have detailed data, update the element immediately
    if (itemData.detailedData) {
      updateItemWithDetails(itemData.detailedData, itemData.originalIndex, itemData.steamData.inspect_link);
    }
  });
  
  // Update filter count
  if (elements.filterCount) {
    elements.filterCount.textContent = `Showing ${items.length} of ${inventoryItems.length} items`;
  }
}

function applySortAndFilter() {
  filteredItems = filterItems(inventoryItems);
  filteredItems = sortItems(filteredItems, currentSort.field, currentSort.order);
  displayItems(filteredItems);
}

async function analyzeInventory(steamId) {
  try {
    // Reset cancellation state and create new AbortController
    isCancelled = false;
    analysisController = new AbortController();
    
    // Show cancel button, hide analyze button
    elements.button.style.display = 'none';
    elements.cancelButton.style.display = 'inline-block';
    
    elements.errorDisplay.style.display = 'none';
    elements.inventoryStatus.style.display = 'block';
    elements.inventoryContainer.style.display = 'none';
    elements.inventorySummary.style.display = 'none';
    
    elements.loadingMessage.textContent = 'Fetching inventory data...';
    updateProgress(0, 0);

    const response = await fetch(`/api/inventory?steamid=${encodeURIComponent(steamId)}`, {
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

    const csgoItems = inventoryData.csgo_items || [];
    
    if (csgoItems.length === 0) {
      throw new Error('No CS2 items found in inventory or inventory is private');
    }

    // Show the inventory container and render all items immediately
    elements.inventoryContainer.style.display = 'block';
    elements.inventorySummary.style.display = 'block';
    
    const inventoryGrid = elements.inventoryGrid;
    inventoryGrid.innerHTML = '';
    
    // Create and render all items immediately with basic Steam inventory data
    elements.loadingMessage.textContent = `Found ${csgoItems.length} CS2 items - rendering now...`;
    const processedItems = [];
    let preloadedCount = 0;
    const itemsNeedingAnalysis = [];
    
    // Initialize inventory data structure
    inventoryItems = [];
    
    for (let i = 0; i < csgoItems.length; i++) {
      const item = csgoItems[i];
      const itemElement = createItemElement(item, i);
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
    
    console.log(`Pre-loaded ${preloadedCount} items from database, ${itemsNeedingAnalysis.length} items need analysis`);
    
    // Update summary with initial data including pre-loaded items
    updateSummary(inventoryData, processedItems);
    
    if (itemsNeedingAnalysis.length === 0) {
      // All items were pre-loaded!
      elements.inventoryStatus.style.display = 'none';
      elements.status.textContent = `Successfully loaded ${csgoItems.length} items (all from database)`;
      
      // Show sidebar controls
      elements.sidebar.style.display = 'block';
      
      // Initialize filtered items with all items (default sort by date)
      filteredItems = [...inventoryItems];
      applySortAndFilter();
      
      return;
    }
    
    // Now start the detailed analysis phase for remaining items
    elements.loadingMessage.textContent = `Getting precise float values for ${itemsNeedingAnalysis.length} new items...`;
    updateProgress(preloadedCount, csgoItems.length);
    
    // Process remaining items asynchronously for detailed analysis
    for (let i = 0; i < itemsNeedingAnalysis.length; i++) {
      // Check for cancellation before processing each item
      if (isCancelled) {
        throw new Error('Analysis was cancelled');
      }

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
      updateSummary(inventoryData, processedItems);
      
      // Small delay to prevent overwhelming the API
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    
    elements.inventoryStatus.style.display = 'none';
    const totalItems = csgoItems.length;
    const analyzedItems = itemsNeedingAnalysis.length;
    elements.status.textContent = `Successfully loaded ${totalItems} items (${preloadedCount} from database, ${analyzedItems} analyzed)`;
    
    // Show sidebar controls
    elements.sidebar.style.display = 'block';
    
    // Initialize filtered items with all items (default sort by date)
    filteredItems = [...inventoryItems];
    applySortAndFilter();
    
  } catch (error) {
    console.error('Error analyzing inventory:', error);
    elements.inventoryStatus.style.display = 'none';
    
    // Handle different types of errors
    if (error.name === 'AbortError' || error.message === 'Analysis was cancelled') {
      elements.errorDisplay.innerHTML = 'Analysis was cancelled';
      elements.status.textContent = 'Analysis cancelled by user';
    } else {
      elements.errorDisplay.innerHTML = error.message;
    }
    elements.errorDisplay.style.display = 'block';
  } finally {
    // Always restore button states
    elements.button.style.display = 'inline-block';
    elements.cancelButton.style.display = 'none';
    analysisController = null;
  }
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
  elements.status.textContent = '';
  
  // Reset button states
  elements.button.style.display = 'inline-block';
  elements.cancelButton.style.display = 'none';
  
  // Reset data
  inventoryItems = [];
  filteredItems = [];
  currentSort = { field: 'rarity', order: 'desc' };
  currentFilters = { rarity: '', quality: '', floatMin: null, floatMax: null, hideCommemorative: true };
  
  // Cancel any ongoing analysis
  if (analysisController) {
    cancelAnalysis();
  }
}

function validateSteamId(steamId) {
  const steamId64Regex = /^7656119\d{10}$/;
  return steamId64Regex.test(steamId);
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
    totalItems: document.getElementById("total-items"),
    csgoItems: document.getElementById("csgo-items"),
    stattrakItems: document.getElementById("stattrak-items"),
    inventoryContainer: document.getElementById("inventory-container"),
    sidebar: document.getElementById("sidebar"),
    sidebarHeader: document.getElementById("sidebar-header"),
    sidebarArrow: document.getElementById("sidebar-arrow"),
    sidebarSections: document.getElementById("sidebar-sections"),
    inventoryGrid: document.getElementById("inventory-grid"),
    status: document.getElementById("status"),
    filterCount: document.getElementById("filter-count"),
    
    // Control elements
    sortSelect: document.getElementById("sort-select"),
    sortOrder: document.getElementById("sort-order"),
    filterRarity: document.getElementById("filter-rarity"),
    filterQuality: document.getElementById("filter-quality"),
    filterFloatMin: document.getElementById("filter-float-min"),
    filterFloatMax: document.getElementById("filter-float-max"),
    hideCommemorative: document.getElementById("hide-commemorative")
  };

  elements.textbox.addEventListener("keydown", function (event) {
    if (event.code === "Enter") {
      event.preventDefault();
      elements.button.click();
    }
  });

  elements.button.addEventListener("click", function (element) {
    element.target.blur();

    const steamId = elements.textbox.value.trim();
    
    if (!steamId) {
      elements.errorDisplay.innerHTML = 'Please enter a Steam user ID';
      elements.errorDisplay.style.display = 'block';
      return;
    }
    
    if (!validateSteamId(steamId)) {
      elements.errorDisplay.innerHTML = 'Invalid Steam ID format. Please enter a valid Steam 64-bit ID (e.g., 76561198261551396)';
      elements.errorDisplay.style.display = 'block';
      return;
    }

    resetInterface();
    analyzeInventory(steamId);
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
      case 'quality':
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

  // Auto-apply filters when quality changes
  elements.filterQuality.addEventListener("change", function() {
    currentFilters.quality = this.value;
    applySortAndFilter();
  });

  // Auto-apply filters when typing in float inputs
  elements.filterFloatMin.addEventListener("input", function() {
    currentFilters.floatMin = this.value ? parseFloat(this.value) : null;
    applySortAndFilter();
  });

  elements.filterFloatMax.addEventListener("input", function() {
    currentFilters.floatMax = this.value ? parseFloat(this.value) : null;
    applySortAndFilter();
  });

  // Auto-apply filter when checkbox is toggled
  elements.hideCommemorative.addEventListener("change", function() {
    currentFilters.hideCommemorative = this.checked;
    applySortAndFilter();
  });

  // Mobile sidebar toggle functionality
  elements.sidebarHeader.addEventListener("click", function() {
    const sections = elements.sidebarSections;
    const arrow = elements.sidebarArrow;
    
    // Toggle expanded class
    sections.classList.toggle("expanded");
    arrow.classList.toggle("rotated");
  });

  if (window.location.hash) {
    const hashSteamId = window.location.hash.substring(1);
    if (validateSteamId(hashSteamId)) {
      elements.textbox.value = hashSteamId;
      elements.button.click();
    }
  }
});