"""Unified Codex image-provider dispatcher. Standard library only; no retries."""
from __future__ import annotations

import argparse
import base64
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
import mimetypes
from pathlib import Path
import re
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


PROFILE_RELATIVE = Path("control-center") / "image-profiles.json"
I2M_HELPER_RELATIVE = Path("skills") / "i2m-imagegen" / "scripts" / "image_api.py"
DEFAULT_I2M_MODEL = "gpt-image-2.5-sunburst"
# Verified independently on the official OpenAI Image generation guide on 2026-10-26.
DEFAULT_OFFICIAL_MODEL = "gpt-image-2.5-sunburst"
DEFAULT_OFFICIAL_BASE = "https://api.openai.com/v1"
MAX_IMAGE_BYTES = 50 * 1024 * 1024
MAX_API_RESPONSE_BYTES = ((MAX_IMAGE_BYTES + 2) // 3) * 4 + 1024 * 1024
FORMAT_EXTENSIONS = {
    "png": {".png"},
    "jpeg": {".jpg", ".jpeg"},
    "webp": {".webp"},
}
IMAGE_MIME = {
    ".png": "image/png",
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".webp": "image/webp",
}


class DispatchError(RuntimeError):
    pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise DispatchError("API redirect refused; the dispatcher performs one request only.")


def canonical_provider(value: str | None) -> str:
    value = (value or "i2m").strip().lower()
    if value in {"i2m", "114514", "i2m/114514", "i2m-114514"}:
        return "i2m"
    if value in {"official", "openai", "openai-images"}:
        return "official"
    raise DispatchError(f"Unsupported image provider: {value!r}")


def first_value(mapping: dict, *names: str, default=None):
    for name in names:
        if name in mapping and mapping[name] is not None:
            return mapping[name]
    return default


def read_profile(codex_home: Path) -> tuple[str, dict]:
    codex_home = codex_home.expanduser().resolve()
    profile_path = codex_home / PROFILE_RELATIVE
    data = {}
    if profile_path.is_file():
        try:
            loaded = json.loads(profile_path.read_text(encoding="utf-8-sig"))
        except (OSError, UnicodeError, json.JSONDecodeError) as error:
            raise DispatchError(f"Cannot read image profiles: {error}") from None
        if not isinstance(loaded, dict):
            raise DispatchError("image-profiles.json must contain an object.")
        data = loaded

    raw_active = first_value(data, "active_provider", "activeProvider", default="official")
    active = canonical_provider(str(raw_active))
    profiles = first_value(data, "profiles", "providers", default={})
    if not isinstance(profiles, dict):
        raise DispatchError("image-profiles.json profiles must be an object.")

    selected = {}
    for key, value in profiles.items():
        try:
            if canonical_provider(str(key)) == active and isinstance(value, dict):
                selected = value
                break
        except DispatchError:
            continue

    if active == "i2m":
        config_path = codex_home / "skills" / "i2m-imagegen" / "config.json"
        config = {}
        if config_path.is_file():
            try:
                parsed = json.loads(config_path.read_text(encoding="utf-8-sig"))
                if isinstance(parsed, dict):
                    config = parsed
            except (OSError, UnicodeError, json.JSONDecodeError) as error:
                raise DispatchError(f"Cannot read the i2m image config: {error}") from None
            if not isinstance(parsed, dict):
                raise DispatchError("The i2m image config must contain an object.")
            config = parsed
        defaults = {
            "provider": "i2m",
            "base_url": first_value(config, "base_url", default="https://sub.114514.one/v1"),
            "model": first_value(config, "model", default=DEFAULT_I2M_MODEL),
            "size": first_value(config, "size", default="1024x1024"),
            "quality": first_value(config, "quality", default="high"),
            "format": first_value(config, "output_format", default="png"),
            "timeout": first_value(config, "timeout", default=600),
            "credential_file": first_value(config, "credential_file", default=""),
        }
    else:
        defaults = {
            "provider": "official",
            "base_url": DEFAULT_OFFICIAL_BASE,
            "model": DEFAULT_OFFICIAL_MODEL,
            "size": "1024x1024",
            "quality": "high",
            "format": "png",
            "timeout": 600,
            "credential_file": "",
        }

    merged = dict(defaults)
    aliases = {
        "provider": ("provider",),
        "base_url": ("base_url", "baseUrl", "BaseUrl"),
        "model": ("model", "Model"),
        "size": ("size", "Size"),
        "quality": ("quality", "Quality"),
        "format": ("format", "output_format", "outputFormat", "Format"),
        "timeout": ("timeout", "Timeout"),
        "credential_file": ("credential_file", "credentialFile", "CredentialFile"),
    }
    for field, names in aliases.items():
        value = first_value(selected, *names)
        if value is not None:
            merged[field] = value
    merged["provider"] = active
    return active, merged


def parse_size(value: str) -> tuple[int, int] | None:
    if value == "auto":
        return None
    match = re.fullmatch(r"([1-9][0-9]{0,4})x([1-9][0-9]{0,4})", value)
    if not match:
        raise DispatchError("Size must be auto or WIDTHxHEIGHT.")
    return int(match.group(1)), int(match.group(2))


def parse_timeout(value) -> int:
    if isinstance(value, bool) or not isinstance(value, (int, str)):
        raise DispatchError("Timeout must be an integer between 1 and 1800 seconds.")
    try:
        timeout = int(value)
    except ValueError:
        raise DispatchError("Timeout must be an integer between 1 and 1800 seconds.") from None
    if timeout < 1 or timeout > 1800:
        raise DispatchError("Timeout must be between 1 and 1800 seconds.")
    return timeout


def validate_request(profile: dict, prompt: str, out: Path, images: list[Path], timeout: int):
    if not prompt.strip():
        raise DispatchError("The prompt is empty.")
    if timeout < 1 or timeout > 1800:
        raise DispatchError("Timeout must be between 1 and 1800 seconds.")
    parse_size(str(profile["size"]))
    if str(profile["format"]) not in FORMAT_EXTENSIONS:
        raise DispatchError("Format must be png, jpeg, or webp.")
    if str(profile["quality"]) not in {"low", "medium", "high", "xhigh", "max", "auto"}:
        raise DispatchError("Unsupported quality value.")
    if profile["provider"] == "i2m" and str(profile["quality"]) == "xhigh":
        raise DispatchError("i2m quality xhigh is not supported by the existing image_api.py.")
    if out.suffix.lower() not in FORMAT_EXTENSIONS[str(profile["format"])]:
        raise DispatchError("The output extension does not match the requested format.")
    for path in images:
        suffix = path.suffix.lower()
        if suffix not in IMAGE_MIME:
            raise DispatchError("Reference images must be PNG, JPEG, or WebP.")
        try:
            if not path.is_file():
                raise DispatchError(f"An input image is not a regular file or does not exist: {path}")
            if path.stat().st_size > MAX_IMAGE_BYTES:
                raise DispatchError("An input image exceeds 50 MiB.")
            with path.open("rb") as handle:
                actual_format = image_signature(handle.read(12))
        except OSError as error:
            raise DispatchError(f"Cannot read an input image: {path}: {error}") from None
        expected_format = {".png": "png", ".jpg": "jpeg", ".jpeg": "jpeg", ".webp": "webp"}[suffix]
        if actual_format != expected_format:
            raise DispatchError(f"The input image content does not match its file extension: {path}")


def read_prompt_file(path: Path) -> str:
    path = path.expanduser()
    try:
        return path.read_text(encoding="utf-8-sig")
    except (OSError, UnicodeError) as error:
        raise DispatchError(f"Cannot read prompt file {path}: {error}") from None


def validate_credential_reference(value, codex_home: Path) -> Path:
    path = Path(str(value)).expanduser()
    if not path.is_absolute() or path.suffix.lower() != ".dpapi":
        raise DispatchError("CredentialFile must be an absolute .dpapi path under .codex/secrets.")
    try:
        path = path.resolve()
        roots = [
            (codex_home / "secrets").resolve(),
            (Path.home() / ".codex" / "secrets").resolve(),
        ]
    except (OSError, RuntimeError) as error:
        raise DispatchError(f"Cannot resolve CredentialFile: {error}") from None
    for root in roots:
        try:
            path.relative_to(root)
            return path
        except ValueError:
            continue
    raise DispatchError("CredentialFile must be under the Codex Home or user .codex/secrets directory.")


def validate_i2m_credential_reference(codex_home: Path) -> Path:
    config_path = codex_home / "skills" / "i2m-imagegen" / "config.json"
    try:
        config = json.loads(config_path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise DispatchError(f"Cannot read the i2m credential reference from {config_path}: {error}") from None
    if not isinstance(config, dict):
        raise DispatchError("The i2m image config must contain an object.")
    return validate_credential_reference(first_value(config, "credential_file", default=""), codex_home)


def prepare_output_directory(out: Path):
    try:
        out.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryFile(dir=out.parent, prefix=".codex-image-dispatch-"):
            pass
    except OSError as error:
        raise DispatchError(f"Cannot prepare output directory {out.parent}: {error}") from None
    if not out.parent.is_dir():
        raise DispatchError(f"Output parent is not a directory: {out.parent}")


def image_signature(raw: bytes) -> str | None:
    if raw.startswith(b"\x89PNG\r\n\x1a\n"):
        return "png"
    if raw.startswith(b"\xff\xd8\xff"):
        return "jpeg"
    if raw.startswith(b"RIFF") and raw[8:12] == b"WEBP":
        return "webp"
    return None


def image_dimensions(raw: bytes) -> tuple[int, int] | None:
    if raw.startswith(b"\x89PNG\r\n\x1a\n") and len(raw) >= 24:
        return struct.unpack(">II", raw[16:24])
    if raw.startswith(b"\xff\xd8\xff"):
        index = 2
        while index + 9 < len(raw):
            if raw[index] != 0xFF:
                index += 1
                continue
            marker = raw[index + 1]
            index += 2
            if marker in {0xD8, 0xD9}:
                continue
            if index + 2 > len(raw):
                break
            length = int.from_bytes(raw[index:index + 2], "big")
            if length < 2 or index + length > len(raw):
                break
            if marker in {0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF}:
                if index + 7 <= len(raw):
                    return int.from_bytes(raw[index + 5:index + 7], "big"), int.from_bytes(raw[index + 3:index + 5], "big")
            index += length
        return None
    if raw.startswith(b"RIFF") and raw[8:12] == b"WEBP" and len(raw) >= 30:
        chunk = raw[12:16]
        if chunk == b"VP8X":
            return 1 + int.from_bytes(raw[24:27], "little"), 1 + int.from_bytes(raw[27:30], "little")
        if chunk == b"VP8 " and len(raw) >= 30:
            return int.from_bytes(raw[26:28], "little") & 0x3FFF, int.from_bytes(raw[28:30], "little") & 0x3FFF
        if chunk == b"VP8L" and len(raw) >= 25:
            bits = int.from_bytes(raw[21:25], "little")
            return (bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1
    return None


def validate_output(raw: bytes, requested_format: str, requested_size: str) -> tuple[tuple[int, int] | None, list[str]]:
    if not raw:
        raise DispatchError("The API returned an empty image.")
    if len(raw) > 50 * 1024 * 1024:
        raise DispatchError("The returned image exceeds 50 MiB.")
    actual_format = image_signature(raw)
    expected = "jpeg" if requested_format == "jpeg" else requested_format
    if actual_format != expected:
        raise DispatchError(f"The API returned {actual_format or 'invalid data'} instead of {requested_format}.")
    dimensions = image_dimensions(raw)
    if dimensions is None:
        raise DispatchError("The returned image dimensions could not be validated.")
    width, height = dimensions
    if width <= 0 or height <= 0 or width > 3840 or height > 3840 or width * height > 8_294_400:
        raise DispatchError("The returned image dimensions exceed the supported output limits.")
    warnings = []
    expected_dimensions = parse_size(requested_size)
    if expected_dimensions and dimensions and dimensions != expected_dimensions:
        warnings.append(
            f"Provider returned {dimensions[0]}x{dimensions[1]} instead of "
            f"{expected_dimensions[0]}x{expected_dimensions[1]}; image was preserved without resizing."
        )
    return dimensions, warnings


def decrypt_dpapi(path: Path) -> str:
    if sys.platform != "win32":
        raise DispatchError("The configured credential requires this Windows user account.")
    if not path.is_file():
        raise DispatchError("The configured DPAPI credential file does not exist.")
    if path.suffix.lower() != ".dpapi":
        raise DispatchError("CredentialFile must point to a .dpapi file.")

    class Blob(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_ubyte))]

    try:
        raw = base64.b64decode(path.read_text(encoding="utf-8-sig"), validate=True)
    except (OSError, ValueError) as error:
        raise DispatchError(f"Cannot read the DPAPI credential: {error}") from None
    buffer = ctypes.create_string_buffer(raw)
    source = Blob(len(raw), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
    target = Blob()
    crypt = ctypes.WinDLL("crypt32", use_last_error=True)
    crypt.CryptUnprotectData.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.c_void_p,
        ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
    crypt.CryptUnprotectData.restype = wintypes.BOOL
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    kernel.LocalFree.restype = ctypes.c_void_p
    if not crypt.CryptUnprotectData(ctypes.byref(source), None, None, None, None, 1, ctypes.byref(target)):
        raise DispatchError("Cannot decrypt the image credential for this Windows account.")
    try:
        key = ctypes.string_at(target.pbData, target.cbData).decode("utf-8")
    finally:
        ctypes.memset(target.pbData, 0, target.cbData)
        kernel.LocalFree(target.pbData)
    if not key.strip():
        raise DispatchError("The image credential is empty.")
    return key


def multipart(fields: dict, images: list[Path]) -> tuple[bytes, str]:
    boundary = "codex-image-" + uuid.uuid4().hex
    parts = []
    for name, value in fields.items():
        parts.append((f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n").encode())
    for path in images:
        suffix = path.suffix.lower()
        mime = IMAGE_MIME.get(suffix)
        if mime is None:
            raise DispatchError("Reference images must be PNG, JPEG, or WebP.")
        try:
            with path.open("rb") as handle:
                raw = handle.read(MAX_IMAGE_BYTES + 1)
        except OSError as error:
            raise DispatchError(f"Cannot read an input image: {path}: {error}") from None
        if len(raw) > MAX_IMAGE_BYTES:
            raise DispatchError("An input image exceeds 50 MiB.")
        expected_format = {".png": "png", ".jpg": "jpeg", ".jpeg": "jpeg", ".webp": "webp"}[suffix]
        if image_signature(raw[:12]) != expected_format:
            raise DispatchError(f"The input image content does not match its file extension: {path}")
        safe_name = "input" + suffix
        parts.append((f"--{boundary}\r\nContent-Disposition: form-data; name=\"image[]\"; filename=\"{safe_name}\"\r\n"
                      f"Content-Type: {mime}\r\n\r\n").encode() + raw + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    return b"".join(parts), f"multipart/form-data; boundary={boundary}"


def redact(message: str, key: str | None) -> str:
    if key:
        message = message.replace(key, "[REDACTED]")
    return re.sub(r"sk-[A-Za-z0-9_-]+", "[REDACTED]", message)


def output_paths(out: Path) -> list[Path]:
    return [out]


def check_outputs_absent(out: Path):
    candidates = [out.with_suffix(".json")] + [out.with_suffix(ext) for ext in (".png", ".jpg", ".jpeg", ".webp")]
    if any(path.exists() for path in candidates):
        raise FileExistsError("An output or receipt already exists; choose a new output name.")


def receipt_path(out: Path) -> Path:
    return out.with_suffix(".json")


def write_receipt(out: Path, payload: dict):
    receipt = receipt_path(out)
    with receipt.open("x", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2)


def build_i2m_command(codex_home: Path, profile: dict, args: argparse.Namespace, prompt: str, out: Path) -> list[str]:
    helper = codex_home / I2M_HELPER_RELATIVE
    if not helper.is_file():
        raise DispatchError(f"The i2m helper script is missing: {helper}")
    command = [sys.executable, str(helper)]
    if args.prompt is not None:
        command += ["--prompt", prompt]
    else:
        command += ["--prompt-file", str(args.prompt_file.expanduser().resolve())]
    command += [
        "--out", str(out),
        "--size", str(profile["size"]),
        "--quality", str(profile["quality"]),
        "--format", str(profile["format"]),
        "--timeout", str(args.timeout if args.timeout is not None else profile["timeout"]),
    ]
    for image in args.image:
        command += ["--image", str(image.resolve())]
    if args.dry_run:
        command.append("--dry-run")
    return command


def run_i2m(codex_home: Path, profile: dict, args: argparse.Namespace, prompt: str, out: Path) -> dict:
    command = build_i2m_command(codex_home, profile, args, prompt, out)
    if args.dry_run:
        result = subprocess.run(command, check=False, text=True, capture_output=True)
        if result.stdout:
            print(result.stdout, end="")
        if result.stderr:
            print(result.stderr, end="", file=sys.stderr)
        if result.returncode:
            raise DispatchError(f"i2m dry-run failed with exit code {result.returncode}.")
        return {"provider": "i2m", "dry_run": True, "command": command}
    validate_i2m_credential_reference(codex_home)
    check_outputs_absent(out)
    prepare_output_directory(out)
    print("Submitting one i2m image API request; automatic retries are disabled.", flush=True)
    try:
        result = subprocess.run(command, check=False)
    except OSError as error:
        raise DispatchError(f"Could not start the i2m helper: {error}") from None
    if result.returncode:
        raise DispatchError(f"i2m request failed with exit code {result.returncode}; no retry was submitted.")
    actual_out = out.with_suffix(".jpeg") if profile["format"] == "jpeg" and out.suffix.lower() == ".jpg" else out
    if not actual_out.is_file():
        raise DispatchError("The i2m helper reported success but did not create the requested image; no retry was submitted.")
    try:
        image_size = actual_out.stat().st_size
        with actual_out.open("rb") as handle:
            actual_format = image_signature(handle.read(12))
    except OSError as error:
        raise DispatchError(f"Cannot verify the i2m output {actual_out}: {error}") from None
    if image_size == 0 or image_size > MAX_IMAGE_BYTES:
        raise DispatchError("The i2m helper produced an empty image or an image exceeding 50 MiB; no retry was submitted.")
    expected_format = "jpeg" if profile["format"] == "jpeg" else str(profile["format"])
    if actual_format != expected_format:
        raise DispatchError("The i2m helper output format does not match the requested format; no retry was submitted.")
    if not receipt_path(out).is_file():
        raise DispatchError("The i2m helper reported success but did not create its receipt; no retry was submitted.")
    return {"provider": "i2m", "outputs": [str(actual_out)], "receipt": str(receipt_path(out))}


def official_request_url(base_url: str, editing: bool) -> str:
    parsed = urllib.parse.urlsplit(str(base_url))
    if (parsed.scheme != "https" or parsed.hostname != "api.openai.com" or parsed.username or parsed.password
            or parsed.query or parsed.fragment or parsed.path.rstrip("/") != "/v1"):
        raise DispatchError("Official Image API BaseUrl must be exactly https://api.openai.com/v1.")
    return "https://api.openai.com/v1/images/" + ("edits" if editing else "generations")


def run_official(codex_home: Path, profile: dict, args: argparse.Namespace, prompt: str, out: Path) -> dict:
    endpoint = official_request_url(profile["base_url"], bool(args.image))
    timeout = int(args.timeout if args.timeout is not None else profile["timeout"])
    fields = {
        "model": str(profile["model"]),
        "prompt": prompt,
        "size": str(profile["size"]),
        "quality": str(profile["quality"]),
        "output_format": str(profile["format"]),
        "n": 1,
    }
    descriptor = {
        "provider": "official",
        "endpoint": endpoint,
        "request": fields,
        "input_images": [str(path.resolve()) for path in args.image],
        "outputs": [str(out)],
    }
    if args.dry_run:
        descriptor["dry_run"] = True
        return descriptor

    credential_path = validate_credential_reference(profile["credential_file"], codex_home)
    prepare_output_directory(out)
    check_outputs_absent(out)
    if args.image:
        body, content_type = multipart(fields, args.image)
    else:
        body, content_type = json.dumps(fields).encode("utf-8"), "application/json"
    started = time.monotonic()
    key = decrypt_dpapi(credential_path)
    try:
        print("Submitting one official Images API request; automatic retries are disabled.", flush=True)
        request = urllib.request.Request(endpoint, data=body, headers={
            "Authorization": "Bearer " + key,
            "Content-Type": content_type,
            "Accept": "application/json",
            "User-Agent": "CodexControlCenter-image-dispatch/1.0",
        }, method="POST")
        opener = urllib.request.build_opener(NoRedirect)
        with opener.open(request, timeout=timeout) as response:
            response_body = response.read(MAX_API_RESPONSE_BYTES + 1)
    except urllib.error.HTTPError as error:
        message = error.read(4001).decode("utf-8", errors="replace")[:4000]
        raise DispatchError(f"Official Images API HTTP {error.code}: {redact(message, key)}") from None
    except (TimeoutError, urllib.error.URLError) as error:
        raise DispatchError("The request timed out or lost its connection. Server completion is uncertain; no retry was submitted.") from None
    finally:
        del key
    if len(response_body) > MAX_API_RESPONSE_BYTES:
        raise DispatchError("The official Images API response exceeds the supported size limit.")
    try:
        result = json.loads(response_body)
    except (UnicodeError, json.JSONDecodeError):
        raise DispatchError("The official Images API returned invalid JSON.") from None
    items = result.get("data") if isinstance(result, dict) else None
    if not isinstance(items, list) or len(items) != 1:
        raise DispatchError("The API did not return exactly one image. No retry was submitted.")
    item = items[0] if isinstance(items[0], dict) else {}
    if item.get("b64_json"):
        try:
            raw = base64.b64decode(item["b64_json"], validate=True)
        except (ValueError, TypeError) as error:
            raise DispatchError("The API returned invalid Base64 image data.") from None
    else:
        raise DispatchError("The official API must return b64_json; image URL downloads are disabled.")
    dimensions, warnings = validate_output(raw, str(profile["format"]), str(profile["size"]))
    with out.open("xb") as handle:
        handle.write(raw)
    payload = {
        "provider": "official",
        "endpoint": endpoint,
        "created_at": datetime.now(timezone.utc).isoformat(),
        "request": fields,
        "input_images": [str(path.resolve()) for path in args.image],
        "outputs": [str(out)],
        "usage": result.get("usage"),
        "image_dimensions": dimensions,
        "elapsed_seconds": round(time.monotonic() - started, 2),
    }
    if warnings:
        payload["warnings"] = warnings
    write_receipt(out, payload)
    return {"provider": "official", "outputs": [str(out)], "receipt": str(receipt_path(out)),
            "image_dimensions": dimensions, "warnings": warnings}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Unified Codex image provider dispatcher")
    parser.add_argument("--codex-home", required=True, type=Path)
    prompt_group = parser.add_mutually_exclusive_group(required=True)
    prompt_group.add_argument("--prompt")
    prompt_group.add_argument("--prompt-file", type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--image", action="append", type=Path, default=[])
    parser.add_argument("--size")
    parser.add_argument("--quality", choices=["low", "medium", "high", "xhigh", "max", "auto"])
    parser.add_argument("--format", choices=["png", "jpeg", "webp"])
    parser.add_argument("--timeout", type=int)
    parser.add_argument("--dry-run", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    codex_home = args.codex_home.expanduser().resolve()
    active, profile = read_profile(codex_home)
    for key, value in (("size", args.size), ("quality", args.quality), ("format", args.format)):
        if value is not None:
            profile[key] = value
    if args.timeout is not None:
        profile["timeout"] = args.timeout
    else:
        profile["timeout"] = parse_timeout(profile["timeout"])
    prompt = args.prompt if args.prompt is not None else read_prompt_file(args.prompt_file)
    out = args.out.expanduser().resolve()
    images = [path.expanduser().resolve() for path in args.image]
    validate_request(profile, prompt, out, images, int(profile["timeout"]))
    if active == "i2m":
        result = run_i2m(codex_home, profile, args, prompt, out)
    else:
        result = run_official(codex_home, profile, args, prompt, out)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"ERROR: {type(error).__name__}: {error}", file=sys.stderr)
        raise SystemExit(1)

