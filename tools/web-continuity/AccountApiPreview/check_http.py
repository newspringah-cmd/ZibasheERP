"""Exercise the isolated .NET preview over loopback HTTPS with two synthetic accounts."""
import http.client
import json
import queue
import ssl
import subprocess
import sys
import threading
from pathlib import Path
from urllib.parse import urlsplit


def verify(dotnet):
    assembly = Path(__file__).resolve().parent / "bin/Release/net10.0/AccountApiPreview.dll"
    process = subprocess.Popen([dotnet, str(assembly)], stdout=subprocess.PIPE, text=True)
    lines = queue.Queue()
    def read_output():
        for line in process.stdout:
            lines.put(line.rstrip())

    thread = threading.Thread(target=read_output, daemon=True)
    thread.start()
    try:
        ready = lines.get(timeout=20)
        origin = ready.removeprefix("PREVIEW_URL ")
        target = urlsplit(origin)
        assert ready.startswith("PREVIEW_URL ") and target.scheme == "https" and target.hostname == "127.0.0.1"
        # The preview creates an ephemeral self-signed certificate. Trust relaxation is local to this client.
        tls = ssl._create_unverified_context()

        def request(method, path, body=None, cookie=None, source=None):
            headers = {}
            if method == "POST":
                headers["Origin"] = source if source is not None else origin
            if body is not None:
                body = json.dumps(body)
                headers["Content-Type"] = "application/json"
            if cookie:
                headers["Cookie"] = cookie
            conn = http.client.HTTPSConnection(target.hostname, target.port, context=tls, timeout=5)
            try:
                conn.request(method, path, body, headers)
                reply = conn.getresponse()
                raw = reply.read()
                assert reply.getheader("Cache-Control") == "no-store"
                data = json.loads(raw) if raw else None
                return reply.status, reply.getheader("Set-Cookie"), data
            finally:
                conn.close()

        start = "/api/v2/session/sms/start"
        confirm = "/api/v2/session/sms/verify"
        logout = "/api/v2/session/logout"
        invoices = "/api/v2/me/invoices"
        assert request("GET", invoices)[0] == 401
        assert request("POST", start, {"mobile": "09000000001"}, source="https://example.invalid")[0] == 403
        assert request("POST", start, {"mobile": "09000000001"}, source="")[0] == 403
        assert request("POST", start, {"mobile": "invalid"})[0] == 400

        def sign_in(mobile):
            status, cookie, challenge = request("POST", start, {"mobile": mobile})
            assert status == 202 and cookie is None
            assert set(challenge) == {"challengeId", "retryAfterSeconds"}
            _, destination, code = lines.get(timeout=5).split()
            assert destination == mobile
            payload = {"challengeId": challenge["challengeId"], "code": code}
            assert request("POST", confirm, {**payload, "code": "bad"})[0] == 401
            status, issued, body = request("POST", confirm, payload)
            assert status == 204 and body is None and issued
            for attribute in ["secure", "httponly", "samesite=strict", "path=/", "max-age=900"]:
                assert attribute in issued.lower()
            assert "domain=" not in issued.lower()
            assert request("POST", confirm, payload)[0] == 401
            return issued.split(";", 1)[0]

        first, second = sign_in("09000000001"), sign_in("09000000002")
        assert first != second
        assert request("POST", start, {"mobile": "09000000001"})[0] == 429
        unknown = request("POST", start, {"mobile": "09000000003"})
        assert unknown[0] == 202 and set(unknown[2]) == {"challengeId", "retryAfterSeconds"}
        assert lines.empty()
        for cookie, number, due in [(first, "PREVIEW-001", "150000"), (second, "PREVIEW-002", "300000")]:
            status, _, result = request("GET", invoices + "?customerId=41000000-0000-0000-0000-000000000002", cookie=cookie)
            assert status == 200 and [row["number"] for row in result["invoices"]] == [number]
            assert result["summary"]["outstanding"] == due
            assert result["invoices"][0]["lines"][0]["volumeMl"] == "1.5"
        assert request("GET", invoices + "?pageSize=51", cookie=first)[0] == 400
        assert request("GET", invoices, cookie="__Host-ZibashePreview=" + "A" * 64)[0] == 401
        assert request("POST", logout, cookie=first, source="https://example.invalid")[0] == 403
        assert request("GET", invoices, cookie=first)[0] == 200
        status, expired, _ = request("POST", logout, cookie=first)
        assert status == 204 and "expires=" in expired.lower()
        assert request("GET", invoices, cookie=first)[0] == 401
        assert request("GET", invoices, cookie=second)[0] == 200
        print("HTTPS preview passed: OTP, cookie, account isolation, origin checks, paging and session revocation")
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        thread.join(timeout=5)
        process.stdout.close()


if __name__ == "__main__":
    verify(sys.argv[1] if len(sys.argv) == 2 else "dotnet")
