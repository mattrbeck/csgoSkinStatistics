// Jest setup file
require('@testing-library/jest-dom');

// Mock console methods to reduce noise in tests
global.console = {
  ...console,
  log: jest.fn(),
  warn: jest.fn(),
  error: jest.fn(),
};

// Mock window.location for tests that manipulate the URL
delete window.location;
window.location = {
  hash: '',
  href: 'http://localhost/',
  origin: 'http://localhost',
  protocol: 'http:',
  host: 'localhost',
  hostname: 'localhost',
  port: '',
  pathname: '/',
  search: '',
  assign: jest.fn(),
  replace: jest.fn(),
  reload: jest.fn(),
};

// Mock fetch for API calls. The default returns a resolvable (but empty) response so that
// modules which kick off a fetch at load time (e.g. inventory.js fetching float-ranges.json)
// can be required without throwing; individual tests override it with mockResolvedValueOnce etc.
global.fetch = jest.fn(() =>
  Promise.resolve({ ok: false, json: () => Promise.resolve(null) }));

// Mock performance.now for timing tests
global.performance = {
  now: jest.fn(() => Date.now()),
};

// Add DOM cleanup after each test
afterEach(() => {
  document.body.innerHTML = '';
  jest.clearAllMocks();
});