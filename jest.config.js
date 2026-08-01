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
  // Measured 2026-07-31 (`npx jest --coverage`, 7 suites / 90 tests):
  //   statements 24.06%  branches 26.21%  functions 27.05%  lines 24.14%
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
  // Biggest single win available: wwwroot/inventory-item.js (949 lines) is at 0%.
  coverageThreshold: {
    global: {
      branches: 24,
      functions: 25,
      lines: 22,
      statements: 22
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