#!/usr/bin/env node
/**
 * Smoke test for the MCP stdio bridge (mcp/index.js, or the copy inside a built .mcpb).
 *
 * It starts a stub MCP server in-process, then runs the bridge as a child process the way a
 * host does, and asserts the three things a host depends on: one JSON response per request
 * on stdout, nothing on stdout for a notification, and a clean exit when stdin closes.
 *
 *   node tools/mcp/smoke_test.js                    # tests mcp/index.js
 *   node tools/mcp/smoke_test.js /tmp/b/index.js    # tests the extracted bundle
 *
 * No dependencies: it must run on a bare GitHub runner, like the bundle itself.
 */

'use strict';

const http = require('http');
const path = require('path');
const { spawn } = require('child_process');

const target = process.argv[2] || path.join(__dirname, '..', '..', 'mcp', 'index.js');
const deadlineMs = 20000;

let failures = 0;

function check(condition, message) {
  if (condition) {
    console.log(`  ok   ${message}`);
  } else {
    console.error(`  FAIL ${message}`);
    failures++;
  }
}

function startStub() {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      let body = '';
      req.on('data', (chunk) => { body += chunk; });
      req.on('end', () => {
        const message = JSON.parse(body || '{}');
        if (message.method === 'tools/call' && message.params?.name === 'agent_run' && message.params?.arguments?.prompt === 'explode') {
          res.writeHead(500, { 'content-type': 'application/json' });
          res.end(JSON.stringify({ error: 'stub failure' }));
          return;
        }
        if (message.id === undefined) {
          res.writeHead(202).end();
          return;
        }
        res.writeHead(200, { 'content-type': 'application/json', 'mcp-session-id': 'smoke' });
        res.end(JSON.stringify({ jsonrpc: '2.0', id: message.id, result: { echo: message.method } }));
      });
    });
    server.listen(0, '127.0.0.1', () => {
      resolve({ server, port: server.address().port });
    });
  });
}

function runBridge(url, requests) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [target], {
      env: { ...process.env, AGENTBRIDGE_URL: url, AGENTBRIDGE_TIMEOUT_MS: '5000' },
      stdio: ['pipe', 'pipe', 'pipe']
    });

    let stdout = '';
    let stderr = '';
    const timer = setTimeout(() => {
      child.kill();
      reject(new Error(`bridge did not exit within ${deadlineMs} ms`));
    }, deadlineMs);

    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', (code) => {
      clearTimeout(timer);
      resolve({ code, stdout, stderr });
    });

    for (const request of requests) {
      child.stdin.write(JSON.stringify(request) + '\n');
    }
    child.stdin.end();
  });
}

async function main() {
  const { server, port } = await startStub();
  const url = `http://127.0.0.1:${port}/mcp`;

  try {
    console.log(`bridge: ${target}`);
    const result = await runBridge(url, [
      { jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } },
      { jsonrpc: '2.0', method: 'notifications/initialized' },
      { jsonrpc: '2.0', id: 2, method: 'tools/list' },
      { jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'agent_run', arguments: { prompt: 'explode' } } }
    ]);

    const lines = result.stdout.split('\n').filter((line) => line.trim() !== '');
    const parsed = lines.map((line) => JSON.parse(line));
    const byId = new Map(parsed.map((message) => [message.id, message]));

    check(result.code === 0, `exits with code 0 (got ${result.code})`);
    check(lines.length === 3, `writes one line per request id (got ${lines.length})`);
    check(byId.get(1)?.result?.echo === 'initialize', 'forwards initialize');
    check(byId.get(2)?.result?.echo === 'tools/list', 'forwards tools/list');
    check(byId.get(3)?.error?.code === -32603, 'turns an HTTP failure into a JSON-RPC error');
    check(!result.stdout.includes('notifications/initialized'), 'stays silent for a notification');
    check(result.stderr.includes('tools/call') || result.stderr.length > 0, 'logs diagnostics to stderr, not stdout');
  } finally {
    server.close();
  }

  if (failures > 0) {
    console.error(`\n${failures} check(s) failed`);
    process.exit(1);
  }
  console.log('\nbridge smoke test passed');
}

main().catch((error) => {
  console.error(`smoke test error: ${error.message}`);
  process.exit(1);
});
