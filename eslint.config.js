module.exports = [
  {
    files: ["**/*.mjs"],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: "module",
      globals: {
        console: "readonly",
        fetch: "readonly",
        process: "readonly"
      }
    },
    rules: {
      "no-unused-vars": "warn",
      "no-console": "off"
    }
  },
  {
    files: ["**/*.js"],
    languageOptions: {
      ecmaVersion: 2021,
      sourceType: "script",
      globals: {
        browser: true,
        jest: true,
        console: "readonly",
        document: "readonly",
        window: "readonly",
        fetch: "readonly",
        URLSearchParams: "readonly",
        performance: "readonly",
        customElements: "readonly",
        HTMLElement: "readonly",
        // Shared rendering helpers defined in decals.js, loaded before the page scripts.
        buildStickerChips: "readonly",
        buildFloatBar: "readonly",
        buildWearPill: "readonly",
        rarityColorOf: "readonly"
      }
    },
    rules: {
      "no-unused-vars": "warn",
      "no-console": "off"
    }
  }
];