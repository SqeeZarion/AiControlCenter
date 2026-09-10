import { safeLocalReturnUrl } from './safe-return-url';

describe('safeLocalReturnUrl', () => {
  it('accepts only well-formed local application URLs', () => {
    expect(safeLocalReturnUrl('/directions?page=2#active')).toBe('/directions?page=2#active');
    expect(safeLocalReturnUrl('https://evil.example')).toBeNull();
    expect(safeLocalReturnUrl('//evil.example/path')).toBeNull();
    expect(safeLocalReturnUrl('/\\evil.example/path')).toBeNull();
    expect(safeLocalReturnUrl('/%')).toBeNull();
    expect(safeLocalReturnUrl('/login?returnUrl=%2Fdirections')).toBeNull();
  });
});
