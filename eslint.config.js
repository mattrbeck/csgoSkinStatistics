module.exports = [
  {
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
        HTMLElement: "readonly"
      }
    },
    rules: {
      "no-unused-vars": "warn",
      "no-console": "off"
    }
  }
];