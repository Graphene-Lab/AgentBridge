#!/usr/bin/env python3
"""Streams an AgentBridge Windows Store MSI from GitHub Releases.

Microsoft Partner Center (win32 EXE/MSI products) requires the package URL to
answer HTTP 200 WITHOUT redirects and the binary behind it to stay frozen for
that URL. GitHub release download URLs always 302 to a signed CDN URL, so this
daemon performs that hop internally and streams the body through, presenting two
stable local URLs:

    https://aitechnology.it/agentbridge/msi/<version>  -> MSI of release tag v<version>
    https://aitechnology.it/agentbridge/msi            -> MSI of the LATEST release

Store submissions must use the versioned form: a release tag's asset never
changes, while /msi follows every new release (policy 10.2.9 requires a
versioned URL whose binary does not change after submission). The response
relays the upstream Content-Length and Range support so the Store download
manager can size, resume and verify the ~1.3 GB installer — a chunked response
with no length and no ranges made the 2026-09 certification fail with policy
10.3.4 "the product failed to install through the Store".

No file is stored on this box (disk is tight): every request resolves the tag
and pipes the MSI bytes through in chunks.

Endpoints:
    GET|HEAD /msi           -> MSI of the latest release (200, octet-stream)
    GET|HEAD /msi/<version> -> MSI of release tag v<version>, immutable
    GET /healthz            -> "ok"
"""
import http.server
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

REPO = "Graphene-Lab/AgentBridge"
PREFIX = "GrapheneAgentBridge-"
PORT = 8686
CHUNK = 256 * 1024
CACHE_SECONDS = 300  # /releases/latest is resolved at most once per 5 min per process
VERSION_PATH = re.compile(r"^/msi/v?([0-9]+(?:\.[0-9]+)+)$")

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


def _asset(version):
    """(asset url, tag) for a pinned version, or for the latest release if None."""
    tag = ("v" + version) if version else _latest_tag()
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
        self._serve(body=True)

    def do_HEAD(self):
        self._serve(body=False)

    def _serve(self, body):
        path = urllib.parse.urlparse(self.path).path
        if path == "/healthz":
            self._text(200, "ok")
            return
        if path in ("/", "/msi"):
            version = None
        else:
            m = VERSION_PATH.match(path)
            if not m:
                self._text(404, "proxy: unknown path %s — use /msi or /msi/<version>" % path)
                return
            version = m.group(1)
        try:
            url, tag = _asset(version)
            # Open the upstream FIRST (follows the GitHub 302 chain): only answer
            # 200 once the asset is confirmed reachable, so a missing installer
            # (e.g. between release creation and MSI upload) is an honest error
            # instead of a 200 with an empty body.
            req = urllib.request.Request(url)
            rng = self.headers.get("Range")
            if rng:
                req.add_header("Range", rng)
            up = urllib.request.urlopen(req, timeout=60)
        except urllib.error.HTTPError as e:
            self._text(e.code, "proxy: GitHub returned %s for %s" % (e.code, url))
            return
        except Exception as e:
            self._text(502, "proxy: cannot reach the release asset: %s" % e)
            return
        try:
            name = "%s%s.msi" % (PREFIX, tag.lstrip("v"))
            self.send_response(up.status)  # 206 when the client asked for a range
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Disposition", 'attachment; filename="%s"' % name)
            self.send_header("Cache-Control", "no-store")
            # Relay the upstream length/range headers: a close-delimited body with
            # no Content-Length cannot be sized or resumed by the Store downloader.
            for h in ("Content-Length", "Content-Range", "Accept-Ranges"):
                v = up.headers.get(h)
                if v:
                    self.send_header(h, v)
            self.end_headers()
            if not body:
                return
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
        finally:
            up.close()


if __name__ == "__main__":
    http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
