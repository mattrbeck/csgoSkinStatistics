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
    // cert-decode.js is the one real ES module under wwwroot: post.js pulls it in with a dynamic
    // import() so it stays out of the initial page payload. It keeps the .js extension (rather
    // than .mjs) so `npm run lint:js` and jest's `wwwroot/*.js` coverage glob both still see it.
    files: ["wwwroot/cert-decode.js"],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: "module",
      globals: {
        TextDecoder: "readonly"
      }
    },
    rules: {
      "no-unused-vars": "warn",
      "no-console": "off"
    }
  },
  {
    files: ["**/*.js"],
    ignores: ["wwwroot/cert-decode.js"],
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
        rarityColorOf: "readonly",
        enableTooltip: "readonly",
        enableLongPressCopy: "readonly"
      }
    },
    rules: {
      "no-unused-vars": "warn",
      "no-console": "off"
    }
  }
];