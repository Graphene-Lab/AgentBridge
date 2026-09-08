#!/usr/bin/env python3
"""Streams the latest AgentBridge Windows Store MSI from GitHub Releases.

Microsoft Partner Center (win32 EXE/MSI products) requires the package URL to
answer HTTP 200 WITHOUT redirects. GitHub release download URLs always 302 to a
signed CDN URL, so this daemon performs that hop internally and streams the
body through, presenting a single stable local URL:

    https://aitechnology.it/agentbridge/msi

No file is stored on this box (disk is tight): every request resolves the
current "latest" AgentBridge release and pipes the MSI bytes through in chunks.

Resolution is redirect-based (GET /releases/latest with redirects disabled and
read the Location header), not the GitHub API, so there is no rate limit. The
asset name is our own release convention: GrapheneAgentBridge-<tag-without-v>.msi.

Endpoints:
    GET /          -> streams the MSI of the latest release (200, octet-stream)
    GET /healthz   -> "ok"
"""
import http.server
import re
import sys
import time
import urllib.error
import urllib.request

REPO = "Graphene-Lab/AgentBridge"
PREFIX = "GrapheneAgentBridge-"
PORT = 8686
CHUNK = 256 * 1024
CACHE_SECONDS = 300  # /releases/latest is resolved at most once per 5 min per process

_cache = {"tag": None, "at": 0.0}


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def _latest_tag():
    """Tag of the newest Graphene-Lab/AgentBridge release, cached briefly."""
    now = time.time()
    if _cache["tag"] and now - _cache["at"] < CACHE_SECONDS:
        return _cache["tag"]
    opener = urllib.request.build_opener(_NoRedirect)
    try:
        # With redirects disabled the 302 surfaces as HTTPError; its Location
        # header carries the tag: https://github.com/<repo>/releases/tag/<tag>.
        try:
            r = opener.open("https://github.com/%s/releases/latest" % REPO, timeout=30)
        except urllib.error.HTTPError as e:
            if e.code < 300 or e.code >= 400:
                raise
            loc = e.headers.get("Location") or ""
        else:
            with r:
                loc = r.headers.get("Location") or ""
        m = re.search(r"/releases/tag/([^/?#]+)$", loc)
        if not m:
            raise RuntimeError("unexpected /releases/latest Location: %r" % loc)
        _cache["tag"], _cache["at"] = m.group(1), now
        return _cache["tag"]
    except Exception:
        if _cache["tag"]:
            return _cache["tag"]  # serve the last known tag on a transient failure
        raise


def _msi_url():
    tag = _latest_tag()
    url = "https://github.com/%s/releases/download/%s/%s%s.msi" % (
        REPO, tag, PREFIX, tag.lstrip("v"))
    return url, tag


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def _text(self, code, body):
        data = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/healthz":
            self._text(200, "ok")
            return
        try:
            url, tag = _msi_url()
            # Open the upstream FIRST (follows the GitHub 302 chain): only answer
            # 200 once the asset is confirmed reachable, so a missing installer
            # (e.g. between release creation and MSI upload) is an honest error
            # instead of a 200 with an empty body.
            up = urllib.request.urlopen(url, timeout=60)
        except urllib.error.HTTPError as e:
            self._text(e.code, "proxy: GitHub returned %s for %s" % (e.code, url))
            return
        except Exception as e:
            self._text(502, "proxy: cannot reach the release asset: %s" % e)
            return
        name = "%s%s.msi" % (PREFIX, tag.lstrip("v"))
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Disposition", 'attachment; filename="%s"' % name)
        self.send_header("Cache-Control", "no-store")
        self.end_headers()  # no Content-Length: body is close-delimited (HTTP/1.0)
        try:
            with up:
                while True:
                    chunk = up.read(CHUNK)
                    if not chunk:
                        break
                    try:
                        self.wfile.write(chunk)
                    except (BrokenPipeError, ConnectionResetError):
                        return
        except Exception as e:
            # Headers already sent; just drop the connection to signal the error.
            print("proxy stream error: %s" % e, file=sys.stderr)
            try:
                self.connection.close()
            except Exception:
                pass
            return


if __name__ == "__main__":
    http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
