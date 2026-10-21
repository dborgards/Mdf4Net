#!/usr/bin/env python3
"""Interoperability check between Mdf4Net and asammdf, in both directions.

    check_asammdf.py [--configuration Release] [--keep DIR]

1. Mdf4Net -> asammdf: tools/Mdf4Net.InteropProbe writes one .mf4 per scenario plus a .json with
   the values any conforming reader must see. asammdf opens each file and every channel (and CAN
   frame) is compared with the JSON.
2. asammdf -> Mdf4Net: this script writes files with asammdf (data types, conversions, strings,
   several groups, compression, MDF 4.20) plus a .json each, and the probe's `verify` command
   reads them with Mdf4Net and compares. Two files (raw_virtual_group, raw_remote_master) hold
   constructs neither writer produces with record-oriented data; they are laid out by hand and
   checked against asammdf's reading of them.

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
import struct
from datetime import datetime, timedelta, timezone
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
    if isinstance(expected, dict) and 'float' in expected:
        # JSON has no NaN or infinity; they travel as {"float": "NaN"}.
        wanted = float(expected['float'])
        return isinstance(actual, (int, float)) and (math.isnan(wanted) and math.isnan(actual) or actual == wanted)
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
        if expected.get('start'):
            wanted = datetime.fromisoformat(expected['start'])
            # asammdf converts the nanoseconds through a double: a few microseconds near 2500.
            actual = mdf.header.start_time
            if abs(actual.timestamp() - wanted.timestamp()) > 4e-6 or actual.utcoffset() != wanted.utcoffset():
                problems.append(f'start time {mdf.header.start_time.isoformat()}, expected {wanted.isoformat()}')
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
        if math.isnan(value) or math.isinf(value):
            return {'float': repr(float(value)).replace('nan', 'NaN').replace('inf', 'Infinity')}
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

    # Interpolated value table on a float channel with missing (NaN) samples: NaN stays NaN.
    table_raw = np.where(i % 10 == 0, np.nan, (i % 7) * 0.25)
    signals.append(Signal(samples=table_raw, timestamps=t, name='a_table_nan', conversion={
        'raw_0': 0.0, 'phys_0': 0.0, 'raw_1': 1.0, 'phys_1': 10.0, 'interpolation': True}))
    expected.append(expectation(0, 'a_table_nan', t, [v if math.isnan(v) else min(10.0, v * 10) for v in table_raw]))

    # A value-to-text table whose default is a nested range table: for an integer channel the
    # range [0, 1] includes 1 also when reached through the reference. The expected values follow
    # the format, not asammdf's own reading (8.8.27 reads range tables differently).
    nested_raw = np.array([0, 1, 2, 5])[i % 4].astype(np.uint8)
    signals.append(Signal(samples=nested_raw, timestamps=t, name='a_nested', conversion={
        'val_0': 5, 'text_0': 'five',
        'default_addr': {'lower_0': 0, 'upper_0': 1, 'phys_0': 10, 'default': 99}}))
    expected.append(expectation(0, 'a_nested', t, [{0: 10.0, 1: 10.0, 2: 99.0, 5: 'five'}[int(v)] for v in nested_raw]))
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
    # -14:00, the most negative time zone offset there is.
    strings_start = datetime(2026, 10, 20, 12, 0, 0, tzinfo=timezone(timedelta(hours=-14)))
    mdf = MDF(version='4.10')
    mdf.header.start_time = strings_start
    mdf.append([
        Signal(samples=fixed, timestamps=t, name='s_fixed', encoding='latin-1'),
        Signal(samples=np.array(variable), timestamps=t, name='s_variable', encoding='utf-8'),
    ])
    mdf.save(directory / 'asammdf_strings.mf4', overwrite=True, compression=2)
    mdf.close()
    (directory / 'asammdf_strings.json').write_text(json.dumps({'start': strings_start.isoformat(), 'channels': [
        expectation(0, 's_fixed', t, [v.decode() for v in fixed]),
        expectation(0, 's_variable', t, [v.decode() for v in variable]),
    ]}))

    # Several groups at different rates, starting in the year 2500 (beyond a signed 64-bit
    # nanosecond count, inside MDF's unsigned one), at +14:00, the largest time zone offset.
    start = datetime(2500, 1, 2, 3, 4, 5, tzinfo=timezone(timedelta(hours=14)))
    mdf = MDF(version='4.10')
    mdf.header.start_time = start
    t_fast = np.arange(1000) * 0.001
    t_slow = np.arange(20) * 0.05
    mdf.append([Signal(samples=np.arange(1000, dtype=np.float64) * 0.1, timestamps=t_fast, name='m_fast')])
    mdf.append([Signal(samples=np.arange(20, dtype=np.int32) * -3, timestamps=t_slow, name='m_slow')])
    mdf.save(directory / 'asammdf_multigroup.mf4', overwrite=True)
    mdf.close()
    (directory / 'asammdf_multigroup.json').write_text(json.dumps({'start': start.isoformat(), 'channels': [
        expectation(0, 'm_fast', t_fast, np.arange(1000) * 0.1),
        expectation(1, 'm_slow', t_slow, np.arange(20) * -3.0),
    ]}))

    write_virtual_group(directory, cycles=4)
    write_remote_master_group(directory)


class RawBlocks:
    """Lays out an MDF file block by block, for constructs no writer in reach produces. Blocks
    are named; a link is the name of another block or 0."""

    def __init__(self):
        self.blocks = []  # (name, block id, links, data)

    def block(self, name, block_id, links, data):
        self.blocks.append((name, block_id, links, data))

    def text(self, name, value):
        raw = value.encode() + b'\0'
        self.block(name, b'##TX', [], raw + b'\0' * (-len(raw) % 8))

    def linear(self, name, factor, offset):
        # cc_type, precision, flags, ref count, value count, physical range, then b and a.
        self.block(name, b'##CC', [0, 0, 0, 0], struct.pack('<BBHHHdddd', 1, 0, 0, 0, 2, 0, 0, offset, factor))

    def channel(self, name, next_channel, label, channel_type, sync_type, conversion, byte_offset=0, bit_count=0):
        # cn_type, sync type, data type (unsigned LE), bit offset, byte offset, bit count, flags,
        # invalidation bit, precision, reserved, attachment count, then six range doubles.
        data = struct.pack('<BBBBIIIIBBH', channel_type, sync_type, 0, 0, byte_offset, bit_count, 0, 0, 0, 0, 0) + bytes(48)
        self.block(name, b'##CN', [next_channel, 0, label, 0, conversion, 0, 0, 0], data)

    def channel_group(self, name, first_channel, cycles, data_bytes, flags=0, master_group=None):
        # record id, cycle count, flags, path separator, reserved, data bytes, invalidation bytes.
        # With the remote master flag (bit 3) the block carries the seventh link, cg_cg_master.
        links = [0, first_channel, 0, 0, 0, 0] + ([master_group] if master_group else [])
        self.block(name, b'##CG', links, struct.pack('<QQHHIII', 0, cycles, flags, 0, 0, data_bytes, 0))

    def write(self, path: Path, version: str) -> None:
        """Writes the blocks in order, each starting at a multiple of 8 bytes."""
        offsets, position = {}, 64
        for name, _, links, data in self.blocks:
            offsets[name] = position
            position += 24 + 8 * len(links) + len(data)
            position += -position % 8
        number = int(version.replace('.', ''))
        out = bytearray(b'MDF     ' + version.ljust(8).encode() + b'rawbytes' + bytes(4) + struct.pack('<H', number) + bytes(34))
        for _, block_id, links, data in self.blocks:
            out += block_id + bytes(4) + struct.pack('<QQ', 24 + 8 * len(links) + len(data), len(links))
            out += struct.pack(f'<{len(links)}Q', *[offsets[link] if link else 0 for link in links]) + data
            out += bytes(-len(out) % 8)
        path.write_bytes(bytes(out))


def write_virtual_group(directory: Path, cycles: int) -> None:
    """A finalized file whose only channel group stores no record bytes: a virtual time master
    (index * 0.5 s) and a virtual data channel (index * 2 + 1). The cycle count alone gives the
    number of samples. Neither writer produces such a group through its public API, so the blocks
    are laid out here by hand; asammdf's reading of the master is the cross-check."""
    raw = RawBlocks()
    raw.block('hd', b'##HD', ['dg', 0, 0, 0, 0, 0], bytes(32))
    raw.block('dg', b'##DG', [0, 'cg', 0, 0], bytes(8))
    raw.channel_group('cg', 'cn_time', cycles, 0)
    raw.channel('cn_time', 'cn_index', 'tx_time', 3, 1, 'cc_time')  # virtual master, time
    raw.channel('cn_index', 0, 'tx_index', 6, 0, 'cc_index')  # virtual data
    raw.text('tx_time', 'v_time')
    raw.text('tx_index', 'v_index')
    raw.linear('cc_time', 0.5, 0.0)
    raw.linear('cc_index', 2.0, 1.0)
    mf4 = directory / 'raw_virtual_group.mf4'
    raw.write(mf4, '4.10')

    index = np.arange(cycles, dtype=np.float64)
    mdf = MDF(str(mf4))
    try:
        # asammdf 8.8.27 reads the master of such a group, but not its channels (it divides by the
        # record size); the channel values follow the format.
        master = mdf.get_master(0)
    finally:
        mdf.close()
    if list(master) != list(index * 0.5):
        raise SystemExit(f'asammdf reads the virtual master as {list(master)}, expected {list(index * 0.5)}')
    (directory / 'raw_virtual_group.json').write_text(json.dumps({'channels': [
        expectation(0, 'v_time', index * 0.5, index * 0.5),
        expectation(0, 'v_index', index * 0.5, index * 2 + 1),
    ]}))


def write_remote_master_group(directory: Path) -> None:
    """An MDF 4.20 file whose second channel group takes its master from the first (cg_flags bit
    3, cg_cg_master): the master group holds the time (raw 2, 3, 4 at 0.25 s: 0.5, 0.75, 1.0 s),
    the remote group holds r_value (10, 20, 30) and no master channel of its own, and a third
    group (r_chain: 1, 2, 3) refers to the second, so its master is two links away. asammdf
    reads such groups but writes them only with column-oriented storage, so the file is laid out
    by hand; asammdf's reading of the two remote groups' master is the cross-check."""
    raw = RawBlocks()
    raw.block('hd', b'##HD', ['dg_master', 0, 0, 0, 0, 0], bytes(32))
    raw.block('dg_master', b'##DG', ['dg_remote', 'cg_master', 'dt_master', 0], bytes(8))
    raw.channel_group('cg_master', 'cn_time', 3, 1)
    raw.channel('cn_time', 0, 'tx_time', 2, 1, 'cc_time', bit_count=8)  # master, time
    raw.text('tx_time', 'r_time')
    raw.linear('cc_time', 0.25, 0.0)
    raw.block('dt_master', b'##DT', [], bytes([2, 3, 4]))
    raw.block('dg_remote', b'##DG', ['dg_chain', 'cg_remote', 'dt_remote', 0], bytes(8))
    raw.channel_group('cg_remote', 'cn_value', 3, 1, flags=1 << 3, master_group='cg_master')
    raw.channel('cn_value', 0, 'tx_value', 0, 0, 0, bit_count=8)  # fixed-length, no conversion
    raw.text('tx_value', 'r_value')
    raw.block('dt_remote', b'##DT', [], bytes([10, 20, 30]))
    raw.block('dg_chain', b'##DG', [0, 'cg_chain', 'dt_chain', 0], bytes(8))
    raw.channel_group('cg_chain', 'cn_chain', 3, 1, flags=1 << 3, master_group='cg_remote')
    raw.channel('cn_chain', 0, 'tx_chain', 0, 0, 0, bit_count=8)
    raw.text('tx_chain', 'r_chain')
    raw.block('dt_chain', b'##DT', [], bytes([1, 2, 3]))
    mf4 = directory / 'raw_remote_master.mf4'
    raw.write(mf4, '4.20')

    timestamps = [0.5, 0.75, 1.0]
    wanted = {'r_value': [10, 20, 30], 'r_chain': [1, 2, 3]}
    mdf = MDF(str(mf4))
    try:
        for group, name in ((1, 'r_value'), (2, 'r_chain')):
            master = list(mdf.get_master(group))
            signal = mdf.get(name, group=group)
            read = (master, list(signal.timestamps), list(signal.samples))
            if read != (timestamps, timestamps, wanted[name]):
                raise SystemExit(f'asammdf reads remote master group {group} as {read}, expected {(timestamps, timestamps, wanted[name])}')
    finally:
        mdf.close()
    (directory / 'raw_remote_master.json').write_text(json.dumps({'channels': [
        expectation(0, 'r_time', timestamps, timestamps),
        expectation(1, 'r_value', timestamps, wanted['r_value']),
        expectation(2, 'r_chain', timestamps, wanted['r_chain']),
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
