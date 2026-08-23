#!/usr/bin/env bash
# Giraffe AI Launcher - Linux / macOS
#
# Serves the chat (index.html) on http://localhost:8000/ and proxies /v1/*
# to the local LLM at http://127.0.0.1:8787 (OpenAI-compatible, no API key).
# The browser talks to the same origin, so no CORS and no insecure flags.
#
# No external dependencies: uses python3 when available; on macOS it falls
# back to the Ruby that ships with the system. Both servers are implemented
# inline below (same role as the PowerShell server inside start.bat).

set -u

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PORT=8000
BACKEND_HOST="127.0.0.1"
BACKEND_PORT=8787
URL="http://localhost:${PORT}/"
SERVER_PID=""

# ---------- Parsing argomenti ----------
# --provider <json> (raw JSON, e.g. from AgentBridge) or an already URL-encoded value:
# the JSON is appended to the opened URL as ?provider=<url-encoded JSON> and the client
# registers/selects that provider on load (see index.html init()).
AUTO_PROVIDER=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --provider)
      AUTO_PROVIDER="$2"
      shift 2
      ;;
    *)
      echo "Parametro sconosciuto: $1"
      exit 1
      ;;
  esac
done

if [ -n "$AUTO_PROVIDER" ]; then
  if [[ "$AUTO_PROVIDER" == \{* ]]; then
    # Raw JSON: URL-encode it with python3 or ruby (same interpreters used by the server).
    if command -v python3 >/dev/null 2>&1; then
      ENCODED_PROVIDER=$(printf '%s' "$AUTO_PROVIDER" | python3 -c "import sys,urllib.parse; print(urllib.parse.quote(sys.stdin.read()))")
    elif command -v ruby >/dev/null 2>&1; then
      ENCODED_PROVIDER=$(printf '%s' "$AUTO_PROVIDER" | ruby -r uri -e 'puts URI.encode_www_form_component(STDIN.read)')
    else
      echo "ERRORE: nessun interprete disponibile per codificare il JSON (python3 o ruby)." >&2
      exit 1
    fi
  else
    # Already URL-encoded: use as-is.
    ENCODED_PROVIDER="$AUTO_PROVIDER"
  fi
  URL="${URL}?provider=${ENCODED_PROVIDER}"
fi

# ---------- [0] OS detection ----------
case "$(uname -s)" in
  Darwin) OS_NAME="macos" ;;
  *)      OS_NAME="linux" ;;
esac

echo "========================================"
echo " GIRAFFE AI - LAUNCHER (${OS_NAME})"
echo "========================================"
echo

# ---------- helpers ----------
port_in_use() {
  (exec 3<>"/dev/tcp/${BACKEND_HOST}/${PORT}") 2>/dev/null && { exec 3>&- 3<&-; return 0; }
  return 1
}

# ---------- [1/4] port check ----------
echo "[1/4] Checking port ${PORT}..."
if port_in_use; then
  echo "PORT ${PORT} ALREADY IN USE - Server already running"
else
  echo "PORT ${PORT} FREE - Starting server..."
fi

# ---------- [2/4] start server ----------
start_server() {
  if command -v python3 >/dev/null 2>&1; then
    echo "[2/4] Starting server (python3)..."
    python3 - "$SCRIPT_DIR" "$PORT" <<'PY' &
import http.client, os, signal, sys, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, unquote

ROOT = sys.argv[1]
PORT = int(sys.argv[2])
BACKEND_HOST = '127.0.0.1'
BACKEND_PORT = 8787
FORWARD_HEADERS = ('content-type', 'authorization', 'accept')


class H(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, fmt, *args):
        pass

    def do_GET(self):
        self.route('GET')

    def do_POST(self):
        self.route('POST')

    def route(self, method):
        u = urlparse(self.path)
        path = unquote(u.path)
        if path.startswith('/v1/'):
            self.proxy(method, path, u)
        else:
            self.static(path)

    def static(self, path):
        if path == '/':
            path = '/index.html'
        root = os.path.normpath(ROOT)
        full = os.path.normpath(os.path.join(ROOT, path.lstrip('/')))
        if full != root and not full.startswith(root + os.sep):
            self.send_error(403)
            return
        if not os.path.isfile(full):
            body = b'<html><body><h1>404 - File not found</h1></body></html>'
            self.send_response(404)
            self.send_header('Content-Type', 'text/html')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        with open(full, 'rb') as f:
            data = f.read()
        self.send_response(200)
        self.send_header('Content-Type', 'text/html')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def proxy(self, method, path, u):
        length = int(self.headers.get('Content-Length', 0) or 0)
        body = self.rfile.read(length) if length else None
        headers = {k: v for k, v in self.headers.items() if k.lower() in FORWARD_HEADERS}
        target = path + (('?' + u.query) if u.query else '')
        conn = http.client.HTTPConnection(BACKEND_HOST, BACKEND_PORT, timeout=600)
        try:
            conn.request(method, target, body=body, headers=headers)
            resp = conn.getresponse()
            self.send_response(resp.status)
            self.send_header('Content-Type', resp.getheader('Content-Type') or 'application/json')
            self.send_header('Transfer-Encoding', 'chunked')
            self.end_headers()
            try:
                while True:
                    chunk = resp.read(8192)
                    if not chunk:
                        break
                    self.wfile.write(('%x\r\n' % len(chunk)).encode('ascii') + chunk + b'\r\n')
                    self.wfile.flush()
            finally:
                self.wfile.write(b'0\r\n\r\n')
                self.wfile.flush()
        finally:
            conn.close()


server = ThreadingHTTPServer(('127.0.0.1', PORT), H)


def _stop(sig, frame):
    threading.Thread(target=server.shutdown, daemon=True).start()


signal.signal(signal.SIGTERM, _stop)
signal.signal(signal.SIGINT, _stop)
server.serve_forever()
PY
  elif [ "$OS_NAME" = "macos" ] && command -v ruby >/dev/null 2>&1; then
    echo "[2/4] Starting server (ruby, macOS built-in)..."
    ruby - "$SCRIPT_DIR" "$PORT" <<'RB' &
require 'webrick'
require 'socket'

ROOT = File.expand_path(ARGV[0])
PORT = Integer(ARGV[1])
BACKEND = '127.0.0.1'
BACKEND_PORT = 8787

class ChunkedReader
  def initialize(sock, seed = '')
    @sock = sock
    @buf = seed.dup
  end

  def read_line
    @buf << @sock.readpartial(16384) until (i = @buf.index("\r\n"))
    line = @buf[0...i]
    @buf = @buf[(i + 2)..-1] || +''
    line
  end

  def read_exact(n)
    @buf << @sock.readpartial(16384) while @buf.bytesize < n
    data = @buf[0...n]
    @buf = @buf[n..-1] || +''
    data
  end

  # prossimo chunk decodificato; nil a fine stream
  def next_chunk
    size = read_line.to_i(16)
    return nil if size.zero?
    data = read_exact(size)
    read_line
    data
  end
end

class Handler < WEBrick::HTTPServlet::AbstractServlet
  def service(req, res)
    if req.path.start_with?('/v1/')
      proxy(req, res)
    else
      static(req, res)
    end
  end

  def static(req, res)
    path = req.path == '/' ? '/index.html' : req.path
    full = File.expand_path(File.join(ROOT, path))
    if full.start_with?(ROOT + File::SEPARATOR) && File.file?(full)
      res.status = 200
      res['Content-Type'] = 'text/html'
      res.body = File.binread(full)
    else
      res.status = 404
      res['Content-Type'] = 'text/html'
      res.body = '<html><body><h1>404 - File not found</h1></body></html>'
    end
  end

  def proxy(req, res)
    body = req.body.to_s
    request_line = "#{req.request_method} #{req.path} HTTP/1.1\r\n"
    headers = "Host: #{BACKEND}:#{BACKEND_PORT}\r\n"
    headers << "Content-Type: #{req['content-type'] || 'application/json'}\r\n"
    headers << "Content-Length: #{body.bytesize}\r\n" unless body.empty?
    headers << "Connection: close\r\n\r\n"
    begin
      sock = TCPSocket.new(BACKEND, BACKEND_PORT)
    rescue StandardError
      res.status = 502
      res['Content-Type'] = 'application/json'
      res.body = '{"error":"backend unreachable"}'
      return
    end
    sock.write(request_line + headers)
    sock.write(body) unless body.empty?
    head = +''
    loop do
      head << sock.readpartial(16384)
      break if head.include?("\r\n\r\n")
    end
    sep = head.index("\r\n\r\n")
    rest = head[(sep + 4)..-1] || ''
    head = head[0...sep]
    res.status = head[/\AHTTP\/1\.[01] (\d+)/, 1].to_i
    res['Content-Type'] = head[/\r\nContent-Type: ([^\r\n]+)/i, 1] || 'text/event-stream'
    chunked = !!(head =~ /transfer-encoding:\s*chunked/i)
    if chunked
      res.chunked = true
    else
      len = head[/\r\ncontent-length:\s*(\d+)/i, 1]
      res['Content-Length'] = len if len
    end
    r, w = IO.pipe
    Thread.new do
      begin
        if chunked
          cr = ChunkedReader.new(sock, rest)
          while (chunk = cr.next_chunk)
            w.write(chunk)
          end
        else
          w.write(rest) unless rest.empty?
          loop { w.write(sock.readpartial(16384)) }
        end
      rescue EOFError, IOError, Errno::ECONNRESET, Errno::EPIPE
      ensure
        begin; sock.close; rescue StandardError; end
        w.close
      end
    end
    res.body = r
  end
end

server = WEBrick::HTTPServer.new(
  BindAddress: '127.0.0.1',
  Port: PORT,
  Logger: WEBrick::Log.new('/dev/null'),
  AccessLog: []
)
server.mount('/', Handler)
trap('TERM') { server.shutdown }
trap('INT') { server.shutdown }
server.start
RB
  else
    echo "ERROR: no python3 found. Install python3 (Linux) or Xcode Command Line Tools (macOS) and retry." >&2
    exit 1
  fi
  SERVER_PID=$!
}

if ! port_in_use; then
  start_server
  i=0
  while [ "$i" -lt 40 ]; do
    port_in_use && break
    sleep 0.25
    i=$((i + 1))
  done
  if ! port_in_use; then
    echo "ERROR: server did not start on port ${PORT}." >&2
    exit 1
  fi
  echo "SERVER STARTED ON PORT ${PORT} (pid ${SERVER_PID})"
else
  echo "[2/4] Server already listening - skipping start"
fi

# ---------- [3/4] open browser ----------
echo
echo "[3/4] Opening browser at ${URL}..."
if [ "$OS_NAME" = "macos" ]; then
  if command -v open >/dev/null 2>&1; then
    open "$URL"
  else
    echo "No 'open' command found. Open this URL manually: ${URL}"
  fi
else
  if command -v xdg-open >/dev/null 2>&1; then
    xdg-open "$URL" >/dev/null 2>&1 &
  elif command -v x-www-browser >/dev/null 2>&1; then
    x-www-browser "$URL" >/dev/null 2>&1 &
  elif command -v sensible-browser >/dev/null 2>&1; then
    sensible-browser "$URL" >/dev/null 2>&1 &
  elif command -v google-chrome >/dev/null 2>&1; then
    google-chrome --app="$URL" >/dev/null 2>&1 &
  elif command -v chromium >/dev/null 2>&1; then
    chromium --app="$URL" >/dev/null 2>&1 &
  elif command -v firefox >/dev/null 2>&1; then
    firefox "$URL" >/dev/null 2>&1 &
  else
    echo "No browser launcher found. Open this URL manually: ${URL}"
  fi
fi

# ---------- [4/4] done ----------
echo
echo "[4/4] Server running at ${URL}"
if [ -n "$SERVER_PID" ]; then
  echo "      (server pid ${SERVER_PID}; stop it with: kill ${SERVER_PID})"
fi
echo
