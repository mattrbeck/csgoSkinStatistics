let elements;
let analysisController = null; // AbortController for canceling requests
let isCancelled = false;
const conversionBuffer = new ArrayBuffer(4);
const conversionView = new DataView(conversionBuffer);

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
  const itemDiv = document.createElement('div');
  itemDiv.className = 'inventory-item loading';
  itemDiv.id = `item-${index}`;
  
  // Determine if this is a knife or souvenir from the item info
  let nameClasses = '';
  if (item.quality === 'Souvenir') {
    nameClasses += ' souvenir';
  }
  if (item.type && item.type.includes('★')) {
    nameClasses += ' knife';
  }
  
  itemDiv.innerHTML = `
    <div class="item-header">
      <div class="item-name${nameClasses}" id="name-${index}">
        ${item.name || item.market_name || 'Unknown Item'}
      </div>
    </div>
    <div class="item-details">
      <div class="detail-row">
        <span class="detail-label">Float:</span>
        <span class="detail-value loading-placeholder" id="float-${index}">Analyzing...</span>
      </div>
      <div class="detail-row">
        <span class="detail-label">Wear:</span>
        <span class="detail-value" id="wear-${index}">${item.wear || 'Unknown'}</span>
      </div>
      <div class="detail-row">
        <span class="detail-label">Rarity:</span>
        <span class="detail-value" id="rarity-${index}">${item.rarity || 'Unknown'}</span>
      </div>
      <div class="detail-row">
        <span class="detail-label">Pattern:</span>
        <span class="detail-value loading-placeholder" id="pattern-${index}">Analyzing...</span>
      </div>
    </div>
    <div class="item-actions">
      <a href="${item.inspect_link}" class="inspect-link" id="inspect-${index}">Inspect In Game</a>
    </div>
  `;
  
  return itemDiv;
}

function updateItemWithDetails(itemData, index, inspectLink) {
  const itemDiv = document.getElementById(`item-${index}`);
  const nameElement = document.getElementById(`name-${index}`);
  const floatElement = document.getElementById(`float-${index}`);
  const wearElement = document.getElementById(`wear-${index}`);
  const rarityElement = document.getElementById(`rarity-${index}`);
  const patternElement = document.getElementById(`pattern-${index}`);
  const inspectElement = document.getElementById(`inspect-${index}`);

  if (itemData.error) {
    // Keep existing basic info, just mark as error and update loading fields
    itemDiv.classList.remove('loading');
    itemDiv.classList.add('error');
    
    // Only update the fields that were showing "Analyzing..."
    floatElement.innerHTML = 'Analysis Failed';
    floatElement.classList.remove('loading-placeholder');
    floatElement.classList.add('error-message');
    
    patternElement.innerHTML = 'Analysis Failed';
    patternElement.classList.remove('loading-placeholder');
    patternElement.classList.add('error-message');
    
    // Add small error indicator but don't replace the whole item
    let existingError = itemDiv.querySelector('.error-message-small');
    if (!existingError) {
      const errorDiv = document.createElement('div');
      errorDiv.className = 'error-message-small';
      errorDiv.textContent = 'Detailed analysis failed';
      itemDiv.appendChild(errorDiv);
    }
    return;
  }

  itemDiv.classList.remove('loading');
  itemDiv.classList.add('loaded');
  
  // Enhance the name with detailed info if we got weapon/skin data
  if (itemData.weapon && itemData.skin) {
    nameElement.classList.remove('knife', 'souvenir');
    let nameText = `${itemData.weapon} | ${itemData.skin}`;
    if (itemData.special) {
      nameText += ` <span class="item-special">${itemData.special}</span>`;
    }
    if (itemData.stattrak) {
      nameText += '<span class="stattrak-badge">ST</span>';
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
  const paintwearFloat = uint32ToFloat32(itemData.paintwear);
  floatElement.textContent = paintwearFloat.toFixed(6);
  floatElement.classList.remove('loading-placeholder');
  
  // Update wear if we got better data, otherwise keep existing
  const detailedWear = getWearFromFloat(paintwearFloat);
  if (detailedWear !== 'Unknown' && detailedWear !== wearElement.textContent) {
    wearElement.textContent = detailedWear;
  }
  
  // Update rarity if we got better data, otherwise keep existing  
  const detailedRarity = getRarityFromNumber(itemData.rarity);
  if (detailedRarity !== 'Unknown' && detailedRarity !== rarityElement.textContent) {
    rarityElement.textContent = detailedRarity;
  }
  
  // Update pattern seed
  patternElement.textContent = itemData.paintseed;
  patternElement.classList.remove('loading-placeholder');

  // Ensure inspect link is set
  inspectElement.href = inspectLink || '#';
}

function updateProgress(completed, total) {
  const progressFill = elements.progressFill;
  const progressText = elements.progressText;
  
  const percentage = total > 0 ? (completed / total) * 100 : 0;
  progressFill.style.width = `${percentage}%`;
  progressText.textContent = `${completed} / ${total} items analyzed`;
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

    elements.loadingMessage.textContent = 'Analyzing items...';
    elements.inventoryContainer.style.display = 'block';
    
    const inventoryGrid = elements.inventoryGrid;
    inventoryGrid.innerHTML = '';
    
    updateSummary(inventoryData, []);
    
    const processedItems = [];
    
    for (let i = 0; i < csgoItems.length; i++) {
      // Check for cancellation before processing each item
      if (isCancelled) {
        throw new Error('Analysis was cancelled');
      }

      const item = csgoItems[i];
      const itemElement = createItemElement(item, i);
      inventoryGrid.appendChild(itemElement);
      
      try {
        const itemResponse = await fetch(`/api?${new URLSearchParams({url: item.inspect_link})}`, {
          signal: analysisController.signal
        });
        const itemData = await itemResponse.json();
        
        processedItems[i] = itemData.error ? null : itemData;
        updateItemWithDetails(itemData, i, item.inspect_link);
        
      } catch (error) {
        console.error(`Error loading item ${i}:`, error);
        processedItems[i] = null;
        updateItemWithDetails({ error: 'Failed to load item details' }, i, item.inspect_link);
      }
      
      updateProgress(i + 1, csgoItems.length);
      updateSummary(inventoryData, processedItems);
      
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    
    elements.inventoryStatus.style.display = 'none';
    elements.status.textContent = `Successfully analyzed ${csgoItems.length} items`;
    
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
  elements.inventoryGrid.innerHTML = '';
  elements.status.textContent = '';
  
  // Reset button states
  elements.button.style.display = 'inline-block';
  elements.cancelButton.style.display = 'none';
  
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
    inventoryGrid: document.getElementById("inventory-grid"),
    status: document.getElementById("status"),
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

  if (window.location.hash) {
    const hashSteamId = window.location.hash.substring(1);
    if (validateSteamId(hashSteamId)) {
      elements.textbox.value = hashSteamId;
      elements.button.click();
    }
  }
});