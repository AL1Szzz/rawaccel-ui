#!/usr/bin/env node
// Spray-Y analysis from a --poll-jitter raw CSV (us,dx,dy).
//
// IMPORTANT LAYER NOTE: the CSV is POST-driver raw input. The accel curve arms on
// the hand speed, the Y/X output multiplier is applied after it, so the reported
// "hand speed" is |dy| / dt / yx. Below the Y input offset the curve gain is
// exactly 1, so hand speed < offset proves the Y curve did not arm for that
// interval. Above the offset the output can be larger than the input, so the
// share above the offset is a valid crossing test, but not a speed measurement.
//
// The console raw-input probe receives coalesced packets (~125 Hz here), so a
// brief 2 ms peak can be averaged down. Spray compensation is sustained, which
// is what this tool is for; it is not a packet-jitter measurement.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..');
const DATA = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'RawAccelUI', 'field');
const THRESHOLDS = [0.25, 0.5, 0.75, 1, 1.5, 2, 2.5, 3, 4, 5];

const fmt = (v, d = 2) => Number.isFinite(v) ? v.toFixed(d) : '—';
const pct = (v) => Number.isFinite(v) ? v.toFixed(1) + '%' : '—';

function stamp(d = new Date()) {
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}_${p(d.getHours())}-${p(d.getMinutes())}-${p(d.getSeconds())}`;
}

function args() {
  const o = {};
  const a = process.argv.slice(2);
  for (let i = 0; i < a.length; i++) {
    if (a[i] === '--csv') o.csv = a[++i];
    else if (a[i] === '--latest') o.latest = true;
    else if (a[i] === '--y-offset') o.yOffset = Number(a[++i]);
    else if (a[i] === '--yx') o.yx = Number(a[++i]);
    else if (a[i] === '--skip-first') o.skipFirst = Number(a[++i]);
    else if (!a[i].startsWith('--')) o.csv = a[i];
  }
  return o;
}

function latestCsv() {
  const dir = os.tmpdir();
  const files = fs.readdirSync(dir)
    .filter(f => /^poll-jitter-.*\.csv$/i.test(f))
    .map(f => ({ f: path.join(dir, f), t: fs.statSync(path.join(dir, f)).mtimeMs }))
    .sort((a, b) => b.t - a.t);
  return files[0]?.f || '';
}

function readDriverState() {
  const none = { offset: 2.5, yx: 1, source: 'default (driver read failed)' };
  try {
    const exe = path.join(ROOT, 'app', 'RaProbe.exe');
    if (!fs.existsSync(exe)) return { offset: 2.5, yx: 1, source: 'default (RaProbe not found)' };
    const out = execFileSync(exe, [], { encoding: 'utf8', timeout: 15000, windowsHide: true });
    const y = /accel_y:[^\n]*in_off=([0-9.]+)/.exec(out);
    const x = /output_dpi:[0-9.]+\s+yx:([0-9.]+)/.exec(out);
    if (y || x) return {
      offset: y ? Number(y[1]) : 2.5,
      yx: x ? Number(x[1]) : 1,
      source: 'driver',
    };
  } catch { /* fall through */ }
  return none;
}

function quantile(a, p) {
  const v = a.filter(Number.isFinite).sort((x, y) => x - y);
  if (!v.length) return NaN;
  const i = Math.min(v.length - 1, Math.floor(p * (v.length - 1)));
  return v[i];
}

function parseCsv(file) {
  const lines = fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '').trim().split(/\r?\n/);
  const packets = [];
  let bad = 0;
  for (const line of lines) {
    if (!line || /^us,/i.test(line)) continue;
    const [us, dx, dy] = line.split(',').map(Number);
    if (!Number.isFinite(us) || !Number.isFinite(dx) || !Number.isFinite(dy)) { bad++; continue; }
    packets.push({ us, dx, dy });
  }
  return { packets, bad };
}

function main() {
  const o = args();
  const file = o.csv || (o.latest || !o.csv ? latestCsv() : '');
  if (!file || !fs.existsSync(file)) {
    console.log('No poll-jitter CSV found. Run 测压枪Y.cmd first.');
    return 2;
  }
  const parsed = parseCsv(file);
  const skipFirst = Number.isFinite(o.skipFirst) ? Math.max(0, o.skipFirst) : 1.5;
  const firstUs = parsed.packets[0]?.us ?? 0;
  const cutUs = firstUs + skipFirst * 1000;
  const rows = [];
  let prev = null, pauses = 0, bad = parsed.bad, skipped = 0;
  for (const p of parsed.packets) {
    if (p.us < cutUs) { skipped++; continue; }
    if (prev !== null) {
      const dt = (p.us - prev) / 1000;
      if (dt <= 0) bad++;
      else if (dt > 100) pauses++;
      else rows.push({ dt, dx: p.dx, dy: p.dy });
    }
    prev = p.us;
  }
  if (rows.length < 30) {
    console.log(`Too few intervals (${rows.length} usable, ${pauses} pauses, ${bad} bad).`);
    return 2;
  }

  const drv = readDriverState();
  const yOffset = Number.isFinite(o.yOffset) ? o.yOffset : drv.offset;
  const yx = Number.isFinite(o.yx) ? o.yx : drv.yx;
  const offsetSource = Number.isFinite(o.yOffset) ? '--y-offset' : drv.source;
  const yxSource = Number.isFinite(o.yx) ? '--yx' : drv.source;

  const totalMs = rows.reduce((s, r) => s + r.dt, 0);
  const movingY = rows.filter(r => r.dy !== 0);
  // The CSV is POST-driver. The vertical multiplier is applied after the accel
  // curve (RaCore/Pipeline.cs: vin.y *= dpiAdj * yx_output_dpi_ratio), so the
  // curve armed on the HAND speed = |dy| / dt / yx. Everything below that is
  // compared with the offset is therefore divided by yx; the post-driver number
  // is still printed, because that is what CS2 actually receives.
  const handY = (r) => Math.abs(r.dy) / r.dt / yx;
  const ySpeeds = movingY.map(handY);
  const yPost = movingY.map(r => Math.abs(r.dy) / r.dt);
  const xSpeeds = rows.filter(r => r.dx !== 0).map(r => Math.abs(r.dx) / r.dt);
  const zeroYpct = 100 * (rows.length - movingY.length) / rows.length;

  const shareAll = (t) => 100 * rows.filter(r => handY(r) >= t).length / rows.length;
  const shareMov = (t) => movingY.length ? 100 * ySpeeds.filter(v => v >= t).length / ySpeeds.length : NaN;

  const p = (q, arr = ySpeeds) => quantile(arr, q);
  const verdict = movingY.length < 50
    ? 'not enough vertical movement to judge'
    : p(0.90) < yOffset
      ? `Y threshold ${fmt(yOffset)} is ABOVE ~90% of this Y movement: the Y curve did not arm below it (gain is exactly 1 below the offset).`
      : p(0.50) < yOffset
        ? `only the upper Y tail crosses ${fmt(yOffset)}: the Y curve arms occasionally, not through the burst.`
        : `most Y movement is above ${fmt(yOffset)}: the Y curve is arming through the burst.`;

  const L = [];
  L.push('Spray Y report (hand speed + what CS2 receives)');
  L.push('=====================================');
  L.push(`source      : ${file}`);
  L.push(`first ${skipFirst.toFixed(1)} s skipped (${skipped} packets) to remove the Alt-Tab/window-switch transient`);
  L.push(`intervals   : ${rows.length}  (${pauses} pauses >100 ms, ${bad} bad rows)`);
  L.push(`duration    : ${(totalMs / 1000).toFixed(1)} s   median interval ${fmt(p(0.5, rows.map(r => r.dt)))} ms`);
  L.push(`Y offset    : ${fmt(yOffset)} c/ms  (${offsetSource})`);
  L.push(`Y multiplier: ${fmt(yx, 3)}  (Y/X output DPI ratio, ${yxSource})  -> hand speed = counts/ms measured / ${fmt(yx, 3)}`);
  L.push('');
  L.push('--- Y movement, hand speed (packets with dy != 0) ---');
  L.push(`packets     : ${movingY.length}   zero-Y packets ${zeroYpct.toFixed(1)}%`);
  L.push(`hand p50    : ${fmt(p(0.50))} c/ms   p75 ${fmt(p(0.75))}   p90 ${fmt(p(0.90))}   p95 ${fmt(p(0.95))}   p99 ${fmt(p(0.99))}   max ${fmt(p(1))}`);
  L.push(`at 600 DPI  : p50 ${fmt(p(0.50) * 1000 / 600 * 2.54)} cm/s   p90 ${fmt(p(0.90) * 1000 / 600 * 2.54)}   p99 ${fmt(p(0.99) * 1000 / 600 * 2.54)}`);
  L.push(`post-driver : p50 ${fmt(p(0.50, yPost))} c/ms   p90 ${fmt(p(0.90, yPost))}   p99 ${fmt(p(0.99, yPost))}   (what the game sees, x${fmt(yx, 2)} included)`);
  L.push(`X speed p50 : ${fmt(p(0.50, xSpeeds))} c/ms   p90 ${fmt(p(0.90, xSpeeds))}   (unaffected by the Y multiplier: drift control)`);
  L.push('');
  L.push('--- hand-speed share at or above thresholds ---');
  L.push('  threshold   all intervals   Y-moving packets');
  for (const t of THRESHOLDS) L.push(`  ${t.toFixed(2).padStart(5)} c/ms   ${pct(shareAll(t)).padStart(7)}        ${pct(shareMov(t)).padStart(7)}`);
  L.push('');
  L.push('--- verdict ---');
  L.push(verdict);
  L.push('');
  L.push('--- layer caveats ---');
  L.push('* hand speed is what the curve sees: the accel gain is computed first, the Y/X multiplier is');
  L.push('  applied afterwards, so a multiplier cannot make the curve arm earlier. With yx=' + fmt(yx, 2) + ' the');
  L.push('  post-driver number exceeds the offset even where the hand speed does not - use the hand line.');
  L.push('* below the Y offset the curve gain is exactly 1, so hand speed < offset proves the curve did not arm;');
  L.push('  the classic gain above it is gain-1 = accel*(v-offset)^2/v, which starts at 0 right at the offset');
  L.push('* the console raw-input probe is coalesced (~125 Hz); a very short 2 ms peak can be averaged down');
  L.push('* this is a SPRAY tool: for single-flick kinematics, use the demo report; for 2 ms packet jitter, use the jitter report');

  const outDir = path.join(DATA, 'spray', `${stamp()}-${path.basename(file, '.csv')}`);
  fs.mkdirSync(outDir, { recursive: true });
  fs.writeFileSync(path.join(outDir, 'report.txt'), L.join('\n') + '\n');
  fs.writeFileSync(path.join(outDir, 'report.json'), JSON.stringify({
    source: file, yOffset, yOffsetSource: offsetSource, yx, yxSource,
    intervals: rows.length, pauses, durationS: totalMs / 1000, zeroYpct, skipFirstS: skipFirst, skippedPackets: skipped,
    y: { p50: p(0.5), p75: p(0.75), p90: p(0.9), p95: p(0.95), p99: p(0.99), max: p(1), movingPackets: movingY.length },
    yPostDriver: { p50: p(0.5, yPost), p90: p(0.9, yPost), p99: p(0.99, yPost) },
    x: { p50: p(0.5, xSpeeds), p90: p(0.9, xSpeeds) },
    shareAll: Object.fromEntries(THRESHOLDS.map(t => [t, shareAll(t)])),
    shareMoving: Object.fromEntries(THRESHOLDS.map(t => [t, shareMov(t)])),
    verdict,
  }, null, 2));
  try { fs.copyFileSync(file, path.join(outDir, path.basename(file))); } catch { }

  console.log('REPORT: ' + path.join(outDir, 'report.txt'));
  console.log(`Y hand p50 ${fmt(p(0.5))}  p90 ${fmt(p(0.9))}  p99 ${fmt(p(0.99))} c/ms  |  offset ${fmt(yOffset)}  |  above offset (moving) ${pct(shareMov(yOffset))}  |  yx ${fmt(yx, 2)}`);
  console.log(verdict);
  return 0;
}

process.exit(main());
