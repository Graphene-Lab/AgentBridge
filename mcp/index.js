#!/usr/bin/env node
/**
 * AgentBridge MCP stdio bridge.
 *
 * AgentBridge exposes Model Context Protocol over HTTP (`POST /mcp`, JSON-RPC 2.0).
 * MCP hosts that only launch stdio servers (Claude Desktop extensions, VS Code stdio
 * servers, ...) cannot talk to an HTTP endpoint directly, so this bridge forwards every
 * newline-delimited JSON-RPC message from stdin to that endpoint and writes the answers
 * back to stdout. Nothing else is written to stdout: it carries the protocol.
 *
 * Configuration (environment):
 *   AGENTBRIDGE_URL        HTTP MCP endpoint (default http://localhost:5290/mcp)
 *   AGENTBRIDGE_TIMEOUT_MS per-request timeout (default 900000 = 15 minutes)
 *
 * Requires Node.js 18 or newer (global fetch).
 */
'use strict';

const readline = require('node:readline');

const ENDPOINT = process.env.AGENTBRIDGE_URL || 'http://localhost:5290/mcp';
const TIMEOUT_MS = Number.parseInt(process.env.AGENTBRIDGE_TIMEOUT_MS || '', 10) || 900000;

let sessionId = null;
let stdoutQueue = Promise.resolve();

function log(message) {
  process.stderr.write(`[agentbridge-mcp] ${message}\n`);
}

function emit(payload) {
  const text = typeof payload === 'string' ? payload.trim() : JSON.stringify(payload);
  if (!text) {
    return;
  }
  try {
    JSON.parse(text);
  } catch {
    log(`dropping payload that is not JSON-RPC: ${text.slice(0, 200)}`);
    return;
  }
  stdoutQueue = stdoutQueue.then(
    () =>
      new Promise((resolve) => {
        process.stdout.write(`${text}\n`, () => resolve());
      }),
  );
}

function idOf(message) {
  if (Array.isArray(message) || message === null || typeof message !== 'object') {
    return null;
  }
  return message.id === undefined ? null : message.id;
}

function replyError(id, code, message) {
  if (id === null || id === undefined) {
    return; // notifications have no reply
  }
  emit({ jsonrpc: '2.0', id, error: { code, message } });
}

function handleSseBlock(block) {
  const data = block
    .split(/\r?\n/)
    .filter((line) => line.startsWith('data:'))
    .map((line) => line.slice(5).trim())
    .join('\n');
  if (data) {
    emit(data);
  }
}

async function drainSse(response) {
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  for (;;) {
    const { value, done } = await reader.read();
    if (done) {
      break;
    }
    buffer += decoder.decode(value, { stream: true });
    let boundary = buffer.indexOf('\n\n');
    while (boundary !== -1) {
      handleSseBlock(buffer.slice(0, boundary));
      buffer = buffer.slice(boundary + 2);
      boundary = buffer.indexOf('\n\n');
    }
  }
  buffer += decoder.decode();
  if (buffer.trim()) {
    handleSseBlock(buffer);
  }
}

async function forward(message) {
  const headers = {
    'content-type': 'application/json',
    accept: 'application/json, text/event-stream',
  };
  if (sessionId) {
    headers['mcp-session-id'] = sessionId;
  }

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), TIMEOUT_MS);
  try {
    let response;
    try {
      response = await fetch(ENDPOINT, {
        method: 'POST',
        headers,
        body: JSON.stringify(message),
        signal: controller.signal,
      });
    } catch (error) {
      const reason =
        error && error.name === 'AbortError'
          ? `no answer from ${ENDPOINT} within ${TIMEOUT_MS} ms`
          : `AgentBridge is not reachable at ${ENDPOINT} - start AgentBridge (agent on Linux/macOS, agent.exe on Windows) and retry`;
      log(reason);
      replyError(idOf(message), -32603, reason);
      return;
    }

    const negotiated = response.headers.get('mcp-session-id');
    if (negotiated) {
      sessionId = negotiated;
    }

    if (response.status === 202 || response.status === 204) {
      await response.arrayBuffer();
      return;
    }

    if (!response.ok) {
      const body = await response.text().catch(() => '');
      const reason = `AgentBridge answered HTTP ${response.status}${body ? `: ${body.slice(0, 500)}` : ''}`;
      log(reason);
      replyError(idOf(message), -32603, reason);
      return;
    }

    const contentType = (response.headers.get('content-type') || '').toLowerCase();
    if (contentType.includes('text/event-stream')) {
      await drainSse(response);
      return;
    }

    const text = await response.text();
    if (text.trim()) {
      emit(text);
    }
  } finally {
    clearTimeout(timer);
  }
}

function handleLine(line) {
  const text = line.trim();
  if (!text) {
    return;
  }

  let message;
  try {
    message = JSON.parse(text);
  } catch {
    log('received malformed JSON on stdin');
    emit({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' } });
    return;
  }

  forward(message).catch((error) => {
    const reason = `bridge failure: ${error && error.message ? error.message : String(error)}`;
    log(reason);
    replyError(idOf(message), -32603, reason);
  });
}

function main() {
  log(`bridging stdio MCP to ${ENDPOINT}`);
  const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
  input.on('line', handleLine);
  input.on('close', () => {
    // Never process.exit() from here: on Windows it trips a libuv assertion while the
    // stdin handle is still closing. Once stdin ends and no request is in flight the
    // event loop empties on its own, so the host sees a clean exit code 0.
    log('stdin closed');
  });
}

process.on('SIGTERM', () => process.exit(0));
process.on('SIGINT', () => process.exit(0));

main();
