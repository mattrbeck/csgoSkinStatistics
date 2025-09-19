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

// Mock fetch for API calls
global.fetch = jest.fn();

// Mock performance.now for timing tests
global.performance = {
  now: jest.fn(() => Date.now()),
};

// Add DOM cleanup after each test
afterEach(() => {
  document.body.innerHTML = '';
  jest.clearAllMocks();
});