# ANet fork of Czarnak/tia-portal-mcp

Private working fork for Honza (ESARoboTech / CZE-SA). Upstream: https://github.com/Czarnak/tia-portal-mcp (MIT, (c) Czarnak — LICENSE kept unchanged).
Goal: let an AI session *see* everything Openness V21 can expose, read-only by default. Plan: vault note `TIACONNECT — plán forku Czarnak (2026-09-30)`.

## Changes vs upstream
| Version | Change | Upstream PR candidate |
|---|---|---|
| 3.0.1-anet.1 | Hardware enumeration also walks `Project.UngroupedDevicesGroup` (decentral ET 200SP / GSD / switches were invisible: "No device named …"). Locator `ungroupedDevices/{i}`, owner-location code `ungrouped`. | yes |
| 3.0.1-anet.2 | New read tool `export_to_folder`: exports blocks (.s7dcl/.s7res documents, .scl/.db source, SimaticML .xml fallback), PLC types (.udt / .xml) and tag tables (.xml) of one PLC under `--export-root` / `TIA_MCP_EXPORT_ROOT`, chunked by a time budget (runId + nextOffset), `manifest.jsonl` per run. Classified TemporaryExport (allowed in read-only; never writes the project). Software units not yet covered. | maybe |

## Build on Haiku
Double-click `anet-build.cmd` (needs .NET SDK >= 10.0.400, TIA Portal V21 installed). Log: `anet-build.log`. Version from `anet-version.txt`.
