import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { CodexStateTracker, extractThreadId } from './state-mapper.mjs';
import { findThreadName } from './thread-index.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(here, '..');

function argValue(name, fallback = null) {
  const index = process.argv.indexOf(name);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const port = Number(argValue('--port', process.env.PORT || '8765'));
const host = argValue('--host', process.env.HOST || '127.0.0.1');
const requestedThreadId = argValue('--thread-id', process.env.CODEX_THREAD_ID || null);
const codexHome = path.resolve(process.env.CODEX_HOME || path.join(os.homedir(), '.codex'));
const sessionsRoot = path.join(codexHome, 'sessions');
const sessionIndexPath = path.join(codexHome, 'session_index.jsonl');

if (!Number.isInteger(port) || port < 1 || port > 65535) {
  throw new Error(`Invalid port: ${port}`);
}
const codexSessionsAvailable = fs.existsSync(sessionsRoot);

const clients = new Set();
let replaying = false;
let currentFile = null;
let currentOffset = 0;
let leftover = '';
let currentThreadId = null;
let currentTaskName = null;
let lastDiscoveryAt = 0;
let lastTitleRefreshAt = 0;

function publicState(state = tracker.current) {
  return {
    ...state,
    threadId: currentThreadId,
    taskName: currentTaskName,
    taskActive: Boolean(tracker.turnActive),
    trackingMode: requestedThreadId ? 'fixed-thread' : 'most-recent-active',
    bridgeVersion: 1
  };
}

function sendEvent(response, state) {
  response.write(`event: status\ndata: ${JSON.stringify(publicState(state))}\n\n`);
}

function broadcast(state) {
  if (replaying) return;
  for (const client of clients) sendEvent(client, state);
}

function refreshCurrentTaskName(force = false) {
  const now = Date.now();
  if (!force && now - lastTitleRefreshAt < 2000) return;
  lastTitleRefreshAt = now;
  let nextTaskName = null;
  try {
    nextTaskName = findThreadName(fs.readFileSync(sessionIndexPath, 'utf8'), currentThreadId);
  } catch {
    // Older or headless Codex installs may not maintain a local title index.
  }
  if (nextTaskName === currentTaskName) return;
  currentTaskName = nextTaskName;
  broadcast(tracker.current);
}

const tracker = new CodexStateTracker(broadcast);

function listSessionFiles(directory, output = []) {
  let entries = [];
  try {
    entries = fs.readdirSync(directory, { withFileTypes: true });
  } catch {
    return output;
  }
  for (const entry of entries) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) listSessionFiles(fullPath, output);
    else if (entry.isFile() && entry.name.endsWith('.jsonl')) output.push(fullPath);
  }
  return output;
}

function findTargetSession() {
  const files = listSessionFiles(sessionsRoot);
  let best = null;
  for (const file of files) {
    if (requestedThreadId && !path.basename(file).includes(requestedThreadId)) continue;
    let stat;
    try {
      stat = fs.statSync(file);
    } catch {
      continue;
    }
    if (!best || stat.mtimeMs > best.mtimeMs) best = { file, mtimeMs: stat.mtimeMs };
  }
  return best?.file || null;
}

function processLine(line) {
  const trimmed = line.trim();
  if (!trimmed) return;
  try {
    tracker.process(JSON.parse(trimmed));
  } catch {
    // A concurrently written partial JSON line is retained in `leftover`; malformed
    // complete lines are ignored so the bridge never blocks Codex itself.
  }
}

function replayFile(file) {
  replaying = true;
  currentFile = file;
  currentThreadId = extractThreadId(file);
  refreshCurrentTaskName(true);
  currentOffset = 0;
  leftover = '';
  const fresh = new CodexStateTracker((state) => {
    tracker.current = state;
    tracker.sequence = state.sequence;
    tracker.turnActive = fresh.turnActive;
    tracker.expiresAt = fresh.expiresAt;
  });
  const content = fs.readFileSync(file, 'utf8');
  const lines = content.split(/\r?\n/);
  const endsWithNewline = /\r?\n$/.test(content);
  const pending = endsWithNewline ? '' : (lines.pop() || '');
  for (const line of lines) {
    if (!line.trim()) continue;
    try {
      fresh.process(JSON.parse(line));
    } catch {
      // Ignore a malformed historical line; Codex continues writing independently.
    }
  }
  fresh.tick(Date.now());
  tracker.current = fresh.current;
  tracker.sequence = fresh.sequence;
  tracker.turnActive = fresh.turnActive;
  tracker.expiresAt = fresh.expiresAt;
  leftover = pending;
  currentOffset = Buffer.byteLength(content);
  replaying = false;
  broadcast(tracker.current);
}

function readAppended() {
  if (!currentFile) return;
  let stat;
  try {
    stat = fs.statSync(currentFile);
  } catch {
    currentFile = null;
    return;
  }
  if (stat.size < currentOffset) {
    replayFile(currentFile);
    return;
  }
  if (stat.size === currentOffset) return;

  const length = stat.size - currentOffset;
  const buffer = Buffer.alloc(length);
  const descriptor = fs.openSync(currentFile, 'r');
  try {
    fs.readSync(descriptor, buffer, 0, length, currentOffset);
  } finally {
    fs.closeSync(descriptor);
  }
  currentOffset = stat.size;
  const text = leftover + buffer.toString('utf8');
  const lines = text.split(/\r?\n/);
  leftover = lines.pop() || '';
  for (const line of lines) processLine(line);
}

function pollSessions() {
  const now = Date.now();
  if (!currentFile || now - lastDiscoveryAt >= 2000) {
    lastDiscoveryAt = now;
    const target = findTargetSession();
    if (target && target !== currentFile) replayFile(target);
  }
  readAppended();
  refreshCurrentTaskName();
  tracker.tick(now);
}

const mimeTypes = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
  '.webp': 'image/webp'
};

function sendJson(response, statusCode, value) {
  response.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store'
  });
  response.end(JSON.stringify(value));
}

function serveStatic(request, response) {
  const url = new URL(request.url, `http://${request.headers.host || `${host}:${port}`}`);
  let pathname = decodeURIComponent(url.pathname);
  if (pathname === '/') pathname = '/codex.html';
  const filePath = path.resolve(projectRoot, `.${pathname}`);
  if (!filePath.startsWith(projectRoot + path.sep)) {
    sendJson(response, 403, { error: 'Forbidden' });
    return;
  }
  let stat;
  try {
    stat = fs.statSync(filePath);
  } catch {
    sendJson(response, 404, { error: 'Not found' });
    return;
  }
  if (!stat.isFile()) {
    sendJson(response, 404, { error: 'Not found' });
    return;
  }
  response.writeHead(200, {
    'Content-Type': mimeTypes[path.extname(filePath).toLowerCase()] || 'application/octet-stream',
    'Cache-Control': filePath.endsWith('.html') || filePath.endsWith('.js') || filePath.endsWith('.css')
      ? 'no-store'
      : 'public, max-age=3600'
  });
  fs.createReadStream(filePath).pipe(response);
}

const server = http.createServer((request, response) => {
  const url = new URL(request.url, `http://${request.headers.host || `${host}:${port}`}`);
  if (url.pathname === '/api/health') {
    sendJson(response, 200, { ok: true, threadId: currentThreadId, codexSessionsAvailable });
    return;
  }
  if (url.pathname === '/api/status') {
    sendJson(response, 200, publicState());
    return;
  }
  if (url.pathname === '/api/events') {
    response.writeHead(200, {
      'Content-Type': 'text/event-stream; charset=utf-8',
      'Cache-Control': 'no-cache, no-transform',
      Connection: 'keep-alive',
      'X-Accel-Buffering': 'no'
    });
    response.write('retry: 1500\n\n');
    clients.add(response);
    sendEvent(response, tracker.current);
    request.on('close', () => clients.delete(response));
    return;
  }
  serveStatic(request, response);
});

pollSessions();
const pollTimer = setInterval(pollSessions, 350);
const keepAliveTimer = setInterval(() => {
  for (const client of clients) client.write(': keepalive\n\n');
}, 15000);

server.listen(port, host, () => {
  console.log(`Emotion Ball Codex bridge: http://${host}:${port}/codex.html`);
  console.log(codexSessionsAvailable
    ? `Tracking: ${requestedThreadId || 'most recently updated Codex task'}`
    : 'Codex sessions not found; running in local App activity mode.');
});

function shutdown() {
  clearInterval(pollTimer);
  clearInterval(keepAliveTimer);
  for (const client of clients) client.end();
  server.close(() => process.exit(0));
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
