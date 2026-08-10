export function parseIni(iniString: string): Record<string, Record<string, string>> {
  const result: Record<string, Record<string, string>> = {};
  let currentSection = '';

  const lines = iniString.split('\n');
  for (const line of lines) {
    const trimmed = line.trim();
    if (trimmed.startsWith(';')) continue; // Comment
    if (trimmed.startsWith('[') && trimmed.endsWith(']')) {
      currentSection = trimmed.slice(1, -1).trim();
      result[currentSection] = {};
    } else if (trimmed.includes('=')) {
      const idx = trimmed.indexOf('=');
      const key = trimmed.slice(0, idx).trim();
      const val = trimmed.slice(idx + 1).trim();
      if (currentSection) {
        result[currentSection][key] = val;
      } else {
        if (!result['']) result[''] = {};
        result[''][key] = val;
      }
    }
  }

  return result;
}
