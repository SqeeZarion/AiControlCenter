const returnUrlBase = 'https://aicontrolcenter.invalid';

export function safeLocalReturnUrl(value: string | null | undefined): string | null {
  if (!value || !value.startsWith('/') || value.startsWith('//')) {
    return null;
  }

  try {
    decodeURI(value);
    const parsed = new URL(value, returnUrlBase);
    if (parsed.origin !== returnUrlBase || parsed.pathname.startsWith('/login')) {
      return null;
    }

    return `${parsed.pathname}${parsed.search}${parsed.hash}`;
  } catch {
    return null;
  }
}
