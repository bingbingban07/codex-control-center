"""Persistent i2m Images API client. Standard library only; no browser or mouse."""
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
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


CONFIG = Path(__file__).resolve().parents[1] / "config.json"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise RuntimeError("API redirect refused; check the configured endpoint.")


def decrypt_key(path):
    if sys.platform != "win32":
        raise RuntimeError("The configured credential requires this Windows user account.")

    class Blob(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_ubyte))]

    raw = base64.b64decode(Path(path).read_text(encoding="utf-8-sig"), validate=True)
    buffer = ctypes.create_string_buffer(raw)
    source = Blob(len(raw), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
    target = Blob()
    crypt = ctypes.WinDLL("crypt32", use_last_error=True)
    crypt.CryptUnprotectData.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p,
        ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
    crypt.CryptUnprotectData.restype = wintypes.BOOL
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    kernel.LocalFree.restype = ctypes.c_void_p
    if not crypt.CryptUnprotectData(ctypes.byref(source), None, None, None, None, 1, ctypes.byref(target)):
        raise RuntimeError("Cannot decrypt the image credential for this Windows account.")
    try:
        key = ctypes.string_at(target.pbData, target.cbData).decode("utf-8")
    finally:
        ctypes.memset(target.pbData, 0, target.cbData)
        kernel.LocalFree(target.pbData)
    if not key.strip():
        raise RuntimeError("The image credential is empty.")
    return key


def multipart(fields, images, mask):
    boundary = "i2m-" + uuid.uuid4().hex
    parts = []
    for name, value in fields.items():
        parts.append((f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n').encode())
    for field, path in [("image[]", p) for p in images] + ([("mask", mask)] if mask else []):
        mime = mimetypes.guess_type(path.name)[0]
        if mime not in {"image/png", "image/jpeg", "image/webp"}:
            raise ValueError("Reference images must be PNG, JPEG, or WebP.")
        data = path.read_bytes()
        if len(data) > 50 * 1024 * 1024:
            raise ValueError("An input image exceeds 50 MiB.")
        safe_name = "input" + path.suffix.lower()
        parts.append((f'--{boundary}\r\nContent-Disposition: form-data; name="{field}"; filename="{safe_name}"\r\nContent-Type: {mime}\r\n\r\n').encode() + data + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    return b"".join(parts), f"multipart/form-data; boundary={boundary}"


def image_extension(raw):
    if raw.startswith(b"\x89PNG\r\n\x1a\n"):
        return ".png"
    if raw.startswith(b"\xff\xd8\xff"):
        return ".jpeg"
    if raw.startswith(b"RIFF") and raw[8:12] == b"WEBP":
        return ".webp"
    raise RuntimeError("The API returned data that is not a PNG, JPEG, or WebP image.")


def png_dimensions(raw):
    if raw.startswith(b"\x89PNG\r\n\x1a\n") and len(raw) >= 24:
        return list(struct.unpack(">II", raw[16:24]))
    return None


def image_bytes(item, timeout):
    if item.get("b64_json"):
        return base64.b64decode(item["b64_json"], validate=True)
    url = item.get("url", "")
    if url.startswith("data:image/") and ";base64," in url:
        return base64.b64decode(url.split(",", 1)[1], validate=True)
    if urllib.parse.urlsplit(url).scheme != "https":
        raise RuntimeError("The API returned no Base64 image or HTTPS image URL.")
    # No API credential is attached to returned image URLs.
    with urllib.request.urlopen(url, timeout=timeout) as response:
        if urllib.parse.urlsplit(response.url).scheme != "https":
            raise RuntimeError("Refused an insecure image download redirect.")
        return response.read()


def main():
    config = json.loads(CONFIG.read_text(encoding="utf-8-sig"))
    parser = argparse.ArgumentParser(description=__doc__)
    prompts = parser.add_mutually_exclusive_group(required=True)
    prompts.add_argument("--prompt")
    prompts.add_argument("--prompt-file", type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--size", default=config["size"])
    parser.add_argument("--quality", choices=["low", "medium", "high", "max", "auto"], default=config["quality"])
    parser.add_argument("--format", choices=["png", "jpeg", "webp"], default=config["output_format"])
    parser.add_argument("--n", type=int, choices=range(1, 5), default=1)
    parser.add_argument("--timeout", type=int, default=config["timeout"])
    parser.add_argument("--background", choices=["auto", "opaque", "transparent"])
    parser.add_argument("--moderation", choices=["auto", "low"])
    parser.add_argument("--image", action="append", type=Path, default=[])
    parser.add_argument("--mask", type=Path)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    prompt = args.prompt if args.prompt is not None else args.prompt_file.read_text(encoding="utf-8-sig")
    if not prompt.strip():
        raise ValueError("The prompt is empty.")
    if args.size != "auto" and not re.fullmatch(r"[1-9][0-9]{1,4}x[1-9][0-9]{1,4}", args.size):
        raise ValueError("Size must be auto or WIDTHxHEIGHT.")
    if args.timeout < 1 or args.timeout > 1800:
        raise ValueError("Timeout must be between 1 and 1800 seconds.")
    if args.mask and not args.image:
        raise ValueError("--mask requires --image.")
    if args.background == "transparent" and args.format == "jpeg":
        raise ValueError("Transparent backgrounds require PNG or WebP.")
    base = config["base_url"].rstrip("/")
    parsed = urllib.parse.urlsplit(base)
    if parsed.scheme != "https" or parsed.hostname != "sub.114514.one" or parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError("The credential is restricted to the configured HTTPS 114514 provider.")
    endpoint = base + ("/images/edits" if args.image else "/images/generations")
    fields = {"model": config["model"], "prompt": prompt, "size": args.size,
              "quality": args.quality, "n": args.n, "output_format": args.format}
    for name in ("background", "moderation"):
        if getattr(args, name):
            fields[name] = getattr(args, name)
    out = args.out.resolve()
    paths = [out if args.n == 1 else out.with_name(f"{out.stem}-{i+1}{out.suffix}") for i in range(args.n)]
    receipt = out.with_suffix(".json")
    # Check all supported alternate image extensions before submitting a paid request.
    candidates = [receipt] + [p.with_suffix(ext) for p in paths for ext in (".png", ".jpeg", ".jpg", ".webp")]
    if any(p.exists() for p in candidates):
        raise FileExistsError("An output or receipt already exists; choose a new output name.")
    for path in args.image + ([args.mask] if args.mask else []):
        if not path.is_file():
            raise FileNotFoundError("An input image does not exist.")
    if args.image:
        body, content_type = multipart(fields, args.image, args.mask)
    else:
        body, content_type = json.dumps(fields).encode("utf-8"), "application/json"
    if args.dry_run:
        print(json.dumps({"endpoint": endpoint, "request": fields,
                          "input_images": [str(p.resolve()) for p in args.image],
                          "outputs": [str(p) for p in paths]}, ensure_ascii=False, indent=2))
        return
    out.parent.mkdir(parents=True, exist_ok=True)
    key = decrypt_key(config["credential_file"])
    request = urllib.request.Request(endpoint, data=body, headers={
        "Authorization": "Bearer " + key, "Content-Type": content_type,
        "Accept": "application/json", "User-Agent": "i2m-imagegen/1.0"}, method="POST")
    started = time.monotonic()
    print("Submitting one image API request; automatic retries are disabled.", flush=True)
    try:
        opener = urllib.request.build_opener(NoRedirect)
        with opener.open(request, timeout=args.timeout) as response:
            result = json.load(response)
    except urllib.error.HTTPError as error:
        message = error.read().decode("utf-8", errors="replace")[:4000].replace(key, "[REDACTED]")
        message = re.sub(r"sk-[A-Za-z0-9_-]+", "[REDACTED]", message)
        raise RuntimeError(f"Image API HTTP {error.code}: {message}") from None
    except (TimeoutError, urllib.error.URLError) as error:
        raise RuntimeError("The request timed out or lost its connection. Server completion is uncertain; no retry was submitted.") from None
    finally:
        del key
    items = result.get("data") if isinstance(result, dict) else None
    if not isinstance(items, list) or len(items) != args.n:
        raise RuntimeError("The API did not return the requested number of images. No retry was submitted.")
    decoded = [image_bytes(item, args.timeout) for item in items]
    actual_paths = [path.with_suffix(image_extension(raw)) for path, raw in zip(paths, decoded)]
    if any(p.exists() for p in actual_paths):
        raise FileExistsError("A returned-format output already exists; refusing overwrite.")
    for path, raw in zip(actual_paths, decoded):
        with path.open("xb") as handle:
            handle.write(raw)
    metadata = {"provider": "i2m / 114514", "endpoint": endpoint,
                "created_at": datetime.now(timezone.utc).isoformat(), "request": fields,
                "elapsed_seconds": round(time.monotonic() - started, 2),
                "input_images": [str(p.resolve()) for p in args.image],
                "outputs": [str(p) for p in actual_paths], "usage": result.get("usage"),
                "png_dimensions": [png_dimensions(raw) for raw in decoded]}
    if args.size != "auto":
        requested_dimensions = [int(value) for value in args.size.split("x")]
        if any(size is not None and size != requested_dimensions for size in metadata["png_dimensions"]):
            metadata["warning"] = "The provider returned different pixel dimensions than requested; images were preserved without resizing."
    with receipt.open("x", encoding="utf-8") as handle:
        json.dump(metadata, handle, ensure_ascii=False, indent=2)
    print(json.dumps({"ok": True, "outputs": metadata["outputs"], "receipt": str(receipt),
                      "elapsed_seconds": metadata["elapsed_seconds"],
                      "png_dimensions": metadata["png_dimensions"],
                      "warning": metadata.get("warning")}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # Avoid raw response dumps and tracebacks containing authentication headers.
        print(f"ERROR: {type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(1)
