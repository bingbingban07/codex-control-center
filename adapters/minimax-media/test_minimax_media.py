import base64
import io
import json
import struct
import tempfile
import unittest
import urllib.error
import zlib
from pathlib import Path
from unittest import mock

import minimax_media as adapter


def png_bytes(width=1, height=1):
    def chunk(kind, payload):
        body = kind + payload
        return struct.pack(">I", len(payload)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    scanlines = b"".join(b"\x00" + bytes(width * 4) for _ in range(height))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(scanlines)) + chunk(b"IEND", b"")


def wav_bytes():
    fmt = struct.pack("<HHIIHH", 1, 1, 8000, 8000, 1, 8)
    chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt
    chunks += b"data" + struct.pack("<I", 1) + b"\x80\x00"
    payload = b"WAVE" + chunks
    return b"RIFF" + struct.pack("<I", len(payload)) + payload


def mp3_bytes():
    header = bytes.fromhex("fffb9000")
    frame_length = adapter._mp3_frame_length(header, 0)
    return header + bytes(frame_length - len(header))


def mp4_bytes():
    def box(kind, payload=b""):
        return struct.pack(">I4s", len(payload) + 8, kind) + payload

    ftyp = box(b"ftyp", b"isom" + struct.pack(">I", 0) + b"isom")
    return ftyp + box(b"moov") + box(b"mdat", b"\x00")


def jpeg_bytes():
    frame = b"\x08" + struct.pack(">HH", 1, 1) + b"\x01\x01\x11\x00"
    scan = b"\x01\x01\x00\x00\x3f\x00"
    return (
        b"\xff\xd8"
        + b"\xff\xc0" + struct.pack(">H", len(frame) + 2) + frame
        + b"\xff\xda" + struct.pack(">H", len(scan) + 2) + scan
        + b"\x00\xff\xd9"
    )


class MiniMaxMediaTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.codex_home = self.root / "codex"
        self.credential = self.root / "mock.dpapi"
        self.encrypted_blob = b"mock DPAPI ciphertext"
        self.credential.write_text(
            " \n" + base64.b64encode(self.encrypted_blob).decode("ascii") + "\n ", encoding="utf-8"
        )
        self.config_path = self.codex_home / adapter.CONFIG_RELATIVE_PATH
        self.config_path.parent.mkdir(parents=True)
        self.config_path.write_text(
            json.dumps(
                {
                    "version": 1,
                    "base_url": "https://api.minimax.cn",
                    "credential_file": str(self.credential),
                    "models": {
                        "text": "MiniMax-M3.1-Flash",
                        "speech": "speech-2.8-hd",
                        "video": "MiniMax-H3",
                        "image": "image-01",
                        "music": "music-3.0",
                    },
                    "music_enabled": False,
                    "request_timeout_seconds": 12,
                }
            ),
            encoding="utf-8",
        )
        self.request_dir = self.root / "requests"
        self.request_dir.mkdir()
        self.output_dir = self.root / "output"
        self.output_dir.mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def write_request(self, name, value):
        path = self.request_dir / name
        path.write_text(json.dumps(value), encoding="utf-8")
        return path

    def output(self, name):
        return self.output_dir / name

    def decode_mock_credential(self, _path):
        return b"mock-token-for-test"

    def test_dry_run_never_decrypts_or_sends_http(self):
        request = self.write_request("speech.json", {"text": "hello"})
        with mock.patch.object(adapter, "_decrypt_credential_file", side_effect=AssertionError("decrypted")), mock.patch.object(
            adapter, "_http_bytes", side_effect=AssertionError("network")
        ):
            result = adapter.execute("speech", self.codex_home, request, dry_run=True)
        self.assertTrue(result["dryRun"])
        self.assertEqual(result["endpoint"], "/v1/t2a_v2")

    def test_status_checks_presence_without_decryption(self):
        with mock.patch.object(adapter, "_decrypt_credential_file", side_effect=AssertionError("decrypted")):
            result = adapter.execute("status", self.codex_home)
        self.assertEqual(result["status"], "configured")
        self.assertTrue(result["credentialPresent"])
        self.assertFalse(result["credentialValidated"])
        self.assertIn("legacy paid users", result["musicRestriction"])

    def test_dpapi_file_is_strict_base64_text_before_unprotect(self):
        with mock.patch.object(adapter, "_dpapi_unprotect_blob", return_value=b"mock decrypted token") as unprotect:
            result = adapter._decrypt_credential_file(self.credential)
        self.assertEqual(result, b"mock decrypted token")
        unprotect.assert_called_once_with(self.encrypted_blob)

    def test_invalid_dpapi_base64_is_rejected_before_unprotect(self):
        self.credential.write_text("not valid base64!", encoding="utf-8")
        with mock.patch.object(adapter, "_dpapi_unprotect_blob") as unprotect:
            with self.assertRaises(adapter.AdapterError) as raised:
                adapter._decrypt_credential_file(self.credential)
        self.assertEqual(raised.exception.code, "missingcredential")
        unprotect.assert_not_called()

    def test_disabled_music_fails_before_credential_or_post(self):
        request = self.write_request("music.json", {"prompt": "quiet piano", "is_instrumental": True})
        with mock.patch.object(adapter, "_decrypt_credential_file", side_effect=AssertionError("decrypted")), mock.patch.object(
            adapter, "_http_bytes", side_effect=AssertionError("network")
        ):
            with self.assertRaises(adapter.AdapterError) as raised:
                adapter.execute("music", self.codex_home, request, self.output("music.mp3"))
        self.assertEqual(raised.exception.code, "unsupported")

    def test_enabled_music_saves_official_hex_audio_shape(self):
        config = json.loads(self.config_path.read_text(encoding="utf-8"))
        config["music_enabled"] = True
        self.config_path.write_text(json.dumps(config), encoding="utf-8")
        request = self.write_request("music.json", {"prompt": "quiet piano", "is_instrumental": True})
        mp3 = mp3_bytes()
        with mock.patch.object(adapter, "_decrypt_credential_file", side_effect=self.decode_mock_credential), mock.patch.object(
            adapter, "_api_json", return_value={"data": {"audio": mp3.hex(), "status": 2}}
        ) as api_call:
            result = adapter.execute("music", self.codex_home, request, self.output("music.mp3"))
        self.assertEqual(result["artifact"]["format"], "mp3")
        self.assertEqual(api_call.call_count, 1)
        self.assertEqual(api_call.call_args.args[1], "/v1/music_generation")

    def test_api_failure_does_not_retry_paid_post(self):
        request = self.write_request("speech.json", {"text": "hello"})

        class Opener:
            def __init__(self):
                self.calls = 0

            def open(self, req, timeout=None):
                self.calls += 1
                body = io.BytesIO(b"mock-secret failure")
                raise urllib.error.HTTPError(req.full_url, 500, "mock-secret", {}, body)

        opener = Opener()
        with mock.patch.object(adapter, "_decrypt_credential_file", side_effect=self.decode_mock_credential), mock.patch.object(
            adapter.urllib.request, "build_opener", return_value=opener
        ):
            with self.assertRaises(adapter.AdapterError) as raised:
                adapter.execute("speech", self.codex_home, request, self.output("speech.mp3"))
        self.assertEqual(raised.exception.code, "api_error")
        self.assertEqual(opener.calls, 1)
        self.assertNotIn("mock-secret", str(raised.exception))

    def test_speech_image_and_video_use_official_response_shapes(self):
        request_speech = self.write_request("speech.json", {"text": "hello", "audio_setting": {"format": "wav"}})
        wav = wav_bytes()
        request_image = self.write_request("image.json", {"prompt": "a red kite"})
        png = png_bytes(16, 9)
        request_video = self.write_request(
            "video.json",
            {"content": [{"type": "text", "text": "A quiet skyline at dawn"}], "duration": 5, "resolution": "768P", "ratio": "16:9"},
        )
        with mock.patch.object(adapter, "_decrypt_credential_file", side_effect=self.decode_mock_credential), mock.patch.object(
            adapter,
            "_api_json",
            side_effect=[
                {"data": {"audio": wav.hex(), "status": 2}, "base_resp": {"status_code": 0}},
                {"data": {"image_base64": [base64.b64encode(png).decode()]}, "metadata": {"success_count": 1}},
                {"task_id": "task_123"},
            ],
        ) as api_call:
            speech = adapter.execute("speech", self.codex_home, request_speech, self.output("speech.wav"))
            image = adapter.execute("image", self.codex_home, request_image, self.output("image.png"))
            video = adapter.execute("video", self.codex_home, request_video)
        self.assertEqual(speech["artifact"]["format"], "wav")
        self.assertEqual(image["artifact"]["width"], 16)
        self.assertEqual(image["artifact"]["height"], 9)
        self.assertEqual(image["artifact"]["validation"], "image-data-decoded")
        self.assertEqual(video["taskId"], "task_123")
        self.assertEqual(api_call.call_count, 3)
        self.assertEqual(api_call.call_args_list[-1].args[1], "/v2/video_generation")

    def test_truncated_png_is_malformed_and_never_saved(self):
        request = self.write_request("truncated-image.json", {"prompt": "a red kite"})
        output = self.output("truncated.png")
        truncated = png_bytes()[:-12]
        response = {"data": {"image_base64": [base64.b64encode(truncated).decode("ascii")]}}
        with mock.patch.object(adapter, "_decrypt_credential_file", side_effect=self.decode_mock_credential), mock.patch.object(
            adapter, "_api_json", return_value=response
        ):
            with self.assertRaises(adapter.AdapterError) as raised:
                adapter.execute("image", self.codex_home, request, output)
        self.assertEqual(raised.exception.code, "malformed_response")
        self.assertFalse(output.exists())

    def test_png_chunk_crc_and_jpeg_segments_are_validated(self):
        png = png_bytes(3, 2)
        image = adapter._identify_media(png)
        self.assertEqual((image["width"], image["height"]), (3, 2))
        self.assertEqual(image["validation"], "image-data-decoded")

        corrupt = bytearray(png)
        corrupt[-5] ^= 0x01
        with self.assertRaises(adapter.AdapterError) as crc_error:
            adapter._identify_media(bytes(corrupt))
        self.assertEqual(crc_error.exception.code, "malformed_response")

        invalid_stream = bytearray(png)
        idat_offset = 8 + 12 + 13
        idat_length = int.from_bytes(invalid_stream[idat_offset : idat_offset + 4], "big")
        payload_start = idat_offset + 8
        invalid_stream[payload_start] ^= 0xFF
        crc_offset = payload_start + idat_length
        invalid_stream[crc_offset : crc_offset + 4] = struct.pack(
            ">I", zlib.crc32(b"IDAT" + invalid_stream[payload_start:crc_offset]) & 0xFFFFFFFF
        )
        with self.assertRaises(adapter.AdapterError) as decode_error:
            adapter._identify_media(bytes(invalid_stream))
        self.assertEqual(decode_error.exception.code, "malformed_response")

        jpeg = jpeg_bytes()
        jpeg_metadata = adapter._identify_media(jpeg)
        self.assertEqual((jpeg_metadata["width"], jpeg_metadata["height"]), (1, 1))
        with self.assertRaises(adapter.AdapterError) as jpeg_error:
            adapter._identify_media(jpeg[:-2])
        self.assertEqual(jpeg_error.exception.code, "malformed_response")
        broken_segment = bytearray(jpeg)
        broken_segment[4:6] = b"\xff\xff"
        with self.assertRaises(adapter.AdapterError) as segment_error:
            adapter._identify_media(bytes(broken_segment))
        self.assertEqual(segment_error.exception.code, "malformed_response")

    def test_audio_and_video_metadata_claim_structure_only(self):
        for data in (wav_bytes(), mp3_bytes(), mp4_bytes()):
            metadata = adapter._identify_media(data)
            self.assertEqual(metadata["validation"], "container-structure")
            self.assertNotIn("playable", metadata)

    def test_video_submission_timeout_is_uncertain_and_never_retried(self):
        request = self.write_request("video.json", {"content": [{"type": "text", "text": "A forest"}], "duration": 5})
        class Opener:
            def open(self, _request, timeout=None):
                self.calls = getattr(self, "calls", 0) + 1
                raise TimeoutError("private detail")

        opener = Opener()
        with mock.patch.object(adapter, "_decrypt_credential_file", side_effect=self.decode_mock_credential), mock.patch.object(
            adapter.urllib.request, "build_opener", return_value=opener
        ):
            with self.assertRaises(adapter.AdapterError) as raised:
                adapter.execute("video", self.codex_home, request)
        self.assertEqual(raised.exception.code, "uncertain_submission")
        self.assertEqual(opener.calls, 1)

    def test_existing_video_task_is_get_only(self):
        request = self.write_request("task.json", {"task_id": "task_123"})
        with mock.patch.object(adapter, "_decrypt_credential_file", side_effect=self.decode_mock_credential), mock.patch.object(
            adapter,
            "_api_json",
            return_value={"task": {"id": "task_123", "status": "Processing"}},
        ) as api_call:
            result = adapter.execute("video-status", self.codex_home, request)
        self.assertEqual(result["status"], "running")
        self.assertEqual(api_call.call_args.args[2], "GET")
        self.assertEqual(api_call.call_count, 1)

    def test_existing_output_is_rejected_before_paid_post(self):
        request = self.write_request("image.json", {"prompt": "a red kite"})
        existing = self.output("existing.png")
        existing.write_bytes(b"keep me")
        with mock.patch.object(adapter, "_decrypt_credential_file", side_effect=AssertionError("decrypted")), mock.patch.object(
            adapter, "_api_json", side_effect=AssertionError("posted")
        ):
            with self.assertRaises(adapter.AdapterError) as raised:
                adapter.execute("image", self.codex_home, request, existing)
        self.assertEqual(raised.exception.code, "output_exists")
        self.assertEqual(existing.read_bytes(), b"keep me")

    def test_download_has_no_authorization_and_rejects_external_redirect(self):
        media = mp4_bytes()
        request = self.write_request("download.json", {"url": "https://cdn.hailuoai.com/files/result.mp4"})

        class Response:
            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

            def read(self, size=-1):
                return media[:size]

        class Opener:
            def open(self, req, timeout=None):
                self.request = req
                return Response()

        opener = Opener()
        with mock.patch.object(adapter.urllib.request, "build_opener", return_value=opener):
            result = adapter.execute("download", self.codex_home, request, self.output("result.mp4"))
        self.assertNotIn("Authorization", opener.request.headers)
        self.assertEqual(result["artifact"]["format"], "mp4")
        self.assertEqual(result["artifact"]["validation"], "container-structure")
        self.assertNotIn("playable", result["artifact"])

        handler = adapter.NoRedirectHandler()
        redirect = handler.redirect_request(
            opener.request,
            io.BytesIO(),
            302,
            "Found",
            {},
            "https://attacker.example/collect",
        )
        self.assertIsNone(redirect)

    def test_api_error_body_and_url_are_never_exposed(self):
        error_body = io.BytesIO(b"mock-secret")

        class Opener:
            def open(self, _request, timeout=None):
                raise urllib.error.HTTPError(
                    "https://api.minimax.cn/v1/image_generation?api_key=mock-secret",
                    500,
                    "failure mock-secret",
                    {},
                    error_body,
                )

        with mock.patch.object(adapter.urllib.request, "build_opener", return_value=Opener()):
            with self.assertRaises(adapter.AdapterError) as raised:
                adapter._http_bytes(
                    "https://api.minimax.cn/v1/image_generation?api_key=mock-secret",
                    "POST",
                    b"{}",
                    "mock-secret",
                    5,
                )
        self.assertEqual(raised.exception.code, "api_error")
        self.assertNotIn("mock-secret", str(raised.exception))
        self.assertNotIn("api.minimax.cn", str(raised.exception))

    def test_download_rejects_external_host_before_http(self):
        request = self.write_request("download.json", {"url": "https://attacker.example/file.mp4"})
        with mock.patch.object(adapter, "_http_bytes", side_effect=AssertionError("network")):
            with self.assertRaises(adapter.AdapterError) as raised:
                adapter.execute("download", self.codex_home, request, self.output("bad.mp4"))
        self.assertEqual(raised.exception.code, "unsupported")

    def test_model_override_must_match_configuration_before_decryption(self):
        request = self.write_request("speech.json", {"model": "other-model", "text": "hello"})
        with mock.patch.object(adapter, "_decrypt_credential_file", side_effect=AssertionError("decrypted")), mock.patch.object(
            adapter, "_api_json", side_effect=AssertionError("posted")
        ):
            with self.assertRaises(adapter.AdapterError) as raised:
                adapter.execute("speech", self.codex_home, request, self.output("speech.mp3"))
        self.assertEqual(raised.exception.code, "unsupported")

    def test_music_dry_run_shows_legacy_access_restriction(self):
        request = self.write_request("music.json", {"prompt": "quiet piano", "is_instrumental": True})
        with mock.patch.object(adapter, "_decrypt_credential_file", side_effect=AssertionError("decrypted")), mock.patch.object(
            adapter, "_http_bytes", side_effect=AssertionError("network")
        ):
            result = adapter.execute("music", self.codex_home, request, dry_run=True)
        self.assertFalse(result["available"])
        self.assertIn("discontinued", result["restriction"])


if __name__ == "__main__":
    unittest.main()
