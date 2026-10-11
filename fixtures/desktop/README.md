# Desktop fixtures

Synthetic only. No private historical packets.

- `scenarios/*.json` — deterministic preview scenarios listed in the work order.
- `archives/valid-hash-consistent` — supported `desktop-archive/v1` packet.
- `archives/tampered-hash` — declared digest does not match bytes.
- `archives/unsupported-schema` — refused.
- `archives/path-escape` — `../` refused.

Regenerate: `python scripts/generate_fixtures.py`
