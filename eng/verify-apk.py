#!/usr/bin/env python3
"""Verify package identity, signing certificate, versions and 16 KB ABI alignment."""
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
windows = os.name == 'nt'
signer_path = tools / ('apksigner.bat' if windows else 'apksigner')
signer = subprocess.check_output([str(signer_path), 'verify', '--verbose', '--print-certs', str(apk)], text=True)
key = subprocess.check_output(['keytool', '-J-Duser.language=en', '-list', '-v', '-keystore', str(keystore), '-alias', alias, '-storepass:env', 'ANDROID_KEYSTORE_PASSWORD'], text=True)
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
with zipfile.ZipFile(apk) as archive:
    for abi in abis:
        assert f'lib/{abi}/libsrtdroid.so' in archive.namelist(), f'{abi}: missing native SRT library'
badging = subprocess.check_output([str(tools / ('aapt.exe' if windows else 'aapt')), 'dump', 'badging', str(apk)], text=True)
assert "name='nl.deve.livestreamer'" in badging
assert "sdkVersion:'29'" in badging
if len(sys.argv) > 4:
    assert f"versionCode='{sys.argv[4]}'" in badging, 'APK version code differs from the requested build number'
if len(sys.argv) > 5:
    assert f"versionName='{sys.argv[5]}'" in badging, 'APK display version differs from the requested version'
alignment = subprocess.check_output([str(tools / ('zipalign.exe' if windows else 'zipalign')), '-c', '-P', '16', '-v', '4', str(apk)], text=True)
(apk.parent / 'alignment.txt').write_text(alignment)
digest = hashlib.sha256(apk.read_bytes()).hexdigest()
apk.with_suffix('.apk.sha256').write_text(f'{digest}  {apk.name}\n')
report = {'apk': apk.name, 'bytes': apk.stat().st_size, 'sha256': digest, 'certificate_sha256': actual, 'abis': abis, 'minimum_android_api': 29, 'elf_libraries_checked': native_count, 'elf_16kb_alignment': True}
apk.with_suffix('.apk.json').write_text(json.dumps(report, indent=2) + '\n')
print(f'Verified {apk.name}: signing identity, version, Android 10+, ARM64/x86-64, native SRT and 16 KB alignment.')
