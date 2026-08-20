function normalizeThreadName(value) {
  if (typeof value !== 'string') return null;
  const normalized = value.replace(/\s+/g, ' ').trim();
  return normalized ? normalized.slice(0, 160) : null;
}

export function findThreadName(indexContent, threadId) {
  if (!threadId || typeof indexContent !== 'string') return null;
  let result = null;
  for (const line of indexContent.split(/\r?\n/)) {
    if (!line.trim()) continue;
    try {
      const entry = JSON.parse(line);
      if (entry?.id === threadId) result = normalizeThreadName(entry.thread_name);
    } catch {
      // Ignore a concurrently written or historical malformed index line.
    }
  }
  return result;
}
