import json
from pathlib import Path


target_path = Path(__file__).resolve().parents[1] / "production-target.json"
target = json.loads(target_path.read_text(encoding="utf-8"))

required = {
    "region",
    "operatingSystem",
    "ramGiB",
    "apiDomain",
    "apiOrigin",
    "n8nDomain",
    "n8nOrigin",
    "publicPorts",
    "internalLoopbackPorts",
    "databasePlacement",
}
missing = required.difference(target)
assert not missing, f"missing production target fields: {sorted(missing)}"
assert target["operatingSystem"] == "Ubuntu 22.04 LTS"
assert target["ramGiB"] >= 8
assert target["apiDomain"] == "api.zibashe.ir"
assert target["n8nDomain"] == "n8n.zibashe.ir"
assert target["apiDomain"] != target["n8nDomain"]
assert target["apiOrigin"] == f'https://{target["apiDomain"]}'
assert target["n8nOrigin"] == f'https://{target["n8nDomain"]}'
assert set(target["publicPorts"]) == {22, 80, 443}
assert set(target["internalLoopbackPorts"]) == {5678, 8080}
assert target["databasePlacement"] in {"pending", "local", "external"}

forbidden_key_fragments = (
    "password",
    "secret",
    "token",
    "credential",
    "connectionstring",
    "username",
    "ipaddress",
)
for key in target:
    normalized = key.lower().replace("_", "")
    assert not any(value in normalized for value in forbidden_key_fragments), (
        f"production inventory must not contain sensitive key: {key}"
    )

print("Production target test: PASS")
