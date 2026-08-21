import fs from 'node:fs';
import fsp from 'node:fs/promises';
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
if (!['127.0.0.1', 'localhost', '::1'].includes(host)) {
  throw new Error(`The Codex bridge only accepts loopback hosts; refusing to bind ${host}.`);
}

const BRIDGE_VERSION = 2;
const SESSION_CHUNK_BYTES = 1024 * 1024;
const MAX_SESSION_BYTES = 64 * 1024 * 1024;
const MAX_APPEND_BYTES = 8 * 1024 * 1024;

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
    bridgeVersion: BRIDGE_VERSION
  };
}

function sendEvent(response, state) {
  try {
    response.write(`event: status\ndata: ${JSON.stringify(publicState(state))}\n\n`);
  } catch {
    clients.delete(response);
    response.destroy();
  }
}

function broadcast(state) {
  if (replaying) return;
  for (const client of clients) sendEvent(client, state);
}

async function refreshCurrentTaskName(force = false) {
  const now = Date.now();
  if (!force && now - lastTitleRefreshAt < 2000) return;
  lastTitleRefreshAt = now;
  let nextTaskName = null;
  try {
    nextTaskName = findThreadName(await fsp.readFile(sessionIndexPath, 'utf8'), currentThreadId);
  } catch {
    // Older or headless Codex installs may not maintain a local title index.
  }
  if (nextTaskName === currentTaskName) return;
  currentTaskName = nextTaskName;
  broadcast(tracker.current);
}

const tracker = new CodexStateTracker(broadcast);

async function listSessionFiles(directory, output = []) {
  let entries = [];
  try {
    entries = await fsp.readdir(directory, { withFileTypes: true });
  } catch {
    return output;
  }
  for (const entry of entries) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) await listSessionFiles(fullPath, output);
    else if (entry.isFile() && entry.name.endsWith('.jsonl')) output.push(fullPath);
  }
  return output;
}

async function findTargetSession() {
  const files = await listSessionFiles(sessionsRoot);
  let best = null;
  for (const file of files) {
    if (requestedThreadId && !stringEqualsIgnoreCase(extractThreadId(file), requestedThreadId)) continue;
    let stat;
    try {
      stat = await fsp.stat(file);
    } catch {
      continue;
    }
    if (!best || stat.mtimeMs > best.mtimeMs) best = { file, mtimeMs: stat.mtimeMs };
  }
  return best?.file || null;
}

function stringEqualsIgnoreCase(left, right) {
  return typeof left === 'string'
    && typeof right === 'string'
    && left.toLowerCase() === right.toLowerCase();
}

function processLine(line, targetTracker = tracker) {
  const trimmed = line.trim();
  if (!trimmed) return;
  try {
    targetTracker.process(JSON.parse(trimmed));
  } catch {
    // A concurrently written partial JSON line is retained in `leftover`; malformed
    // complete lines are ignored so the bridge never blocks Codex itself.
  }
}

async function replayFile(file) {
  const stat = await fsp.stat(file);
  if (stat.size > MAX_SESSION_BYTES) {
    throw new Error(`Session file is too large to replay: ${file}`);
  }
  const fresh = new CodexStateTracker((state) => {
    // Replay is intentionally silent; the final state is published atomically.
  });
  let offset = 0;
  let pending = '';
  const handle = await fsp.open(file, 'r');
  try {
    while (offset < stat.size) {
      const length = Math.min(SESSION_CHUNK_BYTES, stat.size - offset);
      const buffer = Buffer.alloc(length);
      const { bytesRead } = await handle.read(buffer, 0, length, offset);
      if (bytesRead <= 0) break;
      offset += bytesRead;
      const lines = (pending + buffer.subarray(0, bytesRead).toString('utf8')).split(/\r?\n/);
      pending = lines.pop() || '';
      for (const line of lines) {
        if (line.trim()) processLine(line, fresh);
      }
    }
  } finally {
    await handle.close();
  }
  fresh.tick(Date.now());
  replaying = true;
  currentFile = file;
  currentThreadId = extractThreadId(file);
  currentOffset = offset;
  leftover = pending;
  await refreshCurrentTaskName(true);
  tracker.current = fresh.current;
  tracker.sequence = fresh.sequence;
  tracker.turnActive = fresh.turnActive;
  tracker.expiresAt = fresh.expiresAt;
  replaying = false;
  broadcast(tracker.current);
}

function clearCurrentSession() {
  currentFile = null;
  currentOffset = 0;
  leftover = '';
  currentThreadId = null;
  currentTaskName = null;
  tracker.turnActive = false;
  tracker.expiresAt = null;
  broadcast(tracker.current);
}

async function readAppended() {
  if (!currentFile) return;
  let stat;
  try {
    stat = await fsp.stat(currentFile);
  } catch {
    clearCurrentSession();
    return;
  }
  if (stat.size < currentOffset) {
    await replayFile(currentFile);
    return;
  }
  if (stat.size === currentOffset) return;

  const length = stat.size - currentOffset;
  if (length > MAX_APPEND_BYTES) {
    await replayFile(currentFile);
    return;
  }
  const buffer = Buffer.alloc(Math.min(length, SESSION_CHUNK_BYTES));
  const handle = await fsp.open(currentFile, 'r');
  try {
    const { bytesRead } = await handle.read(buffer, 0, buffer.length, currentOffset);
    currentOffset += bytesRead;
    const text = leftover + buffer.subarray(0, bytesRead).toString('utf8');
    const lines = text.split(/\r?\n/);
    leftover = lines.pop() || '';
    for (const line of lines) processLine(line);
  } finally {
    await handle.close();
  }
}

let pollInFlight = false;
async function pollSessions() {
  if (pollInFlight) return;
  pollInFlight = true;
  const now = Date.now();
  try {
    if (!currentFile || now - lastDiscoveryAt >= 2000) {
      lastDiscoveryAt = now;
      const target = await findTargetSession();
      if (target && target !== currentFile) await replayFile(target);
    }
    await readAppended();
    await refreshCurrentTaskName();
    tracker.tick(now);
  } catch (error) {
    console.error('[Emotionball-Deskpet] session poll failed:', error?.message || error);
    if (error?.code === 'ENOENT' || error?.code === 'EACCES' || error?.code === 'EPERM') {
      clearCurrentSession();
    }
  } finally {
    pollInFlight = false;
  }
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

async function serveStatic(request, response, url) {
  let pathname;
  try {
    pathname = decodeURIComponent(url.pathname);
  } catch {
    sendJson(response, 400, { error: 'Invalid URL' });
    return;
  }
  if (pathname === '/') pathname = '/codex.html';
  const filePath = path.resolve(projectRoot, `.${pathname}`);
  if (!filePath.startsWith(projectRoot + path.sep)) {
    sendJson(response, 403, { error: 'Forbidden' });
    return;
  }
  let stat;
  try {
    const realPath = await fsp.realpath(filePath);
    if (realPath !== projectRoot && !realPath.startsWith(projectRoot + path.sep)) {
      sendJson(response, 403, { error: 'Forbidden' });
      return;
    }
    stat = await fsp.stat(realPath);
    if (!stat.isFile()) {
      sendJson(response, 404, { error: 'Not found' });
      return;
    }
    response.writeHead(200, {
      'Content-Type': mimeTypes[path.extname(realPath).toLowerCase()] || 'application/octet-stream',
      'Cache-Control': realPath.endsWith('.html') || realPath.endsWith('.js') || realPath.endsWith('.css')
        ? 'no-store'
        : 'public, max-age=3600'
    });
    fs.createReadStream(realPath).on('error', () => response.destroy()).pipe(response);
  } catch {
    if (!response.headersSent) sendJson(response, 404, { error: 'Not found' });
    else response.destroy();
  }
}

const server = http.createServer((request, response) => {
  try {
    const url = new URL(request.url || '/', 'http://127.0.0.1');
    if (url.origin !== 'http://127.0.0.1') {
      sendJson(response, 400, { error: 'Absolute URLs are not accepted' });
      return;
    }
    if (url.pathname === '/api/health') {
      sendJson(response, 200, { ok: true, bridgeVersion: BRIDGE_VERSION, threadId: currentThreadId, codexSessionsAvailable: fs.existsSync(sessionsRoot) });
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
    void serveStatic(request, response, url).catch((error) => {
      console.error('[Emotionball-Deskpet] static request failed:', error?.message || error);
      if (!response.headersSent) sendJson(response, 500, { error: 'Internal server error' });
      else response.destroy();
    });
  } catch {
    if (!response.headersSent) sendJson(response, 400, { error: 'Invalid request' });
    else response.destroy();
  }
});

void pollSessions();
const pollTimer = setInterval(() => void pollSessions(), 350);
const keepAliveTimer = setInterval(() => {
  for (const client of clients) {
    try {
      client.write(': keepalive\n\n');
    } catch {
      clients.delete(client);
    }
  }
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
process.on('uncaughtException', (error) => {
  console.error('[Emotionball-Deskpet] uncaught bridge error:', error);
});
process.on('unhandledRejection', (error) => {
  console.error('[Emotionball-Deskpet] unhandled bridge rejection:', error);
});
