# Testing Guide for CS:GO Skin Statistics

This document provides comprehensive information about the testing infrastructure and how to run tests for the CS:GO Skin Statistics application.

## Overview

The project includes comprehensive test coverage for both backend (.NET) and frontend (JavaScript) components:

- **Backend Tests**: xUnit tests for C# services, controllers, and models
- **Frontend Tests**: Jest tests for JavaScript functionality
- **Integration Tests**: API endpoint testing
- **Code Quality**: ESLint and Stylelint for code standards
- **CI/CD**: Automated testing pipeline with GitHub Actions

## Test Structure

```
csgoSkinStatistics/
├── csgoSkinStatistics.Tests/          # Backend C# tests
│   ├── Controllers/                   # Controller tests
│   ├── Services/                      # Service layer tests
│   └── Models/                        # Model tests
├── tests/                             # Frontend JavaScript tests
│   ├── setup.js                       # Jest configuration
│   ├── post.test.js                   # Main page functionality
│   ├── inventory.test.js              # Inventory page functionality
│   └── utils.test.js                  # Utility functions
├── .github/workflows/ci.yml           # CI/CD pipeline
├── test-runner.sh                     # Unix test runner
├── test-runner.ps1                    # Windows test runner
└── jest.config.js                     # Jest configuration
```

## Running Tests

### All Tests (Recommended)

**Linux/macOS:**
```bash
./test-runner.sh
```

**Windows:**
```powershell
.\test-runner.ps1
```

### Backend Tests Only

```bash
dotnet test csgoSkinStatistics.Tests --verbosity normal
```

### Frontend Tests Only

```bash
npm test
```

### With Coverage

```bash
npm test -- --coverage
```

### Watch Mode (Frontend)

```bash
npm run test:watch
```

## Test Categories

### Backend Tests (.NET/C#)

#### DatabaseService Tests
- Database initialization
- Item saving and retrieval
- Stickers and keychains handling
- Error handling

#### ConstDataService Tests
- JSON data loading
- Item information retrieval
- Special pattern handling (Fire & Ice, Doppler, Fade)
- Missing data handling

#### SteamService Tests
- Account management
- Steam ID validation
- Connection handling
- Rate limiting

#### Controller Tests
- API endpoint validation
- URL parsing
- Request/response handling
- Error scenarios

#### Model Tests
- Data serialization/deserialization
- Property validation
- Type safety

### Frontend Tests (JavaScript)

#### Main Page (post.js)
- Item data display
- URL validation
- API integration
- Error handling
- Utility functions (float conversion, wear calculation)

#### Inventory Page (inventory.js)
- Inventory analysis
- Item sorting and filtering
- Progress tracking
- Steam ID validation
- Web component functionality

#### Utility Functions
- URL encoding/decoding
- Data type conversions
- Array manipulation
- DOM utilities
- Local storage handling

## Code Quality Tools

### JavaScript Linting
```bash
npm run lint:js
```

### CSS Linting
```bash
npm run lint:css
```

### All Linting
```bash
npm run lint
```

## Test Configuration

### Jest Configuration (jest.config.js)
- Test environment: jsdom
- Coverage thresholds: 70% minimum
- Test file patterns: `tests/**/*.test.js`
- Setup file: `tests/setup.js`

### Backend Test Configuration
- Framework: xUnit
- Test runner: dotnet test
- Mocking: Moq
- In-memory database: SQLite

## Coverage Reports

Test coverage reports are generated in:
- **Frontend**: `coverage/` directory (HTML, LCOV, JSON)
- **Backend**: TestResults directory (when using `--collect:"XPlat Code Coverage"`)

View HTML coverage report:
```bash
open coverage/lcov-report/index.html  # macOS
start coverage/lcov-report/index.html # Windows
```

## Continuous Integration

The project includes a GitHub Actions workflow (`.github/workflows/ci.yml`) that:

1. **Runs on**: Push to main branches, pull requests
2. **Test Environment**: Ubuntu latest with .NET 9.0 and Node.js 20.x
3. **Steps**:
   - Checkout code
   - Setup .NET and Node.js
   - Install dependencies
   - Run backend tests with coverage
   - Run frontend tests with coverage
   - Run linting checks
   - Build application
   - Security scanning with Trivy
   - Upload test artifacts

## Mock Data and Test Fixtures

### Backend Mocking
- Mock Steam services for testing without real Steam connections
- In-memory database for isolated tests
- Mock HTTP clients for external API calls

### Frontend Mocking
- Mock fetch for API calls
- Mock DOM elements
- Mock localStorage/sessionStorage
- Mock performance APIs

## Writing New Tests

### Backend Test Example
```csharp
[Fact]
public async Task GetItemAsync_ShouldReturnCorrectItem()
{
    // Arrange
    var item = new CEconItemPreviewDataBlock { itemid = 12345 };
    await _databaseService.SaveItemAsync(item);

    // Act
    var result = await _databaseService.GetItemAsync(12345);

    // Assert
    Assert.NotNull(result);
    Assert.Equal(12345UL, result.itemid);
}
```

### Frontend Test Example
```javascript
test('should convert uint32 to float32 correctly', () => {
    const result = uint32ToFloat32(1065353216);
    expect(result).toBeCloseTo(1.0);
});
```

## Debugging Tests

### Backend
- Use Visual Studio or Visual Studio Code debugger
- Set breakpoints in test files
- Run single tests: `dotnet test --filter "TestMethodName"`

### Frontend
- Use `console.log()` in tests (mocked by default)
- Run single test file: `npm test -- inventory.test.js`
- Use `--verbose` flag for detailed output

## Performance Testing

While not included in the current setup, consider adding:
- Load testing for API endpoints
- Performance benchmarks for critical functions
- Memory usage monitoring

## Best Practices

1. **Test Naming**: Use descriptive names that explain what is being tested
2. **Arrange-Act-Assert**: Structure tests clearly
3. **Isolation**: Each test should be independent
4. **Mocking**: Mock external dependencies
5. **Coverage**: Aim for high coverage but focus on critical paths
6. **Fast Tests**: Keep tests fast and reliable

## Troubleshooting

### Common Issues

1. **Tests not found**: Check file patterns in jest.config.js
2. **Module not found**: Ensure dependencies are installed (`npm install`)
3. **Backend compilation errors**: Check project references and package versions
4. **Permission denied**: Make test-runner.sh executable (`chmod +x test-runner.sh`)

### Environment Issues
- Ensure .NET 9.0 SDK is installed
- Ensure Node.js 20+ is installed
- Check that all npm dependencies are installed

For more help, check the project issues on GitHub or contact the development team.