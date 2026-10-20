#!/usr/bin/env python3
"""Interoperability check between Mdf4Net and asammdf, in both directions.

    check_asammdf.py [--configuration Release] [--keep DIR]

1. Mdf4Net -> asammdf: tools/Mdf4Net.InteropProbe writes one .mf4 per scenario plus a .json with
   the values any conforming reader must see. asammdf opens each file and every channel (and CAN
   frame) is compared with the JSON.
2. asammdf -> Mdf4Net: this script writes files with asammdf (data types, conversions, strings,
   several groups, compression, MDF 4.20) plus a .json each, and the probe's `verify` command
   reads them with Mdf4Net and compares.

asammdf is the most widely used open MDF implementation and is independent of this code base, so
agreement in both directions is the strongest conformance evidence available without the ASAM
specification's own test suite. The asammdf version is pinned in eng/interop/requirements.txt.

Exit code 0 when every file agrees, 1 otherwise.
"""

import argparse
import json
import math
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np
from asammdf import MDF, Signal

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent.parent
PROBE = REPOSITORY_ROOT / 'tools' / 'Mdf4Net.InteropProbe' / 'Mdf4Net.InteropProbe.csproj'


def run_probe(configuration: str, command: str, directory: Path) -> int:
    result = subprocess.run(
        ['dotnet', 'run', '--project', str(PROBE), '-c', configuration, '--', command, str(directory)],
        cwd=REPOSITORY_ROOT)
    return result.returncode


# --- Mdf4Net -> asammdf --------------------------------------------------------------------------

def as_python(sample):
    """One asammdf sample as int/float, str or bytes."""
    if isinstance(sample, (bytes, bytearray)):
        return bytes(sample)
    if isinstance(sample, np.ndarray):
        return sample.astype(np.uint8).tobytes()
    if isinstance(sample, (np.bytes_,)):
        return bytes(sample)
    if isinstance(sample, (np.str_, str)):
        return str(sample)
    if isinstance(sample, (np.floating, float)):
        return float(sample)
    if isinstance(sample, (np.integer, int)):
        return int(sample)
    return sample


def same(actual, expected) -> bool:
    if isinstance(expected, dict):
        wanted = bytes.fromhex(expected['hex'])
        # asammdf returns variable-length byte arrays as one 2-D array, zero-padded to the
        # longest sample, so a shorter sample comes back with trailing zeros.
        return isinstance(actual, bytes) and actual[:len(wanted)] == wanted and not actual[len(wanted):].strip(b'\0')
    if isinstance(expected, str):
        if isinstance(actual, bytes):
            actual = actual.rstrip(b'\0').decode('utf-8')
        return actual == expected
    if isinstance(actual, (int, float)):
        return math.isclose(float(actual), float(expected), rel_tol=1e-9, abs_tol=1e-9)
    return False


def check_with_asammdf(mf4: Path, expected: dict) -> list:
    problems = []
    mdf = MDF(str(mf4))
    try:
        for channel in expected['channels']:
            label = f"{channel['group']}/{channel['name']}"
            try:
                signal = mdf.get(channel['name'], group=channel['group'])
            except Exception as error:  # asammdf raises several types for a missing channel
                problems.append(f'{label}: asammdf cannot read it ({error})')
                continue

            if channel.get('unit') and signal.unit != channel['unit']:
                problems.append(f"{label}: unit {signal.unit!r}, expected {channel['unit']!r}")
            if len(signal.samples) != len(channel['values']):
                problems.append(f"{label}: {len(signal.samples)} samples, expected {len(channel['values'])}")
                continue
            if not np.allclose(signal.timestamps, channel['timestamps'], rtol=1e-12, atol=1e-12):
                problems.append(f'{label}: time stamps differ')
            for index, (sample, value) in enumerate(zip(signal.samples, channel['values'])):
                actual = as_python(sample)
                if not same(actual, value):
                    problems.append(f'{label}[{index}]: {actual!r}, expected {value!r}')
                    break

        if expected.get('can'):
            problems += check_can(mdf, expected['can'])
            if mf4.stem == 'can_pdo':
                problems += check_dbc_decoding(mdf, expected['can'])
    finally:
        mdf.close()
    return problems


def check_can(mdf: MDF, frames: list) -> list:
    names = ['ID', 'IDE', 'DLC', 'DataLength', 'BusChannel', 'Dir', 'EDL', 'BRS', 'DataBytes']
    try:
        signals = {name: mdf.get(f'CAN_DataFrame.{name}') for name in names}
    except Exception as error:
        return [f'CAN channels cannot be read ({error})']

    if len(signals['ID'].samples) != len(frames):
        return [f"{len(signals['ID'].samples)} CAN frames, expected {len(frames)}"]

    for index, frame in enumerate(frames):
        data = bytes.fromhex(frame['data'])
        actual = {
            't': float(signals['ID'].timestamps[index]),
            'id': int(signals['ID'].samples[index]) & 0x1FFFFFFF,
            'ext': bool(signals['IDE'].samples[index]),
            'fd': bool(signals['EDL'].samples[index]),
            'brs': bool(signals['BRS'].samples[index]),
            'bus': int(signals['BusChannel'].samples[index]),
            'tx': bool(signals['Dir'].samples[index]),
            'dlc': int(signals['DLC'].samples[index]),
            'data': as_python(signals['DataBytes'].samples[index])[:int(signals['DataLength'].samples[index])].hex().upper(),
        }
        wanted = {key: frame[key] for key in actual}
        wanted['data'] = data.hex().upper()
        if not math.isclose(actual['t'], wanted['t'], abs_tol=1e-12) or {k: v for k, v in actual.items() if k != 't'} != {k: v for k, v in wanted.items() if k != 't'}:
            return [f'CAN frame {index}: {actual}, expected {wanted}']
    return []


PDO_DBC = '''VERSION ""

NS_ :

BS_:

BU_: Node1

BO_ 385 TPDO1_Node1: 3 Node1
 SG_ Speed : 0|16@1+ (0.5,0) [0|32767.5] "rpm" Vector__XXX
 SG_ Counter : 16|8@1+ (1,0) [0|255] "" Vector__XXX

'''


def check_dbc_decoding(mdf: MDF, frames: list) -> list:
    """Decodes the PDO with a DBC through extract_bus_logging and compares the signals."""
    pdo = [f for f in frames if f['id'] == 0x181]
    speed = [int.from_bytes(bytes.fromhex(f['data'])[0:2], 'little') * 0.5 for f in pdo]
    counter = [bytes.fromhex(f['data'])[2] for f in pdo]
    with tempfile.TemporaryDirectory() as scratch:
        dbc = Path(scratch) / 'pdo.dbc'
        dbc.write_text(PDO_DBC)
        decoded = mdf.extract_bus_logging({'CAN': [(str(dbc), 0)]})
    found = {}
    for channel in decoded.iter_channels():
        if channel.name.endswith('Speed') or channel.name.endswith('Counter'):
            found[channel.name.rsplit('.', 1)[-1]] = channel
    problems = []
    for name, wanted in (('Speed', speed), ('Counter', counter)):
        signal = found.get(name)
        if signal is None:
            problems.append(f'DBC decoding produced no {name} signal')
        elif len(signal.samples) != len(wanted) or not np.allclose(signal.samples, wanted):
            problems.append(f'DBC-decoded {name}: {list(signal.samples[:5])}…, expected {wanted[:5]}…')
        elif not np.allclose(signal.timestamps, [f['t'] for f in pdo]):
            problems.append(f'DBC-decoded {name}: time stamps differ')
    return problems


# --- asammdf -> Mdf4Net --------------------------------------------------------------------------

def expectation(group: int, name: str, timestamps, values, unit=None) -> dict:
    def to_json(value):
        if isinstance(value, (bytes, bytearray)):
            return {'hex': bytes(value).hex()}
        if isinstance(value, str):
            return value
        return float(value)
    return {
        'group': group,
        'name': name,
        'unit': unit,
        'timestamps': [float(t) for t in timestamps],
        'values': [to_json(v) for v in values],
    }


def numeric_signals(count: int):
    t = np.arange(count, dtype=np.float64) * 0.002
    i = np.arange(count, dtype=np.int64)
    raw = {
        'a_u8': (i % 256).astype(np.uint8),
        'a_u16': ((i * 37) % 65536).astype(np.uint16),
        'a_u32': (i * 65537).astype(np.uint32),
        'a_u64': (i * 4294967297).astype(np.uint64),
        'a_i8': ((i % 256) - 128).astype(np.int8),
        'a_i16': (i * 11 - 16000).astype(np.int16),
        'a_i32': (i * -70001).astype(np.int32),
        'a_i64': (i * 1000000007 - 9000000000).astype(np.int64),
        'a_f32': (i * 0.5).astype(np.float32),
        'a_f64': np.sin(i * 0.01) * 1000,
    }
    signals = [Signal(samples=v, timestamps=t, name=k, unit='u' if k == 'a_f64' else '') for k, v in raw.items()]
    expected = [expectation(0, k, t, v.astype(np.float64), 'u' if k == 'a_f64' else None) for k, v in raw.items()]

    linear_raw = (i % 1000).astype(np.uint16)
    signals.append(Signal(samples=linear_raw, timestamps=t, name='a_linear', unit='km/h',
                          conversion={'a': 0.25, 'b': -10.0}))
    expected.append(expectation(0, 'a_linear', t, linear_raw * 0.25 - 10.0, 'km/h'))

    state_raw = (i % 4).astype(np.uint8)
    signals.append(Signal(samples=state_raw, timestamps=t, name='a_state', conversion={
        'val_0': 0, 'text_0': b'Off', 'val_1': 1, 'text_1': b'Idle', 'val_2': 2, 'text_2': b'Run'}))
    # No default text: a value without an entry reads as the raw value.
    expected.append(expectation(0, 'a_state', t, [['Off', 'Idle', 'Run'][int(v)] if v < 3 else int(v) for v in state_raw]))
    return signals, expected


def write_asammdf_files(directory: Path, count: int = 2500) -> None:
    for compression in (0, 1, 2):
        signals, expected = numeric_signals(count)
        mdf = MDF(version='4.10')
        mdf.append(signals, comment='asammdf numeric')
        name = f'asammdf_numeric_c{compression}'
        mdf.save(directory / f'{name}.mf4', overwrite=True, compression=compression)
        mdf.close()
        (directory / f'{name}.json').write_text(json.dumps({'channels': expected}))

    signals, expected = numeric_signals(max(1, count // 5))
    mdf = MDF(version='4.20')
    mdf.append(signals)
    mdf.save(directory / 'asammdf_numeric_v420.mf4', overwrite=True)
    mdf.close()
    (directory / 'asammdf_numeric_v420.json').write_text(json.dumps({'channels': expected}))

    # Strings: fixed-length bytes and variable-length (VLSD) strings.
    count = 300
    t = np.arange(count, dtype=np.float64) * 0.01
    fixed = np.array([f'fix{n % 50}'.encode() for n in range(count)], dtype='S8')
    variable = [('var ' + 'y' * (n % 23) + f' {n}').encode('utf-8') for n in range(count)]
    mdf = MDF(version='4.10')
    mdf.append([
        Signal(samples=fixed, timestamps=t, name='s_fixed', encoding='latin-1'),
        Signal(samples=np.array(variable), timestamps=t, name='s_variable', encoding='utf-8'),
    ])
    mdf.save(directory / 'asammdf_strings.mf4', overwrite=True, compression=2)
    mdf.close()
    (directory / 'asammdf_strings.json').write_text(json.dumps({'channels': [
        expectation(0, 's_fixed', t, [v.decode() for v in fixed]),
        expectation(0, 's_variable', t, [v.decode() for v in variable]),
    ]}))

    # Several groups at different rates.
    mdf = MDF(version='4.10')
    t_fast = np.arange(1000) * 0.001
    t_slow = np.arange(20) * 0.05
    mdf.append([Signal(samples=np.arange(1000, dtype=np.float64) * 0.1, timestamps=t_fast, name='m_fast')])
    mdf.append([Signal(samples=np.arange(20, dtype=np.int32) * -3, timestamps=t_slow, name='m_slow')])
    mdf.save(directory / 'asammdf_multigroup.mf4', overwrite=True)
    mdf.close()
    (directory / 'asammdf_multigroup.json').write_text(json.dumps({'channels': [
        expectation(0, 'm_fast', t_fast, np.arange(1000) * 0.1),
        expectation(1, 'm_slow', t_slow, np.arange(20) * -3.0),
    ]}))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--configuration', default='Release')
    parser.add_argument('--keep', type=Path, help='write the files here instead of a temporary directory')
    args = parser.parse_args()

    with tempfile.TemporaryDirectory() as scratch:
        root = args.keep or Path(scratch)
        ours, theirs = root / 'mdf4net', root / 'asammdf'
        ours.mkdir(parents=True, exist_ok=True)
        theirs.mkdir(parents=True, exist_ok=True)

        failed = False
        print('== Mdf4Net -> asammdf')
        if run_probe(args.configuration, 'write', ours) != 0:
            print('FAIL  the probe could not write its files')
            return 1
        for expected_file in sorted(ours.glob('*.json')):
            mf4 = expected_file.with_suffix('.mf4')
            problems = check_with_asammdf(mf4, json.loads(expected_file.read_text()))
            print(f"{'ok  ' if not problems else 'FAIL'}  {mf4.name}")
            for problem in problems[:10]:
                print(f'        {problem}')
            failed |= bool(problems)

        print('\n== asammdf -> Mdf4Net')
        write_asammdf_files(theirs)
        failed |= run_probe(args.configuration, 'verify', theirs) != 0

        return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
