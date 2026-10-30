#!/usr/bin/env python3
"""Guarded MiniMax media API adapter using only the Python standard library."""

from __future__ import annotations

import argparse
import base64
import binascii
import ctypes
import json
import os
import re
import socket
import struct
import sys
import urllib.error
import urllib.parse
import urllib.request
import zlib
from pathlib import Path
from typing import Any


CONFIG_RELATIVE_PATH = Path("control-center") / "minimax-media.json"
ALLOWED_API_HOSTS = {"api.minimax.cn", "api.minimax.io"}
ALLOWED_DOWNLOAD_SUFFIXES = (
    "minimax.cn",
    "minimaxi.com",
    "minimax.io",
    "minimax.chat",
    "hailuoai.com",
)
MAX_API_RESPONSE_BYTES = 96 * 1024 * 1024
MAX_DOWNLOAD_BYTES = 1024 * 1024 * 1024
MUSIC_RESTRICTION = (
    "Music API is unavailable to new paid users since 2026-08-20; only legacy paid users may continue. "
    "Free music models have been discontinued."
)

ENDPOINTS = {
    "speech": ("POST", "/v1/t2a_v2", "speech"),
    "image": ("POST", "/v1/image_generation", "image"),
    "video": ("POST", "/v2/video_generation", "video"),
    "music": ("POST", "/v1/music_generation", "music"),
}

ERROR_MESSAGES = {
    "configuration": "MiniMax media configuration is missing or invalid.",
    "invalidrequest": "The request file or its fields are invalid.",
    "missingcredential": "The encrypted credential is missing or unavailable to this Windows user.",
    "unsupported": "This operation is disabled or unsupported by the configured provider access.",
    "output_exists": "The output path already exists; no file was overwritten.",
    "output_path": "The output path must be absolute and its parent directory must exist.",
    "malformed_response": "The provider returned a malformed response; no automatic retry was made.",
    "uncertain_submission": "Submission outcome is uncertain; inspect the task list before taking further action.",
    "timeout": "The provider request timed out; no automatic retry was made.",
    "api_error": "The provider request failed; response details were suppressed.",
}


class AdapterError(Exception):
    def __init__(self, code: str):
        self.code = code if code in ERROR_MESSAGES else "api_error"
        super().__init__(ERROR_MESSAGES[self.code])

    def as_json(self) -> dict[str, Any]:
        return {"ok": False, "error": self.code, "message": ERROR_MESSAGES[self.code]}


class NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def _is_absolute_path(path: str | os.PathLike[str]) -> bool:
    return Path(path).is_absolute()


def _load_config(codex_home: str | os.PathLike[str]) -> dict[str, Any]:
    if not _is_absolute_path(codex_home):
        raise AdapterError("configuration")
    config_path = Path(codex_home) / CONFIG_RELATIVE_PATH
    try:
        config = json.loads(config_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise AdapterError("configuration") from None
    if not isinstance(config, dict) or config.get("version") != 1:
        raise AdapterError("configuration")

    base_url = config.get("base_url")
    if not isinstance(base_url, str):
        raise AdapterError("configuration")
    try:
        parsed = urllib.parse.urlsplit(base_url)
        valid_base = (
            parsed.scheme == "https"
            and parsed.hostname in ALLOWED_API_HOSTS
            and parsed.username is None
            and parsed.password is None
            and parsed.port in (None, 443)
            and parsed.path in ("", "/")
            and not parsed.query
            and not parsed.fragment
        )
    except ValueError:
        valid_base = False
    if not valid_base:
        raise AdapterError("configuration")

    credential_file = config.get("credential_file")
    models = config.get("models")
    timeout = config.get("request_timeout_seconds")
    if (
        not isinstance(credential_file, str)
        or not _is_absolute_path(credential_file)
        or not isinstance(models, dict)
        or any(not isinstance(models.get(name), str) or not models[name] for name in ("text", "speech", "video", "image", "music"))
        or not isinstance(config.get("music_enabled"), bool)
        or not isinstance(timeout, int)
        or isinstance(timeout, bool)
        or not 1 <= timeout <= 600
    ):
        raise AdapterError("configuration")
    return config


def _load_request(request_file: str | os.PathLike[str] | None) -> dict[str, Any]:
    if not request_file or not _is_absolute_path(request_file):
        raise AdapterError("invalidrequest")
    try:
        request = json.loads(Path(request_file).read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise AdapterError("invalidrequest") from None
    if not isinstance(request, dict):
        raise AdapterError("invalidrequest")
    return request


def _validate_output(out: str | os.PathLike[str] | None, required: bool) -> Path | None:
    if out is None:
        if required:
            raise AdapterError("output_path")
        return None
    if not _is_absolute_path(out):
        raise AdapterError("output_path")
    output = Path(out)
    if os.path.lexists(output):
        raise AdapterError("output_exists")
    if not output.parent.is_dir():
        raise AdapterError("output_path")
    return output


def _validate_model(request: dict[str, Any], config: dict[str, Any], model_key: str) -> dict[str, Any]:
    configured = config["models"][model_key]
    requested = request.get("model", configured)
    if requested != configured:
        raise AdapterError("unsupported")
    payload = dict(request)
    payload["model"] = configured
    return payload


def _validate_download_url(url: Any) -> urllib.parse.SplitResult:
    if not isinstance(url, str) or len(url) > 8192:
        raise AdapterError("invalidrequest")
    try:
        parsed = urllib.parse.urlsplit(url)
        hostname = parsed.hostname.lower() if parsed.hostname else ""
        port = parsed.port
    except ValueError:
        raise AdapterError("invalidrequest") from None
    allowed_host = any(hostname == suffix or hostname.endswith("." + suffix) for suffix in ALLOWED_DOWNLOAD_SUFFIXES)
    if (
        parsed.scheme != "https"
        or not allowed_host
        or parsed.username is not None
        or parsed.password is not None
        or port not in (None, 443)
        or not parsed.path
        or parsed.fragment
    ):
        raise AdapterError("unsupported")
    return parsed


def _read_response(response: Any, limit: int) -> bytes:
    data = response.read(limit + 1)
    if len(data) > limit:
        raise AdapterError("unsupported")
    return data


def _http_bytes(
    url: str,
    method: str,
    body: bytes | None,
    token: str | None,
    timeout: int,
    uncertain_on_transport: bool = False,
    limit: int = MAX_API_RESPONSE_BYTES,
) -> bytes:
    headers = {"Accept": "application/json"} if method != "GET" or token else {}
    if body is not None:
        headers["Content-Type"] = "application/json"
    if token is not None:
        headers["Authorization"] = "Bearer " + token
    request = urllib.request.Request(url, data=body, headers=headers, method=method)
    opener = urllib.request.build_opener(NoRedirectHandler())
    try:
        with opener.open(request, timeout=timeout) as response:
            return _read_response(response, limit)
    except urllib.error.HTTPError as exc:
        try:
            if 300 <= exc.code < 400 or exc.code in (404, 410):
                raise AdapterError("unsupported") from None
            raise AdapterError("api_error") from None
        finally:
            exc.close()
    except (TimeoutError, socket.timeout):
        raise AdapterError("uncertain_submission" if uncertain_on_transport else "timeout") from None
    except urllib.error.URLError as exc:
        if isinstance(exc.reason, (TimeoutError, socket.timeout)):
            raise AdapterError("uncertain_submission" if uncertain_on_transport else "timeout") from None
        raise AdapterError("uncertain_submission" if uncertain_on_transport else "api_error") from None
    except AdapterError:
        raise
    except Exception:
        raise AdapterError("uncertain_submission" if uncertain_on_transport else "api_error") from None


def _api_json(
    config: dict[str, Any],
    path: str,
    method: str,
    payload: dict[str, Any] | None,
    token: str,
    uncertain_on_transport: bool = False,
) -> dict[str, Any]:
    base = config["base_url"].rstrip("/")
    body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8") if payload is not None else None
    raw = _http_bytes(
        base + path,
        method,
        body,
        token,
        config["request_timeout_seconds"],
        uncertain_on_transport=uncertain_on_transport,
    )
    try:
        result = json.loads(raw.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError):
        raise AdapterError("malformed_response") from None
    if not isinstance(result, dict):
        raise AdapterError("malformed_response")
    base_resp = result.get("base_resp")
    if isinstance(base_resp, dict) and base_resp.get("status_code") not in (None, 0):
        raise AdapterError("api_error")
    return result


def _dpapi_unprotect_blob(encrypted: bytes) -> bytes:
    if not encrypted or os.name != "nt":
        raise AdapterError("missingcredential")

    class DataBlob(ctypes.Structure):
        _fields_ = [("cbData", ctypes.c_uint32), ("pbData", ctypes.POINTER(ctypes.c_byte))]

    source_buffer = ctypes.create_string_buffer(encrypted, len(encrypted))
    source = DataBlob(len(encrypted), ctypes.cast(source_buffer, ctypes.POINTER(ctypes.c_byte)))
    destination = DataBlob()
    kernel32 = None
    try:
        crypt32 = ctypes.WinDLL("Crypt32", use_last_error=True)
        kernel32 = ctypes.WinDLL("Kernel32", use_last_error=True)
        crypt32.CryptUnprotectData.argtypes = [
            ctypes.POINTER(DataBlob),
            ctypes.c_void_p,
            ctypes.POINTER(DataBlob),
            ctypes.c_void_p,
            ctypes.c_void_p,
            ctypes.c_uint32,
            ctypes.POINTER(DataBlob),
        ]
        crypt32.CryptUnprotectData.restype = ctypes.c_int
        kernel32.LocalFree.argtypes = [ctypes.c_void_p]
        kernel32.LocalFree.restype = ctypes.c_void_p
        ok = crypt32.CryptUnprotectData(
            ctypes.byref(source), None, None, None, None, 0x1, ctypes.byref(destination)
        )
        if not ok or not destination.pbData or not destination.cbData:
            raise AdapterError("missingcredential")
        return ctypes.string_at(destination.pbData, destination.cbData)
    except AdapterError:
        raise
    except Exception:
        raise AdapterError("missingcredential") from None
    finally:
        if destination.pbData and kernel32 is not None:
            try:
                kernel32.LocalFree(ctypes.cast(destination.pbData, ctypes.c_void_p))
            except Exception:
                pass


def _decrypt_credential_file(path: str | os.PathLike[str]) -> bytes:
    try:
        encoded = Path(path).read_text(encoding="utf-8").strip()
        encrypted = base64.b64decode(encoded.encode("ascii"), validate=True)
    except (OSError, UnicodeError, ValueError, binascii.Error):
        raise AdapterError("missingcredential") from None
    if not encrypted:
        raise AdapterError("missingcredential")
    return _dpapi_unprotect_blob(encrypted)


def _credential_token(config: dict[str, Any]) -> bytearray:
    decrypted = _decrypt_credential_file(config["credential_file"])
    try:
        token = decrypted.decode("utf-8").strip()
    except UnicodeError:
        raise AdapterError("missingcredential") from None
    if not token:
        raise AdapterError("missingcredential")
    return bytearray(token.encode("utf-8"))


def _token_text(token: bytearray) -> str:
    try:
        return token.decode("utf-8")
    except UnicodeError:
        raise AdapterError("missingcredential") from None


def _clear_token(token: bytearray) -> None:
    for index in range(len(token)):
        token[index] = 0


def _download_bytes(url: str, timeout: int) -> bytes:
    _validate_download_url(url)
    return _http_bytes(
        url,
        "GET",
        None,
        None,
        timeout,
        uncertain_on_transport=False,
        limit=MAX_DOWNLOAD_BYTES,
    )


MAX_DECODED_PNG_BYTES = 256 * 1024 * 1024
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
PNG_CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}
PNG_BIT_DEPTHS = {0: {1, 2, 4, 8, 16}, 2: {8, 16}, 3: {1, 2, 4, 8}, 4: {8, 16}, 6: {8, 16}}
PNG_ADAM7 = ((0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2))


def _invalid_media() -> None:
    raise AdapterError("malformed_response")


def _paeth_predictor(left: int, above: int, upper_left: int) -> int:
    estimate = left + above - upper_left
    left_distance = abs(estimate - left)
    above_distance = abs(estimate - above)
    corner_distance = abs(estimate - upper_left)
    if left_distance <= above_distance and left_distance <= corner_distance:
        return left
    if above_distance <= corner_distance:
        return above
    return upper_left


def _decode_png(data: bytes) -> tuple[int, int]:
    if not data.startswith(PNG_SIGNATURE):
        _invalid_media()
    offset = len(PNG_SIGNATURE)
    width = height = bit_depth = color_type = interlace = None
    palette_entries = None
    compressed_parts: list[bytes] = []
    saw_idat = False
    idat_closed = False
    saw_iend = False
    known_critical = {b"IHDR", b"PLTE", b"IDAT", b"IEND"}

    while offset < len(data):
        if len(data) - offset < 12:
            _invalid_media()
        length = int.from_bytes(data[offset : offset + 4], "big")
        kind = data[offset + 4 : offset + 8]
        chunk_end = offset + 12 + length
        if chunk_end > len(data) or not all((65 <= byte <= 90) or (97 <= byte <= 122) for byte in kind):
            _invalid_media()
        payload_start = offset + 8
        payload_end = payload_start + length
        payload = data[payload_start:payload_end]
        expected_crc = int.from_bytes(data[payload_end:chunk_end], "big")
        if zlib.crc32(kind + payload) & 0xFFFFFFFF != expected_crc:
            _invalid_media()
        if offset == len(PNG_SIGNATURE):
            if kind != b"IHDR" or length != 13:
                _invalid_media()
        elif kind == b"IHDR":
            _invalid_media()

        if kind == b"IHDR":
            if width is not None:
                _invalid_media()
            width, height, bit_depth, color_type, compression, filter_method, interlace = struct.unpack(">IIBBBBB", payload)
            if (
                width == 0
                or height == 0
                or width > 0x7FFFFFFF
                or height > 0x7FFFFFFF
                or color_type not in PNG_CHANNELS
                or bit_depth not in PNG_BIT_DEPTHS[color_type]
                or compression != 0
                or filter_method != 0
                or interlace not in (0, 1)
            ):
                _invalid_media()
        elif kind == b"PLTE":
            if width is None or saw_idat or palette_entries is not None or color_type in (0, 4):
                _invalid_media()
            if not payload or len(payload) % 3 or len(payload) > 768:
                _invalid_media()
            palette_entries = len(payload) // 3
            if color_type == 3 and palette_entries > (1 << bit_depth):
                _invalid_media()
        elif kind == b"IDAT":
            if width is None or idat_closed:
                _invalid_media()
            saw_idat = True
            compressed_parts.append(payload)
        elif kind == b"IEND":
            if length != 0 or not saw_idat or chunk_end != len(data):
                _invalid_media()
            saw_iend = True
            offset = chunk_end
            break
        else:
            if saw_idat:
                idat_closed = True
            if kind[0] & 0x20 == 0 and kind not in known_critical:
                _invalid_media()
        if kind != b"IDAT" and saw_idat:
            idat_closed = True
        offset = chunk_end

    if not saw_iend or width is None or height is None or not saw_idat:
        _invalid_media()
    if color_type == 3 and palette_entries is None:
        _invalid_media()

    channels = PNG_CHANNELS[color_type]
    bits_per_pixel = channels * bit_depth
    bytes_per_pixel = max(1, (bits_per_pixel + 7) // 8)
    if interlace == 0:
        passes = ((0, 0, 1, 1),)
    else:
        passes = PNG_ADAM7
    pass_sizes: list[tuple[int, int, int]] = []
    decoded_size = 0
    for start_x, start_y, step_x, step_y in passes:
        pass_width = max(0, (width - start_x + step_x - 1) // step_x)
        pass_height = max(0, (height - start_y + step_y - 1) // step_y)
        if pass_width == 0 or pass_height == 0:
            continue
        row_bytes = (pass_width * bits_per_pixel + 7) // 8
        decoded_size += pass_height * (row_bytes + 1)
        pass_sizes.append((pass_width, pass_height, row_bytes))
    if decoded_size > MAX_DECODED_PNG_BYTES:
        _invalid_media()

    inflater = zlib.decompressobj()
    try:
        decoded = inflater.decompress(b"".join(compressed_parts), decoded_size + 1)
        if inflater.unconsumed_tail or len(decoded) > decoded_size:
            _invalid_media()
        decoded += inflater.flush()
    except zlib.error:
        _invalid_media()
    if len(decoded) != decoded_size or not inflater.eof or inflater.unused_data or inflater.unconsumed_tail:
        _invalid_media()

    cursor = 0
    for pass_width, pass_height, row_bytes in pass_sizes:
        previous = bytearray(row_bytes)
        for _row_index in range(pass_height):
            filter_type = decoded[cursor]
            cursor += 1
            if filter_type > 4:
                _invalid_media()
            source = decoded[cursor : cursor + row_bytes]
            cursor += row_bytes
            row = bytearray(row_bytes)
            for index, raw_value in enumerate(source):
                left = row[index - bytes_per_pixel] if index >= bytes_per_pixel else 0
                above = previous[index]
                upper_left = previous[index - bytes_per_pixel] if index >= bytes_per_pixel else 0
                if filter_type == 0:
                    predictor = 0
                elif filter_type == 1:
                    predictor = left
                elif filter_type == 2:
                    predictor = above
                elif filter_type == 3:
                    predictor = (left + above) // 2
                else:
                    predictor = _paeth_predictor(left, above, upper_left)
                row[index] = (raw_value + predictor) & 0xFF
            if color_type == 3:
                for pixel_index in range(pass_width):
                    bit_offset = pixel_index * bit_depth
                    palette_index = (row[bit_offset // 8] >> (8 - bit_depth - bit_offset % 8)) & ((1 << bit_depth) - 1)
                    if palette_index >= palette_entries:
                        _invalid_media()
            previous = row
    if cursor != len(decoded):
        _invalid_media()
    return width, height


JPEG_SOF_MARKERS = {0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF}


def _jpeg_dimensions(data: bytes) -> tuple[int, int]:
    if len(data) < 4 or not data.startswith(b"\xff\xd8") or not data.endswith(b"\xff\xd9"):
        _invalid_media()
    offset = 2
    dimensions = None
    saw_scan = False
    while offset < len(data):
        if data[offset] != 0xFF:
            _invalid_media()
        marker_start = offset
        while offset < len(data) and data[offset] == 0xFF:
            offset += 1
        if offset >= len(data):
            _invalid_media()
        marker = data[offset]
        offset += 1
        if marker == 0xD9:
            if offset != len(data) or dimensions is None or not saw_scan:
                _invalid_media()
            return dimensions
        if marker in (0x00, 0xD8) or 0xD0 <= marker <= 0xD7:
            _invalid_media()
        if marker == 0x01:
            continue
        if offset + 2 > len(data):
            _invalid_media()
        segment_length = int.from_bytes(data[offset : offset + 2], "big")
        if segment_length < 2 or offset + segment_length > len(data):
            _invalid_media()
        segment = data[offset + 2 : offset + segment_length]
        segment_end = offset + segment_length
        if marker in JPEG_SOF_MARKERS:
            if len(segment) < 6:
                _invalid_media()
            component_count = segment[5]
            if component_count == 0 or len(segment) != 6 + component_count * 3:
                _invalid_media()
            height, width = struct.unpack(">HH", segment[1:5])
            if width == 0 or height == 0:
                _invalid_media()
            dimensions = (width, height)
        if marker == 0xDA:
            if dimensions is None or len(segment) < 4:
                _invalid_media()
            component_count = segment[0]
            if component_count == 0 or len(segment) != 4 + component_count * 2:
                _invalid_media()
            offset = segment_end
            scan_start = offset
            saw_entropy = False
            while offset < len(data):
                if data[offset] != 0xFF:
                    saw_entropy = True
                    offset += 1
                    continue
                marker_start = offset
                offset += 1
                while offset < len(data) and data[offset] == 0xFF:
                    offset += 1
                if offset >= len(data):
                    _invalid_media()
                scan_marker = data[offset]
                if scan_marker == 0x00 or 0xD0 <= scan_marker <= 0xD7:
                    saw_entropy = True
                    offset += 1
                    continue
                offset = marker_start
                break
            if not saw_entropy or offset == scan_start:
                _invalid_media()
            saw_scan = True
        else:
            offset = segment_end
    _invalid_media()


def _validate_wav(data: bytes) -> None:
    if len(data) < 12 or data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        _invalid_media()
    riff_end = 8 + int.from_bytes(data[4:8], "little")
    if riff_end != len(data) or riff_end < 12:
        _invalid_media()
    offset = 12
    saw_format = False
    saw_audio = False
    while offset < riff_end:
        if riff_end - offset < 8:
            _invalid_media()
        kind = data[offset : offset + 4]
        length = int.from_bytes(data[offset + 4 : offset + 8], "little")
        payload_start = offset + 8
        payload_end = payload_start + length
        chunk_end = payload_end + (length & 1)
        if chunk_end > riff_end:
            _invalid_media()
        if kind == b"fmt ":
            if length < 16:
                _invalid_media()
            encoding, channels, sample_rate, _byte_rate, block_align, bits_per_sample = struct.unpack(
                "<HHIIHH", data[payload_start : payload_start + 16]
            )
            if encoding == 0 or channels == 0 or sample_rate == 0 or block_align == 0 or bits_per_sample == 0:
                _invalid_media()
            saw_format = True
        elif kind == b"data":
            if length == 0:
                _invalid_media()
            saw_audio = True
        offset = chunk_end
    if offset != riff_end or not saw_format or not saw_audio:
        _invalid_media()


def _mp3_frame_length(data: bytes, offset: int) -> int | None:
    if offset + 4 > len(data):
        return None
    header = int.from_bytes(data[offset : offset + 4], "big")
    if header >> 21 != 0x7FF:
        return None
    version = (header >> 19) & 0x3
    layer_bits = (header >> 17) & 0x3
    bitrate_index = (header >> 12) & 0xF
    sample_index = (header >> 10) & 0x3
    padding = (header >> 9) & 1
    if version == 1 or layer_bits == 0 or bitrate_index in (0, 15) or sample_index == 3:
        return None
    layer = 4 - layer_bits
    mpeg1 = version == 3
    if mpeg1:
        tables = {
            1: (0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0),
            2: (0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0),
            3: (0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0),
        }
    else:
        tables = {
            1: (0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0),
            2: (0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0),
            3: (0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0),
        }
    sample_rates = (44100, 48000, 32000)
    sample_rate = sample_rates[sample_index]
    if version == 2:
        sample_rate //= 2
    elif version == 0:
        sample_rate //= 4
    bitrate = tables[layer][bitrate_index] * 1000
    if layer == 1:
        return (12 * bitrate // sample_rate + padding) * 4
    coefficient = 72 if layer == 3 and not mpeg1 else 144
    return coefficient * bitrate // sample_rate + padding


def _validate_mp3(data: bytes) -> None:
    offset = 0
    if data.startswith(b"ID3"):
        if len(data) < 10 or any(byte & 0x80 for byte in data[6:10]):
            _invalid_media()
        tag_size = (data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9]
        offset = 10 + tag_size + (10 if data[5] & 0x10 else 0)
        if offset > len(data):
            _invalid_media()
    frame_length = _mp3_frame_length(data, offset)
    if frame_length is None or offset + frame_length > len(data):
        _invalid_media()


def _validate_mp4(data: bytes) -> None:
    offset = 0
    boxes: list[bytes] = []
    while offset < len(data):
        if len(data) - offset < 8:
            _invalid_media()
        size = int.from_bytes(data[offset : offset + 4], "big")
        kind = data[offset + 4 : offset + 8]
        header_size = 8
        if size == 1:
            if len(data) - offset < 16:
                _invalid_media()
            size = int.from_bytes(data[offset + 8 : offset + 16], "big")
            header_size = 16
        elif size == 0:
            size = len(data) - offset
        if size < header_size or offset + size > len(data):
            _invalid_media()
        if not all(32 <= byte <= 126 for byte in kind):
            _invalid_media()
        if not boxes and kind != b"ftyp":
            _invalid_media()
        if kind == b"ftyp":
            payload_size = size - header_size
            if payload_size < 8 or (payload_size - 8) % 4:
                _invalid_media()
        boxes.append(kind)
        offset += size
    if offset != len(data) or b"mdat" not in boxes or not ({b"moov", b"moof"} & set(boxes)):
        _invalid_media()


def _identify_media(data: bytes) -> dict[str, Any]:
    validation = "container-structure"
    dimensions = None
    if data.startswith(PNG_SIGNATURE):
        media_format, mime = "png", "image/png"
        dimensions = _decode_png(data)
        validation = "image-data-decoded"
    elif data.startswith(b"\xff\xd8"):
        media_format, mime = "jpg", "image/jpeg"
        dimensions = _jpeg_dimensions(data)
    elif len(data) >= 12 and data[:4] == b"RIFF" and data[8:12] == b"WAVE":
        media_format, mime = "wav", "audio/wav"
        _validate_wav(data)
    elif data.startswith(b"ID3") or (len(data) >= 2 and data[0] == 0xFF and data[1] & 0xE0 == 0xE0):
        media_format, mime = "mp3", "audio/mpeg"
        _validate_mp3(data)
    elif len(data) >= 8 and data[4:8] == b"ftyp":
        media_format, mime = "mp4", "video/mp4"
        _validate_mp4(data)
    else:
        _invalid_media()

    metadata: dict[str, Any] = {
        "format": media_format,
        "mimeType": mime,
        "sizeBytes": len(data),
        "validation": validation,
    }
    if dimensions is not None:
        metadata["width"], metadata["height"] = dimensions
    return metadata


def _write_exclusive(output: Path, data: bytes) -> None:
    try:
        descriptor = os.open(output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError:
        raise AdapterError("output_exists") from None
    except OSError:
        raise AdapterError("output_path") from None
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
    except OSError:
        try:
            output.unlink()
        except OSError:
            pass
        raise AdapterError("api_error") from None


def _decode_hex_audio(value: Any) -> bytes:
    if not isinstance(value, str) or not value:
        raise AdapterError("malformed_response")
    try:
        return bytes.fromhex(value)
    except ValueError:
        raise AdapterError("malformed_response") from None


def _extract_audio(result: dict[str, Any], output_format: str, timeout: int) -> bytes:
    data = result.get("data")
    audio = data.get("audio") if isinstance(data, dict) else None
    if output_format == "hex":
        return _decode_hex_audio(audio)
    if output_format == "url" and isinstance(audio, str):
        return _download_bytes(audio, timeout)
    raise AdapterError("malformed_response")


def _extract_image(result: dict[str, Any], response_format: str, timeout: int) -> bytes:
    data = result.get("data")
    if not isinstance(data, dict):
        raise AdapterError("malformed_response")
    if response_format == "base64":
        items = data.get("image_base64")
        if not isinstance(items, list) or len(items) != 1 or not isinstance(items[0], str):
            raise AdapterError("malformed_response")
        try:
            return base64.b64decode(items[0], validate=True)
        except (ValueError, binascii.Error):
            raise AdapterError("malformed_response") from None
    if response_format == "url":
        items = data.get("image_urls")
        if not isinstance(items, list) or len(items) != 1:
            raise AdapterError("malformed_response")
        return _download_bytes(items[0], timeout)
    raise AdapterError("malformed_response")


def _validate_audio_request(payload: dict[str, Any]) -> str:
    if payload.get("stream", False) is not False:
        raise AdapterError("unsupported")
    audio_setting = payload.setdefault("audio_setting", {})
    if not isinstance(audio_setting, dict):
        raise AdapterError("invalidrequest")
    audio_format = audio_setting.get("format", "mp3")
    if audio_format not in ("mp3", "wav"):
        raise AdapterError("unsupported")
    audio_setting["format"] = audio_format
    return audio_format


def _normal_status(value: Any) -> str:
    if not isinstance(value, str):
        return "unknown"
    lowered = value.casefold()
    aliases = {
        "pending": "pending",
        "queued": "queued",
        "queueing": "queued",
        "preparing": "preparing",
        "running": "running",
        "processing": "running",
        "succeeded": "succeeded",
        "success": "succeeded",
        "failed": "failed",
        "fail": "failed",
        "cancelled": "cancelled",
        "canceled": "cancelled",
    }
    return aliases.get(lowered, "unknown")


def _task_id(request: dict[str, Any]) -> str:
    value = request.get("task_id", request.get("taskId"))
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", value):
        raise AdapterError("invalidrequest")
    return value


def _dry_run_result(command: str, config: dict[str, Any], request: dict[str, Any], output: Path | None) -> dict[str, Any]:
    if command == "music":
        payload = _validate_model(request, config, "music")
        _validate_audio_request(payload)
        if payload.get("output_format", "hex") not in ("hex", "url"):
            raise AdapterError("invalidrequest")
        return {
            "ok": True,
            "dryRun": True,
            "available": config["music_enabled"],
            "action": "POST",
            "endpoint": "/v1/music_generation",
            "model": payload["model"],
            "restriction": MUSIC_RESTRICTION,
            "output": str(output) if output else None,
        }
    if command == "download":
        _validate_download_url(request.get("url", request.get("download_url")))
        return {"ok": True, "dryRun": True, "action": "GET", "endpoint": "provider media URL", "output": str(output) if output else None}
    if command == "video-status":
        return {"ok": True, "dryRun": True, "action": "GET", "endpoint": "/v2/query/video_generation/{task_id}", "taskId": _task_id(request)}
    method, endpoint, model_key = ENDPOINTS[command]
    payload = _validate_model(request, config, model_key)
    if command == "speech":
        _validate_audio_request(payload)
    elif command == "image":
        if payload.get("n", 1) != 1:
            raise AdapterError("unsupported")
        response_format = payload.setdefault("response_format", "base64")
        if response_format not in ("base64", "url"):
            raise AdapterError("invalidrequest")
    elif command == "music":
        _validate_audio_request(payload)
    return {
        "ok": True,
        "dryRun": True,
        "action": method,
        "endpoint": endpoint,
        "model": payload["model"],
        "output": str(output) if output else None,
    }


def execute(
    command: str,
    codex_home: str | os.PathLike[str],
    request_file: str | os.PathLike[str] | None = None,
    out: str | os.PathLike[str] | None = None,
    dry_run: bool = False,
) -> dict[str, Any]:
    config = _load_config(codex_home)
    credential_exists = Path(config["credential_file"]).is_file()
    if command == "status":
        return {
            "ok": True,
            "status": "configured" if credential_exists else "missingcredential",
            "configPath": str(Path(codex_home) / CONFIG_RELATIVE_PATH),
            "credentialPresent": credential_exists,
            "credentialValidated": False,
            "apiHost": urllib.parse.urlsplit(config["base_url"]).hostname,
            "models": config["models"],
            "musicEnabled": config["music_enabled"],
            "musicRestriction": MUSIC_RESTRICTION,
        }
    if command not in (*ENDPOINTS, "video-status", "download"):
        raise AdapterError("unsupported")

    request = _load_request(request_file)
    is_file_result = command in ("speech", "image", "music", "download")
    output = _validate_output(out, required=is_file_result and not dry_run)
    if command == "video" and out is not None:
        raise AdapterError("unsupported")

    if command == "music" and not config["music_enabled"] and not dry_run:
        raise AdapterError("unsupported")
    if dry_run:
        return _dry_run_result(command, config, request, output)

    if command == "download":
        url = request.get("url", request.get("download_url"))
        _validate_download_url(url)
        assert output is not None
        media = _download_bytes(url, config["request_timeout_seconds"])
        metadata = _identify_media(media)
        _write_exclusive(output, media)
        return {"ok": True, "path": str(output), "artifact": metadata}

    if command == "video-status":
        task_id = _task_id(request)
        token_buffer = _credential_token(config)
        try:
            result = _api_json(
                config,
                "/v2/query/video_generation/" + urllib.parse.quote(task_id, safe=""),
                "GET",
                None,
                _token_text(token_buffer),
            )
        finally:
            _clear_token(token_buffer)
        task = result.get("task")
        if not isinstance(task, dict):
            raise AdapterError("malformed_response")
        response_id = task.get("id")
        if response_id is not None and response_id != task_id:
            raise AdapterError("malformed_response")
        status = _normal_status(task.get("status"))
        content = task.get("content")
        artifact_url = content.get("url") if isinstance(content, dict) else None
        artifact: dict[str, Any] = {"available": isinstance(artifact_url, str)}
        if isinstance(artifact_url, str):
            parsed_artifact = _validate_download_url(artifact_url)
            if not parsed_artifact.query:
                artifact["url"] = artifact_url
            else:
                artifact["urlOmitted"] = True
        if output is not None:
            if status != "succeeded" or not isinstance(artifact_url, str):
                raise AdapterError("unsupported")
            media = _download_bytes(artifact_url, config["request_timeout_seconds"])
            metadata = _identify_media(media)
            _write_exclusive(output, media)
            return {"ok": True, "taskId": task_id, "status": status, "path": str(output), "artifact": metadata}
        return {"ok": True, "taskId": task_id, "status": status, "artifact": artifact}

    method, endpoint, model_key = ENDPOINTS[command]
    payload = _validate_model(request, config, model_key)
    if command == "speech":
        audio_format = _validate_audio_request(payload)
        output_format = payload.setdefault("output_format", "hex")
        if output_format not in ("hex", "url"):
            raise AdapterError("invalidrequest")
    elif command == "image":
        if payload.get("n", 1) != 1:
            raise AdapterError("unsupported")
        payload["n"] = 1
        response_format = payload.setdefault("response_format", "base64")
        if response_format not in ("base64", "url"):
            raise AdapterError("invalidrequest")
    elif command == "music":
        audio_format = _validate_audio_request(payload)
        output_format = payload.setdefault("output_format", "hex")
        if output_format not in ("hex", "url"):
            raise AdapterError("invalidrequest")

    if command in ("speech", "image", "music") and output is None:
        raise AdapterError("output_path")
    token_buffer = _credential_token(config)
    try:
        result = _api_json(
            config,
            endpoint,
            method,
            payload,
            _token_text(token_buffer),
            uncertain_on_transport=(command == "video"),
        )
    finally:
        _clear_token(token_buffer)

    if command == "video":
        task_id = result.get("task_id")
        if not isinstance(task_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", task_id):
            raise AdapterError("malformed_response")
        return {"ok": True, "taskId": task_id, "status": "submitted", "model": payload["model"]}

    if command == "speech":
        media = _extract_audio(result, output_format, config["request_timeout_seconds"])
    elif command == "image":
        media = _extract_image(result, response_format, config["request_timeout_seconds"])
    else:
        media = _extract_audio(result, output_format, config["request_timeout_seconds"])
    metadata = _identify_media(media)
    expected = audio_format if command in ("speech", "music") else None
    if expected is not None and metadata["format"] != expected:
        raise AdapterError("malformed_response")
    if command == "image" and metadata["format"] not in ("png", "jpg"):
        raise AdapterError("malformed_response")
    assert output is not None
    _write_exclusive(output, media)
    result_data: dict[str, Any] = {"ok": True, "path": str(output), "artifact": metadata}
    if command == "image":
        provider_metadata = result.get("metadata")
        if isinstance(provider_metadata, dict):
            for key in ("success_count", "failed_count"):
                value = provider_metadata.get(key)
                if isinstance(value, (int, float)) and not isinstance(value, bool):
                    result_data[key] = value
    return result_data


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Guarded MiniMax media API adapter")
    parser.add_argument("--codex-home", required=True, help="Absolute Codex home path")
    parser.add_argument("command", choices=(*ENDPOINTS, "video-status", "download", "status"))
    parser.add_argument("--request-file", help="Absolute JSON request file path")
    parser.add_argument("--out", help="Absolute output file path; existing files are never overwritten")
    parser.add_argument("--dry-run", action="store_true", help="Validate and show the request plan without decrypting credentials or making HTTP requests")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    try:
        result = execute(args.command, args.codex_home, args.request_file, args.out, args.dry_run)
        print(json.dumps(result, ensure_ascii=False, separators=(",", ":")))
        return 0
    except AdapterError as exc:
        print(json.dumps(exc.as_json(), ensure_ascii=False, separators=(",", ":")))
        return 2


if __name__ == "__main__":
    sys.exit(main())
