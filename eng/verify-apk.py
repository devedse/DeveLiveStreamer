#!/usr/bin/env python3
"""Verify the actual package identity, signature certificate, ABIs and checksum."""
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import sys
import zipfile

apk, keystore = map(Path, sys.argv[1:3])
alias = sys.argv[3]
sdk = Path(os.environ['ANDROID_HOME'])
tools = sorted((sdk / 'build-tools').iterdir(), key=lambda p: tuple(int(x) for x in p.name.split('.') if x.isdigit()))[-1]
signer = subprocess.check_output([str(tools / 'apksigner'), 'verify', '--print-certs', str(apk)], text=True)
key = subprocess.check_output(['keytool', '-list', '-v', '-keystore', str(keystore), '-alias', alias, '-storepass:env', 'ANDROID_KEYSTORE_PASSWORD'], text=True)
actual = re.search(r'certificate SHA-256 digest:\s*([a-fA-F0-9]+)', signer).group(1).lower()
expected = re.search(r'SHA256:\s*([a-fA-F0-9:]+)', key).group(1).replace(':', '').lower()
assert actual == expected, 'APK signing identity does not match the selected keystore'
with zipfile.ZipFile(apk) as archive:
    assert archive.testzip() is None
    abis = sorted({name.split('/')[1] for name in archive.namelist() if name.startswith('lib/') and name.endswith('.so')})
    native_count = 0
    for name in archive.namelist():
        if not name.endswith('.so'):
            continue
        binary = archive.read(name)
        if binary[:4] != b'\x7fELF' or binary[4] != 2:
            continue
        native_count += 1
        offset = struct.unpack_from('<Q', binary, 32)[0]
        size, count = struct.unpack_from('<HH', binary, 54)
        for index in range(count):
            base = offset + size * index
            if struct.unpack_from('<I', binary, base)[0] == 1:
                alignment = struct.unpack_from('<Q', binary, base + 48)[0]
                assert alignment >= 16384, f'{name}: native segment is not aligned for 16 KB pages'
assert 'arm64-v8a' in abis and 'x86_64' in abis, abis
badging = subprocess.check_output([str(tools / 'aapt'), 'dump', 'badging', str(apk)], text=True)
assert "name='nl.deve.livestreamer'" in badging
assert "sdkVersion:'29'" in badging
digest = hashlib.sha256(apk.read_bytes()).hexdigest()
apk.with_suffix('.apk.sha256').write_text(f'{digest}  {apk.name}\n')
report = {'apk': apk.name, 'bytes': apk.stat().st_size, 'sha256': digest, 'certificate_sha256': actual, 'abis': abis, 'minimum_android_api': 29, 'elf_libraries_checked': native_count, 'elf_16kb_alignment': True}
apk.with_suffix('.apk.json').write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2))
