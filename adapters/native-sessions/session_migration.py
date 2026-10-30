"""Unify stored user-chat bindings without rewriting message history or credentials."""
from __future__ import annotations

import argparse
import contextlib
import datetime
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import tomllib
import uuid


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.tmp-' + uuid.uuid4().hex)
    try:
        temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def normalize(path):
    return os.path.normcase(str(Path(path).resolve()).removeprefix('\\\\?\\'))


def header_for(line, thread_id, provider):
    value = json.loads(line)
    if value.get('type') != 'session_meta' or value.get('payload', {}).get('id') != thread_id:
        raise ValueError('Unexpected session metadata: ' + thread_id)
    value['payload']['model_provider'] = provider
    encoded = json.dumps(value, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    ending = b'\r\n' if line.endswith(b'\r\n') else b'\n'
    if len(encoded) + len(ending) > len(line):
        raise ValueError('Metadata does not fit original header: ' + thread_id)
    return encoded + b' ' * (len(line) - len(encoded) - len(ending)) + ending


@contextlib.contextmanager
def locked_header(path, expected):
    with path.open('r+b', buffering=0) as stream:
        if os.name == 'nt':
            import msvcrt
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, len(expected))
        try:
            stream.seek(0)
            if stream.read(len(expected)) != expected:
                raise ValueError('Session header changed during migration: ' + path.name)
            yield stream
        finally:
            if os.name == 'nt':
                stream.seek(0)
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, len(expected))


def write_header(stream, value):
    stream.seek(0)
    if stream.write(value) != len(value):
        raise OSError('Incomplete session header write')
    stream.flush()
    os.fsync(stream.fileno())


def migrate(home, apply=False, finish=False):
    home = Path(home).resolve()
    config = tomllib.loads((home / 'config.toml').read_text(encoding='utf-8-sig'))
    target = {key: config.get(key) for key in ['model_provider', 'model', 'model_reasoning_effort']}
    unified_path=home/'control-center/unified-routing.json'
    unified=json.loads(unified_path.read_text(encoding='utf-8')) if unified_path.exists() else {}
    supported=target['model_provider'] in ['openai','openai_http'] or (unified.get('enabled') and target['model_provider']==unified.get('provider'))
    if not supported or not target['model']:
        raise ValueError('The current default is not native sign-in or the explicitly configured unified route')
    state_dir = home / 'control-center' / 'native-session-migration'
    state_path = state_dir / 'state.json'
    scope = None
    if finish:
        if not state_path.exists():
            return {'state': 'nothing_pending'}
        state = json.loads(state_path.read_text(encoding='utf-8'))
        if not state.get('reloadRequired'):
            return {'state': 'complete'}
        scope = set(state['threadIds'])
    database = home / 'state_5.sqlite'
    if not database.is_file():
        raise FileNotFoundError('Codex thread metadata database is missing')
    db = sqlite3.connect(database.as_uri() + ('?mode=rw' if apply else '?mode=ro'), uri=True, timeout=20)
    db.row_factory = sqlite3.Row
    handles, written, committed = contextlib.ExitStack(), [], False
    try:
        if apply:
            db.execute('BEGIN IMMEDIATE')
        rows = [dict(row) for row in db.execute('SELECT id,rollout_path,source,model_provider,model,reasoning_effort FROM threads')
                if 'subagent' not in row['source'].lower() and (scope is None or row['id'] in scope)]
        files, missing, metadata_rows = [], [], []
        allowed = [normalize(base / name) + os.sep for base in [home, home / 'plus-desktop-home']
                   for name in ['sessions', 'archived_sessions']]
        for row in rows:
            path = Path(row['rollout_path'])
            if path.is_file():
                if not any(normalize(path).startswith(prefix) for prefix in allowed):
                    raise ValueError('Session path is outside Codex history: ' + row['id'])
                with path.open('rb') as source:
                    original = source.readline(8 * 1024 * 1024)
                if not original.endswith(b'\n'):
                    raise ValueError('Invalid or oversized metadata header: ' + row['id'])
                replacement = header_for(original, row['id'], target['model_provider'])
                if json.loads(original)['payload'].get('model_provider') != target['model_provider']:
                    files.append((row, path, original, replacement))
            else:
                missing.append(row['id'])
            if (row['model_provider'], row['model'], row['reasoning_effort']) != tuple(target.values()):
                metadata_rows.append(row)
        result = {'state': 'planned', 'target': target, 'userChats': len(rows), 'headersToUpdate': len(files),
                  'metadataRowsToUpdate': len(metadata_rows), 'existingMissingHistoryFiles': len(missing),
                  'subagentHistoryChanged': False, 'inferenceRequests': 0, 'reloadRequired': True}
        if not apply:
            return result
        backup = state_dir / 'backups' / uuid.uuid4().hex
        backup.mkdir(parents=True)
        manifest = {'target': target, 'rows': metadata_rows, 'headers': [], 'existingMissingHistoryIds': missing}
        # Lock and validate every header before writing any. Appends beyond the header
        # remain possible, and message byte offsets and line endings never move.
        for index, (row, path, original, replacement) in enumerate(files):
            stream = handles.enter_context(locked_header(path, original))
            saved = backup / f'header-{index}.bin'
            saved.write_bytes(original)
            manifest['headers'].append({'threadId': row['id'], 'path': str(path), 'backup': saved.name,
                'length': len(original), 'beforeSha256': hashlib.sha256(original).hexdigest(),
                'afterSha256': hashlib.sha256(replacement).hexdigest()})
            files[index] = (row, stream, original, replacement)
        write_json(backup / 'manifest.json', manifest)
        for row, stream, original, replacement in files:
            written.append((stream, original))
            write_header(stream, replacement)
        for row in metadata_rows:
            db.execute('UPDATE threads SET model_provider=?,model=?,reasoning_effort=? WHERE id=?', (*target.values(), row['id']))
        db.commit()
        committed = True
        result.update(state='applied', backup=str(backup), reloadRequired=not finish)
        write_json(backup / 'result.json', result)
        write_json(state_path, {**result, 'threadIds': [r['id'] for r in rows],
            'completedAt': datetime.datetime.now(datetime.timezone.utc).isoformat()})
        return result
    except BaseException:
        if not committed:
            db.rollback()
            for stream, original in reversed(written):
                write_header(stream, original)
        raise
    finally:
        handles.close()
        db.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--codex-home', required=True)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--finish-pending', action='store_true')
    args = parser.parse_args()
    try:
        print(json.dumps(migrate(args.codex_home, args.apply or args.finish_pending, args.finish_pending), ensure_ascii=False))
    except Exception as error:
        print(json.dumps({'state': 'failed', 'error': type(error).__name__, 'detail': str(error)}, ensure_ascii=False))
        raise SystemExit(1)
