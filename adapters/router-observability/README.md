# Router observability adapter

This adapter adds bounded request metadata around the existing router without modifying `router.mjs`. It is optional and does not change SSE bodies, request bodies, streaming, or backpressure.

## RuntimeService integration

`RuntimeService.StartRouterAsync` looks for `adapters/router-observability/start-managed-router.ps1` under `AppContext.BaseDirectory` and then walks upward through development paths. If that script is present, it is preferred over the existing `launch-router.ps1` and a new process is started with `-UseObserver`. The service does not stop or restart a router that already answers `HTTP 200` on `/health`.

`start-managed-router.ps1` reads `runtime.json` for the Node executable and resolves API-key providers in this order:

1. `.codex/secrets/control-center-<providerId>.dpapi` for that provider.
2. The user and current-process values of the configured `key_env`.
3. Legacy fallbacks for the existing router layout: first provider key source, `router/key-b.dpapi`, or `router/key-mimo.dpapi`.

New providers may use a unique `key_env` such as `CODEX_CC_<PROVIDER_ID_UPPERCASE>`. The script reads root-level `host`/`port` first and rejects any host other than `127.0.0.1`. It does not print credential values, does not modify `launch-router.ps1`, and does not terminate arbitrary PIDs.

## Enabling the observer

Normal startup:

```powershell
pwsh.exe -NoProfile -File .\adapters\router-observability\start-managed-router.ps1 `
  -RouterHome <router-home> `
  -ConfigPath <router-home>\router.json `
  -CodexHome <codex-home>
```

Start the same router through the observer:

```powershell
pwsh.exe -NoProfile -File .\adapters\router-observability\start-managed-router.ps1 `
  -RouterHome <router-home> `
  -ConfigPath <router-home>\router.json `
  -CodexHome <codex-home> `
  -UseObserver
```

A router that is already healthy is left untouched. Enabling or disabling the observer therefore requires a planned restart at a safe time; do not stop it while requests are active. The adapter never performs that restart automatically.

Manual invocation is also supported:

```powershell
node .\adapters\router-observability\observer.mjs `
  --router <router-home>\router.mjs `
  --config <router-home>\router.json `
  --events <router-home>\events.ndjson `
  --max-bytes 2097152 `
  --backups 3
```

## Metadata and limits

The installed `hot-router.mjs` wrapper associates each request with metadata reported by the router engine using AsyncLocalStorage. That path records model/provider identity for sequential, concurrent and cancelled requests without parsing request bodies. Health and model-list probes are excluded from inference records. Standalone observer invocation below lacks that engine integration and retains null metadata when it cannot identify a model; historical records are not retroactively joined.

Each completed or cancelled request adds one NDJSON record with `request_id`, start `time`, measured `total_ms`, `status`, and `cancelled`. The observer does not read request bodies. It therefore writes `model: null` and `provider: null` instead of guessing values. Existing router stdout entries can contain `model_alias` and `provider`, but they do not provide a reliable lifecycle correlation key, so the UI parser must show missing values as `未采集`.

The default event file limit is 2 MiB with three rotated backups. Rotation and logging are best effort: write failures are swallowed and cannot interrupt forwarding. `RuntimeService.ReadRecentRequests` reads only a bounded tail, prefers `events.ndjson` when it has records, and otherwise reads recent `router*.stdout.log` files without inventing durations.

## Tests

Run the adapter tests:

```powershell
node --test .\adapters\router-observability\tests\observer.test.mjs
pwsh.exe -NoProfile -File .\adapters\router-observability\tests\start-managed-router.test.ps1
```

The tests use a local mock router and synthetic DPAPI data. They verify byte-for-byte mock SSE forwarding, request-body preservation, log-failure isolation, cancellation metadata, bounded rotation, and credential-free validation output. No paid API is called.

The credential helper returns a structured per-provider map plus legacy flat credential names on its private pipe. This lets a running older wrapper reload configuration while waiting for a safe process upgrade. Fingerprint-only responses contain neither credential representation. The current wrapper isolates unavailable sources and reports `credential_status`, `config_synced`, `providers_ready`, and unavailable provider/route lists through local health; these report loading, not upstream account permissions or balance.


# Custom tool compatibility (2026-10-30)

`custom-tool-compat.mjs` repairs upstream Responses results that return a declared custom
tool as `function_call` with `{"input":"raw source"}`. Both JSON and SSE restore
`custom_tool_call`, custom input delta/done events, and the original call IDs. Tool names
come only from the current request; ordinary functions and already-native custom calls
remain unchanged. The adapter never executes code or changes tool permissions.

Validate with `node --test adapters/router-observability/tests/custom-tool-compat.test.mjs`
and the installed-runtime / real-engine-metadata regressions. Tests use local synthetic
upstreams. Health advertises `features.custom_tool_compat` only in the updated process.
Install with `scripts/install-router-runtime.ps1`; activation may use
`scripts/activate-router-runtime-when-idle.ps1 -RequiredFeature custom_tool_compat` with
an explicit report path. A successful disk install is not proof of active process support.


## Legacy Responses Lite tool catalogs

Guard revision 6 treats a standard Responses top-level `tools` array as the current catalog. It omits old `input` items of type `additional_tools` when forwarding that request, preventing the legacy Responses Lite catalog from registering the same tools twice after a model switch. Actual messages, calls, call outputs, search outputs and call IDs remain unchanged. A Responses Lite request without top-level tools is left intact, and old tools are never promoted back into the current catalog.

Health exposes only bounded structural counts in `runtime.tool_structure`: legacy items, overlap counts, normalized requests and duplicate-name errors. It retains no tool names, schemas, parameters, message contents or credentials. Error responses are forwarded without retries. Install the runtime and confirm revision 6 after an idle activation; a file copy alone does not update an already running Node process.
