#!/usr/bin/env python3
"""Regenerate the asammdf-written test fixtures in tests/Mdf4Net.Tests/Fixtures/asammdf.

    python3 eng/interop/make_fixtures.py

The fixtures let the unit tests check the reader against files from an independent writer
without needing Python at test time. They are the same files eng/interop/check_asammdf.py writes,
with fewer samples (raw_virtual_group is laid out by hand and cross-checked with asammdf). Regenerate them when the asammdf pin in requirements.txt moves, and commit
the result together with the pin.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import asammdf  # noqa: E402
from check_asammdf import REPOSITORY_ROOT, write_asammdf_files  # noqa: E402

target = REPOSITORY_ROOT / 'tests' / 'Mdf4Net.Tests' / 'Fixtures' / 'asammdf'
target.mkdir(parents=True, exist_ok=True)
for old in list(target.glob('*.mf4')) + list(target.glob('*.json')):
    old.unlink()
write_asammdf_files(target, count=200)
(target / 'VERSION').write_text(f'asammdf {asammdf.__version__}\n')
print(f'wrote fixtures with asammdf {asammdf.__version__} to {target.relative_to(REPOSITORY_ROOT)}')
