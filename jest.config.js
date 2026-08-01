module.exports = {
  testEnvironment: 'jsdom',
  setupFilesAfterEnv: ['<rootDir>/tests/setup.js'],
  testMatch: ['<rootDir>/tests/**/*.test.js'],
  collectCoverageFrom: [
    'wwwroot/*.js',
    '!wwwroot/*.min.js',
    '!**/node_modules/**'
  ],
  coverageDirectory: 'coverage',
  coverageReporters: ['text', 'lcov', 'html', 'json'],
  // Coverage ratchet. These are floors, not goals.
  //
  // Measured 2026-08-01 (`npx jest --coverage`, 8 suites / 171 tests):
  //   statements 40.66%  branches 44.33%  functions 36.47%  lines 41.78%
  // The floors below sit ~2 points under those numbers so ordinary variation
  // (a skipped test, a helper moving between files) cannot redden a healthy build.
  //
  // These numbers may only ever be RATCHETED UPWARD. Lowering a floor to make a
  // build pass defeats the entire point of the gate: it converts a real signal
  // ("this change deleted coverage") into a rubber stamp. If your change drops
  // coverage below a floor, add the tests - do not edit this block.
  //
  // The previous value here was an aspirational 70% that CI never ran (it invoked
  // jest without --coverage), so it enforced nothing for the whole life of the
  // repo. A 22% gate that runs beats a 70% gate that does not.
  //
  // wwwroot/inventory-item.js went 0% -> ~98% on 2026-08-01, which is what moved the global
  // numbers here. Biggest single win still available: wwwroot/post.js (the single-item page)
  // at ~13%.
  coverageThreshold: {
    global: {
      branches: 42,
      functions: 34,
      lines: 39,
      statements: 38
    }
  },
  verbose: true,
  testTimeout: 10000,
  moduleDirectories: ['node_modules', '<rootDir>'],
  testPathIgnorePatterns: [
    '/node_modules/',
    '/bin/',
    '/obj/',
    '/TestResults/'
  ],
  transform: {
    '^.+\\.js$': 'babel-jest'
  },
  transformIgnorePatterns: [
    'node_modules/(?!(some-es6-module)/)'
  ]
};