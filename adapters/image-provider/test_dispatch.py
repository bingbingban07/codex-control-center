import argparse
import base64
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

HERE = Path(__file__).resolve().parent
DISPATCH = HERE / "image_dispatch.py"
PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
)


def load_dispatch():
    import importlib.util
    spec = importlib.util.spec_from_file_location("image_dispatch", DISPATCH)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


class FakeResponse(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()
        return False


class FakeOpener:
    def __init__(self, payload, calls):
        self.payload = payload
        self.calls = calls

    def open(self, request, timeout):
        self.calls.append((request, timeout))
        return FakeResponse(json.dumps(self.payload).encode("utf-8"))


class ImageDispatchTests(unittest.TestCase):
    def setUp(self):
        self.module = load_dispatch()

    def test_official_dry_run_does_not_read_credential(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            profile_dir = home / "control-center"
            profile_dir.mkdir(parents=True)
            (profile_dir / "image-profiles.json").write_text(json.dumps({
                "active_provider": "official",
                "profiles": {"official": {
                    "provider": "official",
                    "base_url": "https://api.openai.com/v1",
                    "model": "gpt-image-2.5-sunburst",
                    "size": "1x1",
                    "quality": "high",
                    "format": "png",
                    "timeout": 600,
                    "credential_file": str(home / "secrets" / "not-yet.dpapi"),
                }}
            }), encoding="utf-8")
            args = argparse.Namespace(
                prompt="test", prompt_file=None, out=home / "out.png", image=[],
                size=None, quality=None, format=None, timeout=None, dry_run=True)
            with mock.patch.object(self.module, "decrypt_dpapi", side_effect=AssertionError("credential read")):
                result = self.module.run_official(
                    home, self.module.read_profile(home)[1], args, "test", home / "out.png")
            self.assertTrue(result["dry_run"])
            self.assertFalse((home / "out.png").exists())
            self.assertFalse((home / "out.json").exists())

    def test_official_model_does_not_inherit_i2m_alias(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            (home / "skills" / "i2m-imagegen" / "scripts").mkdir(parents=True)
            (home / "skills" / "i2m-imagegen" / "config.json").write_text(json.dumps({
                "base_url": "https://sub.114514.one/v1",
                "model": "third-party-model-alias",
                "credential_file": str(home / "secrets" / "i2m.dpapi"),
                "size": "1024x1024",
                "quality": "high",
                "output_format": "png",
                "timeout": 600,
            }), encoding="utf-8")
            (home / "control-center").mkdir()
            (home / "control-center" / "image-profiles.json").write_text(json.dumps({
                "active_provider": "official",
                "profiles": {"official": {"provider": "official"}}
            }), encoding="utf-8")
            provider, profile = self.module.read_profile(home)
            self.assertEqual(provider, "official")
            self.assertEqual(profile["model"], "gpt-image-2.5-sunburst")
            self.assertEqual(profile["base_url"], "https://api.openai.com/v1")

    def test_i2m_dry_run_passes_matching_helper_arguments(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            helper = home / "skills" / "i2m-imagegen" / "scripts" / "image_api.py"
            helper.parent.mkdir(parents=True)
            helper.write_text(
                "import json,sys\n"
                "json.dump(sys.argv[1:],open(sys.argv[sys.argv.index('--out')+1]+'.argv.json','w'))\n"
                "print(json.dumps({'dry_run':True}))\n",
                encoding="utf-8")
            (home / "control-center").mkdir()
            (home / "control-center" / "image-profiles.json").write_text(json.dumps({
                "active_provider": "i2m",
                "profiles": {"i2m": {
                    "provider": "i2m",
                    "base_url": "https://sub.114514.one/v1",
                    "model": "gpt-image-2.5-sunburst",
                    "size": "1024x1024",
                    "quality": "high",
                    "format": "png",
                    "timeout": 600,
                    "credential_file": str(home / "secrets" / "control-center-image-i2m.dpapi"),
                }}
            }), encoding="utf-8")
            out = home / "out.png"
            result = subprocess.run(
                [sys.executable, str(DISPATCH), "--codex-home", str(home),
                 "--prompt", "hello", "--out", str(out), "--dry-run"],
                text=True, capture_output=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            argv = json.loads((home / "out.png.argv.json").read_text(encoding="utf-8"))
            self.assertIn("--prompt", argv)
            self.assertIn("hello", argv)
            self.assertIn("--size", argv)
            self.assertIn("1024x1024", argv)
            self.assertIn("--dry-run", argv)
            self.assertFalse(out.exists())

    def test_i2m_success_without_artifacts_is_not_reported_as_success(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            helper = home / "skills" / "i2m-imagegen" / "scripts" / "image_api.py"
            helper.parent.mkdir(parents=True)
            helper.write_text("raise SystemExit(0)\n", encoding="utf-8")
            config = home / "skills" / "i2m-imagegen" / "config.json"
            config.write_text(json.dumps({
                "credential_file": str(home / "secrets" / "i2m.dpapi")
            }), encoding="utf-8")
            out = home / "out.png"
            profile = {
                "provider": "i2m", "size": "1x1", "quality": "high",
                "format": "png", "timeout": 600,
                "credential_file": str(home / "secrets" / "i2m.dpapi"),
            }
            args = argparse.Namespace(
                prompt="test", prompt_file=None, out=out, image=[],
                timeout=None, dry_run=False)
            with self.assertRaises(self.module.DispatchError):
                self.module.run_i2m(home, profile, args, "test", out)
            self.assertFalse(out.exists())
            self.assertFalse(out.with_suffix(".json").exists())

    def test_i2m_jpg_request_reports_helpers_jpeg_output_path(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            helper = home / "skills" / "i2m-imagegen" / "scripts" / "image_api.py"
            helper.parent.mkdir(parents=True)
            helper.write_text(
                "from pathlib import Path\n"
                "import sys\n"
                "out = Path(sys.argv[sys.argv.index('--out') + 1])\n"
                "out.with_suffix('.jpeg').write_bytes(b'\\xff\\xd8\\xfftest')\n"
                "out.with_suffix('.json').write_text('{}', encoding='utf-8')\n",
                encoding="utf-8")
            config = home / "skills" / "i2m-imagegen" / "config.json"
            config.write_text(json.dumps({
                "credential_file": str(home / "secrets" / "i2m.dpapi")
            }), encoding="utf-8")
            out = home / "out.jpg"
            profile = {
                "provider": "i2m", "size": "1x1", "quality": "high",
                "format": "jpeg", "timeout": 600,
                "credential_file": str(home / "secrets" / "i2m.dpapi"),
            }
            args = argparse.Namespace(
                prompt="test", prompt_file=None, out=out, image=[],
                timeout=None, dry_run=False)
            result = self.module.run_i2m(home, profile, args, "test", out)
            actual = out.with_suffix(".jpeg")
            self.assertEqual(result["outputs"], [str(actual)])
            self.assertTrue(actual.is_file())
            self.assertEqual(result["receipt"], str(out.with_suffix(".json")))

    def test_reference_image_content_must_match_extension(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            image = home / "reference.png"
            image.write_bytes(b"not a PNG")
            profile = {
                "provider": "official", "size": "1x1", "quality": "high",
                "format": "png", "timeout": 600,
            }
            with self.assertRaises(self.module.DispatchError):
                self.module.validate_request(profile, "test", home / "out.png", [image], 600)

    def test_multipart_rechecks_reference_content_before_decryption(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            image = home / "reference.png"
            image.write_bytes(b"not a PNG")
            out = home / "out.png"
            profile = {
                "provider": "official",
                "base_url": "https://api.openai.com/v1",
                "model": "gpt-image-2.5-sunburst",
                "size": "1x1", "quality": "high", "format": "png",
                "timeout": 600,
                "credential_file": str(home / "secrets" / "official.dpapi"),
            }
            args = argparse.Namespace(
                prompt="test", prompt_file=None, out=out, image=[image],
                timeout=None, dry_run=False)
            with mock.patch.object(self.module, "decrypt_dpapi", return_value="sk-test") as decrypt, \
                 mock.patch.object(self.module.urllib.request, "build_opener") as build:
                with self.assertRaises(self.module.DispatchError):
                    self.module.run_official(home, profile, args, "test", out)
            decrypt.assert_not_called()
            build.assert_not_called()

    def test_prompt_file_errors_are_dispatch_errors(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(self.module.DispatchError):
                self.module.read_prompt_file(Path(directory) / "missing prompt.txt")

    def test_invalid_configured_timeout_is_rejected_as_input(self):
        for value in (True, 1.5, "slow"):
            with self.subTest(value=value):
                with self.assertRaises(self.module.DispatchError):
                    self.module.parse_timeout(value)

    def test_official_invalid_output_directory_fails_before_request(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            parent = home / "not-a-directory"
            parent.write_text("file", encoding="utf-8")
            out = parent / "out.png"
            profile = {
                "provider": "official",
                "base_url": "https://api.openai.com/v1",
                "model": "gpt-image-2.5-sunburst",
                "size": "1x1", "quality": "high", "format": "png",
                "timeout": 600,
                "credential_file": str(home / "secrets" / "official.dpapi"),
            }
            args = argparse.Namespace(
                prompt="test", prompt_file=None, out=out, image=[],
                timeout=None, dry_run=False)
            opener = FakeOpener({"data": [{"b64_json": base64.b64encode(PNG).decode("ascii")}]}, [])
            with mock.patch.object(self.module, "decrypt_dpapi", return_value="sk-test") as decrypt, \
                 mock.patch.object(self.module.urllib.request, "build_opener", return_value=opener) as build:
                with self.assertRaises(self.module.DispatchError):
                    self.module.run_official(home, profile, args, "test", out)
            decrypt.assert_not_called()
            build.assert_not_called()

    def test_official_unwritable_output_directory_fails_before_request(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            out = home / "out.png"
            profile = {
                "provider": "official",
                "base_url": "https://api.openai.com/v1",
                "model": "gpt-image-2.5-sunburst",
                "size": "1x1", "quality": "high", "format": "png",
                "timeout": 600,
                "credential_file": str(home / "secrets" / "official.dpapi"),
            }
            args = argparse.Namespace(
                prompt="test", prompt_file=None, out=out, image=[],
                timeout=None, dry_run=False)
            with mock.patch.object(self.module.tempfile, "TemporaryFile", side_effect=PermissionError("write denied")), \
                 mock.patch.object(self.module, "decrypt_dpapi", return_value="sk-test") as decrypt, \
                 mock.patch.object(self.module.urllib.request, "build_opener") as build:
                with self.assertRaises(self.module.DispatchError):
                    self.module.run_official(home, profile, args, "test", out)
            decrypt.assert_not_called()
            build.assert_not_called()

    def test_official_response_body_is_bounded(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            out = home / "out.png"
            profile = {
                "provider": "official",
                "base_url": "https://api.openai.com/v1",
                "model": "gpt-image-2.5-sunburst",
                "size": "1x1", "quality": "high", "format": "png",
                "timeout": 600,
                "credential_file": str(home / "secrets" / "official.dpapi"),
            }
            args = argparse.Namespace(
                prompt="test", prompt_file=None, out=out, image=[],
                timeout=None, dry_run=False)
            calls = []
            opener = FakeOpener({"data": [{"b64_json": base64.b64encode(PNG).decode("ascii")}]}, calls)
            with mock.patch.object(self.module, "MAX_API_RESPONSE_BYTES", 8), \
                 mock.patch.object(self.module, "decrypt_dpapi", return_value="sk-test"), \
                 mock.patch.object(self.module.urllib.request, "build_opener", return_value=opener):
                with self.assertRaises(self.module.DispatchError):
                    self.module.run_official(home, profile, args, "test", out)
            self.assertEqual(len(calls), 1)
            self.assertFalse(out.exists())

    def test_official_rejects_credential_path_outside_managed_secret_roots(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            out = home / "out.png"
            profile = {
                "provider": "official",
                "base_url": "https://api.openai.com/v1",
                "model": "gpt-image-2.5-sunburst",
                "size": "1x1", "quality": "high", "format": "png",
                "timeout": 600,
                "credential_file": str(home / "unmanaged.dpapi"),
            }
            args = argparse.Namespace(
                prompt="test", prompt_file=None, out=out, image=[],
                timeout=None, dry_run=False)
            with mock.patch.object(self.module, "decrypt_dpapi", return_value="sk-test") as decrypt:
                with self.assertRaises(self.module.DispatchError):
                    self.module.run_official(home, profile, args, "test", out)
            decrypt.assert_not_called()

    def test_official_mock_http_writes_one_valid_image(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            out = home / "out.png"
            profile = {
                "provider": "official",
                "base_url": "https://api.openai.com/v1",
                "model": "gpt-image-2.5-sunburst",
                "size": "1x1",
                "quality": "high",
                "format": "png",
                "timeout": 600,
                "credential_file": str(home / "secrets" / "control-center-image-official.dpapi"),
            }
            args = argparse.Namespace(
                prompt="test", prompt_file=None, out=out, image=[],
                size=None, quality=None, format=None, timeout=None, dry_run=False)
            calls = []
            opener = FakeOpener({"data": [{"b64_json": base64.b64encode(PNG).decode("ascii")}]}, calls)
            with mock.patch.object(self.module, "decrypt_dpapi", return_value="sk-test"), \
                 mock.patch.object(self.module.urllib.request, "build_opener", return_value=opener):
                result = self.module.run_official(home, profile, args, "test", out)
            self.assertEqual(len(calls), 1)
            self.assertEqual(calls[0][0].full_url, "https://api.openai.com/v1/images/generations")
            self.assertEqual(tuple(result["image_dimensions"]), (1, 1))
            self.assertEqual(out.read_bytes(), PNG)
            self.assertTrue((home / "out.json").exists())

    def test_official_rejects_image_url_without_second_request(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            out = home / "out.png"
            profile = {
                "provider": "official",
                "base_url": "https://api.openai.com/v1",
                "model": "gpt-image-2.5-sunburst",
                "size": "1x1",
                "quality": "high",
                "format": "png",
                "timeout": 600,
                "credential_file": str(home / "secrets" / "control-center-image-official.dpapi"),
            }
            args = argparse.Namespace(
                prompt="test", prompt_file=None, out=out, image=[],
                size=None, quality=None, format=None, timeout=None, dry_run=False)
            calls = []
            opener = FakeOpener({"data": [{"url": "https://example.invalid/image.png"}]}, calls)
            with mock.patch.object(self.module, "decrypt_dpapi", return_value="sk-test"), \
                 mock.patch.object(self.module.urllib.request, "build_opener", return_value=opener):
                with self.assertRaises(self.module.DispatchError):
                    self.module.run_official(home, profile, args, "test", out)
            self.assertEqual(len(calls), 1)
            self.assertFalse(out.exists())


if __name__ == "__main__":
    unittest.main()

