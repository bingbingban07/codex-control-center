# Image provider dispatcher

`image_dispatch.py` is the single entry point for Codex image generation. It reads the managed profile file at:

`<codex-home>/control-center/image-profiles.json`

The active source is `active_provider`; no provider switch is inferred from a failed request. The supported providers are `i2m` (the default 114514 helper) and `official` (OpenAI Images API).

## Profile file

The managed JSON shape is:

```json
{
  "active_provider": "i2m",
  "profiles": {
    "i2m": {
      "provider": "i2m",
      "base_url": "https://sub.114514.one/v1",
      "model": "gpt-image-2.5-sunburst",
      "size": "1024x1024",
      "quality": "high",
      "format": "png",
      "timeout": 600,
      "credential_file": "C:/Users/<user>/.codex/secrets/control-center-image-i2m.dpapi"
    },
    "official": {
      "provider": "official",
      "base_url": "https://api.openai.com/v1",
      "model": "gpt-image-2.5-sunburst",
      "size": "1024x1024",
      "quality": "high",
      "format": "png",
      "timeout": 600,
      "credential_file": "C:/Users/<user>/.codex/secrets/control-center-image-official.dpapi"
    }
  }
}
```

Fields are snake_case in the file and map to `ImageProfile` properties. `credential_file` is only a reference to a Windows current-user DPAPI `.dpapi` file. The management UI saves the encrypted key. Agents must never read or expose plaintext; the dispatcher decrypts the current-user DPAPI blob only in memory immediately before an actual request. It never uses Plus/ChatGPT authentication.

## Command line

```text
python image_dispatch.py --codex-home <path> (--prompt TEXT | --prompt-file FILE)
                         --out OUTPUT.png
                         [--image INPUT.png ...]
                         [--size WIDTHxHEIGHT|auto]
                         [--quality low|medium|high|xhigh|max|auto]
                         [--format png|jpeg|webp]
                         [--timeout SECONDS]
                         [--dry-run]
```

`--dry-run` prints the request description and does not decrypt a credential or contact an API. The active profile supplies defaults; the optional flags only override request parameters.

For `i2m`, the dispatcher calls the existing helper at
`<codex-home>/skills/i2m-imagegen/scripts/image_api.py` and passes the matching
`--prompt`/`--prompt-file`, `--out`, `--image`, `--size`, `--quality`, `--format`, and
`--timeout` arguments. It does not retry or switch providers.

Official parameters and the default `gpt-image-2.5-sunburst` model were checked directly against the [OpenAI Image generation guide](https://developers.openai.com/api/docs/guides/image-generation); the official default is not inherited from the i2m config. For `official`, it makes one POST to `https://api.openai.com/v1/images/generations`
or `/images/edits`, with no redirects and no retries. The response must contain one
`b64_json` image. Output bytes are checked for PNG/JPEG/WebP signatures, maximum
size, and actual dimensions; a dimension mismatch is preserved and reported.

## Safety constraints

- The source is selected only from `active_provider` and the user's current request.
- Never silently switch provider or model.
- Never automatically retry a paid request after timeout or uncertain failure.
- Agents must not read or expose plaintext credentials. The script may decrypt DPAPI only in memory during an actual request.
- After generation, inspect and display the actual image. Unrelated generation is not automatically authorized.
- User-supplied provider choices in the current request take precedence over defaults.
- The i2m skill instructions remain applicable when that provider is selected.



