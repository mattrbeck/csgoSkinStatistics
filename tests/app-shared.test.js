/**
 * Tests for app-shared.js - the unified search-bar input classifier and the recent-searches
 * localStorage helpers shared by both pages.
 */

const {
  classifyInput, reduceInspect, loadRecents, addRecent, removeRecent, clearRecents,
} = require('../wwwroot/app-shared.js');

describe('classifyInput', () => {
  test('classic S-form inspect link -> item', () => {
    const full = 'steam://run/730//+csgo_econ_action_preview%20S76561198261551396A12256887280D2776544801323831695';
    const r = classifyInput(full);
    expect(r.kind).toBe('item');
    expect(r.value).toBe('S76561198261551396A12256887280D2776544801323831695');
  });

  test('bare S-form payload -> item', () => {
    expect(classifyInput('M625254122282020305A6760346663D30614827701953021').kind).toBe('item');
  });

  test('17-digit SteamID64 -> profile (not mistaken for hex)', () => {
    const r = classifyInput('76561198261551396');
    expect(r.kind).toBe('profile');
    expect(r.value).toBe('76561198261551396');
  });

  test('steamcommunity profile URL -> profile', () => {
    expect(classifyInput('https://steamcommunity.com/id/mattrb').kind).toBe('profile');
    expect(classifyInput('https://steamcommunity.com/profiles/76561198261551396').kind).toBe('profile');
  });

  test('bare vanity name -> profile', () => {
    expect(classifyInput('mattrb').kind).toBe('profile');
  });

  test('long hex cert payload -> item', () => {
    const hex = '0018' + 'A1B2C3D4E5'.repeat(6); // > 34 hex chars
    const r = classifyInput(hex);
    expect(r.kind).toBe('item');
    expect(r.value).toBe(hex.toUpperCase());
  });

  test('short hex-looking vanity stays a profile', () => {
    // "beef" is valid hex but far too short to be a cert; it's a plausible vanity.
    expect(classifyInput('beef').kind).toBe('profile');
  });

  test('empty / junk -> null', () => {
    expect(classifyInput('').kind).toBeNull();
    expect(classifyInput('   ').kind).toBeNull();
    expect(classifyInput('not a valid !!! input').kind).toBeNull();
  });
});

describe('reduceInspect', () => {
  test('strips the inspect prefix', () => {
    expect(reduceInspect('steam://run/730//+csgo_econ_action_preview%20S1A2D3')).toBe('S1A2D3');
  });
  test('leaves a bare payload untouched', () => {
    expect(reduceInspect('S1A2D3')).toBe('S1A2D3');
  });
});

describe('recent searches', () => {
  beforeEach(() => clearRecents());

  test('addRecent stores newest-first and dedupes by type+value', () => {
    addRecent({ type: 'item', value: 'S1A2D3', label: 'AK-47 | Redline' });
    addRecent({ type: 'profile', value: 'mattrb', label: 'matt' });
    addRecent({ type: 'item', value: 'S1A2D3', label: 'AK-47 | Redline' }); // repeat -> moves to front
    const list = loadRecents();
    expect(list).toHaveLength(2);
    expect(list[0]).toMatchObject({ type: 'item', value: 'S1A2D3' });
  });

  test('addRecent ignores entries without a value or label', () => {
    addRecent({ type: 'item', value: '', label: 'x' });
    addRecent({ type: 'item', value: 'y' });
    expect(loadRecents()).toHaveLength(0);
  });

  test('removeRecent drops only the matching entry', () => {
    addRecent({ type: 'item', value: 'S1A2D3', label: 'a' });
    addRecent({ type: 'profile', value: 'mattrb', label: 'b' });
    removeRecent('item', 'S1A2D3');
    const list = loadRecents();
    expect(list).toHaveLength(1);
    expect(list[0].type).toBe('profile');
  });
});
