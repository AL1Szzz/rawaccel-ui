#!/usr/bin/env node
// Burst-level comparison of Aim Botz sessions (POV demos: shots.csv only).
//
// Why this exists. Two dead ends, both checked on this machine:
//  - the wall cannot be measured: a POV demo carries 8 game events
//    (weapon_fire, player_hurt, player_death, entity_killed, player_spawn,
//    item_pickup, player_sound, server_cvar) and no bullet-impact record, so
//    decals never reach the CSV;
//  - the bot cannot be made immortal: this build has no sv_damage_multiplier
//    and no bot_health (verified with `find` in the console).
// So "did the spray stay on the body" is measured the only way the data allows:
// shots are grouped into bursts, split into bodies at the kill ticks, and the
// hitgroup MIX IS READ AS A FUNCTION OF MAGAZINE DEPTH. Recoil climbs, so
// head-share rising with depth = under-compensated; limb-share rising = pulled
// too hard; a flat torso share = the mag stayed on the body.
//
// The aggregate hit% is reported only as context: it is a composition number,
// and adding wall sprays lowers it without changing aim.
//
// Usage: node field/spray-bursts.mjs --last 3
//        node field/spray-bursts.mjs <reportDir|shots.csv> [<more> ...]

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const DATA = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'RawAccelUI', 'field');
const REPORTS = path.join(DATA, 'reports');

// AK-47 fires every ~100 ms = 6.4 ticks at 64 Hz. A wider gap starts a new burst.
const GAP_TICKS = 32;
const LONG_BURST = 18;   // magazine-ish: deep enough that recoil dominates

const fmt = (v) => (Number.isFinite(v) ? v.toFixed(1) : '—');

function splitLine(s) {
  const out = [];
  let cur = '', q = false;
  for (let i = 0; i < s.length; i++) {
    const ch = s[i];
    if (q) {
      if (ch === '"') { if (s[i + 1] === '"') { cur += '"'; i++; } else q = false; }
      else cur += ch;
    } else if (ch === '"') q = true;
    else if (ch === ',') { out.push(cur); cur = ''; }
    else cur += ch;
  }
  out.push(cur);
  return out;
}

export function readShots(file) {
  const lines = fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '').trim().split(/\r?\n/);
  const head = splitLine(lines[0]);
  const col = (n) => head.indexOf(n);
  const iT = col('tick'), iW = col('weapon'), iHit = col('hit'), iG = col('hitgroup');
  const iD = col('damage'), iHp = col('hp_after'), iK = col('kill');
  const rows = [];
  for (const line of lines.slice(1)) {
    const c = splitLine(line);
    if (c.length < 10 || !Number.isFinite(Number(c[iT]))) continue;
    if (/weapon_(knife|hegrenade|flashbang|smokegrenade|molotov|incgrenade|c4)/.test(c[iW])) continue;
    rows.push({
      tick: Number(c[iT]),
      weapon: c[iW],
      hit: c[iHit] === '1',
      group: c[iG] || '',
      dmg: Number(c[iD]) || 0,
      hp: iHp >= 0 && c[iHp] !== '' ? Number(c[iHp]) : NaN,
      kill: iK >= 0 ? c[iK] === '1' : null,
    });
  }
  return { rows, exactKills: iK >= 0 && rows.some(r => r.kill === true) };
}

export function bursts(rows) {
  const out = [];
  let cur = [];
  for (const r of rows) {
    if (cur.length && r.tick - cur[cur.length - 1].tick > GAP_TICKS) { out.push(cur); cur = []; }
    cur.push(r);
  }
  if (cur.length) out.push(cur);
  return out;
}

// Split a magazine into bodies. Preferred: the kill flags written from
// player_death ticks. Fallback for old CSVs: spend each hit off a nominal 100 HP
// body, which is an approximation and is labelled as such in the output.
function bodies(b, exactKills) {
  const segs = [];
  let len = 0;
  if (exactKills) {
    for (const s of b) { len++; if (s.kill) { segs.push(len); len = 0; } }
  } else {
    let hp = 100;
    for (const s of b) {
      len++;
      if (!s.hit) continue;
      hp -= s.dmg;
      if (hp <= 0) { segs.push(len); len = 0; hp = 100; }
    }
  }
  return { segs, leftover: len };
}

const isHead = (g) => /head|neck/.test(g);
const isTorso = (g) => /chest|stomach/.test(g);
const isLimb = (g) => /arm|leg/.test(g);

export function summarize(rows, exactKills) {
  const bs = bursts(rows);
  const long = bs.filter(b => b.length >= LONG_BURST);
  const onTarget = long.filter(b => b.some(s => s.hit));
  const dead = long.filter(b => !b.some(s => s.hit));
  const hits = rows.filter(r => r.hit);
  const perBody = [];
  let killsTotal = 0;
  for (const b of onTarget) {
    const { segs } = bodies(b, exactKills);
    perBody.push(...segs);
    killsTotal += segs.length;
  }
  const bucket = (lo, hi, pred) => {
    let n = 0, h = 0;
    for (const b of onTarget) {
      for (let i = lo - 1; i < Math.min(hi, b.length); i++) {
        if (!b[i].hit) continue;
        n++;
        if (pred(b[i].group)) h++;
      }
    }
    return n ? 100 * h / n : NaN;   // share WITHIN the hits of that depth slice
  };
  const hitRate = (lo, hi) => {
    let n = 0, h = 0;
    for (const b of onTarget) for (let i = lo - 1; i < Math.min(hi, b.length); i++) { n++; if (b[i].hit) h++; }
    return n ? 100 * h / n : NaN;
  };
  const sorted = [...perBody].sort((a, b) => a - b);
  const twoPlus = onTarget.filter(b => bodies(b, exactKills).segs.length >= 2).length;
  return {
    exactKills,
    shots: rows.length, hitShots: hits.length, hitPct: 100 * hits.length / rows.length,
    longBursts: long.length, onTargetLong: onTarget.length, wallLong: dead.length,
    avgLen: onTarget.length ? onTarget.reduce((s, b) => s + b.length, 0) / onTarget.length : NaN,
    killsInSprays: killsTotal,
    killsPerSpray: onTarget.length ? killsTotal / onTarget.length : NaN,
    sprays2Plus: twoPlus,
    sprays2PlusPct: onTarget.length ? 100 * twoPlus / onTarget.length : NaN,
    medianBulletsPerBody: sorted.length ? sorted[Math.floor(sorted.length / 2)] : NaN,
    meanBulletsPerBody: perBody.length ? perBody.reduce((a, b) => a + b, 0) / perBody.length : NaN,
    head1: bucket(1, 10, isHead), head2: bucket(11, 20, isHead), head3: bucket(21, 30, isHead),
    torso1: bucket(1, 10, isTorso), torso2: bucket(11, 20, isTorso), torso3: bucket(21, 30, isTorso),
    limb1: bucket(1, 10, isLimb), limb2: bucket(11, 20, isLimb), limb3: bucket(21, 30, isLimb),
    rate1: hitRate(1, 10), rate2: hitRate(11, 20), rate3: hitRate(21, 30),
  };
}

function meta(dir) {
  const m = /_(\d\d)-(\d\d)-(\d\d)-/.exec(path.basename(dir));
  const label = m ? `${m[1]}:${m[2]}:${m[3]}` : path.basename(dir).slice(0, 11);
  try {
    const j = JSON.parse(fs.readFileSync(path.join(dir, 'report.json'), 'utf8'));
    return {
      label,
      sha: String(j.aggregates && j.aggregates.configSha || ''),
      demo: String(j.meta && j.meta.demo || path.basename(dir)).replace(/[\\/]+/g, '/').toLowerCase(),
    };
  } catch {
    return { label, sha: '', demo: path.basename(dir) };
  }
}

/// A report column labelled "c63b44" means nothing to the person who has to decide whether to
/// keep the curve, so the config hash is mapped back to the name he gave it in the app.
/// Falls back to the hash when RaProbe is not reachable (or the curve was never saved).
let nameBySha = null;
function curveNames() {
  if (nameBySha) return nameBySha;
  nameBySha = {};
  try {
    const exe = path.join(HERE, 'app', 'RaProbe.exe');
    if (!fs.existsSync(exe)) return nameBySha;
    const out = execFileSync(exe, ['--curve-name'], { encoding: 'utf8', timeout: 20000, windowsHide: true });
    for (const m of out.matchAll(/可切换：(.+?) \[([0-9a-fA-F]{12})\]/g)) nameBySha[m[2]] = m[1].trim();
  } catch { /* hex labels still work */ }
  return nameBySha;
}

function resolve(arg) {
  const p = path.isAbsolute(arg) ? arg : path.resolve(HERE, arg);
  if (fs.existsSync(p) && fs.statSync(p).isDirectory()) return path.join(p, 'shots.csv');
  return fs.existsSync(p) ? p : '';
}

function main() {
  const a = process.argv.slice(2);
  let files = [];
  if (a[0] === '--last') {
    const n = Number(a[1] || 2);
    // one demo can be analysed twice; keep the newest analysis per demo
    const byDemo = new Map();
    for (const d of fs.readdirSync(REPORTS).filter(x => /raui_session/.test(x)).sort()) {
      const dir = path.join(REPORTS, d);
      if (!fs.existsSync(path.join(dir, 'shots.csv'))) continue;
      const m = meta(dir);
      byDemo.set(m.demo, { f: path.join(dir, 'shots.csv'), m });
    }
    files = [...byDemo.values()].slice(-n);
  } else files = a.map(resolve).filter(Boolean).map(f => ({ f, m: meta(path.dirname(f)) }));
  if (files.length === 0) { console.log('no shots.csv found'); return 2; }

  const L = ['Spray-burst comparison (POV aim-map sessions)', '=============================================', ''];
  const set = [];
  for (const { f, m } of files) {
    const { rows, exactKills } = readShots(f);
    if (rows.length < 40) { L.push(`${m.label}: too few gun shots (${rows.length})`); continue; }
    set.push(Object.assign(summarize(rows, exactKills), m));
  }
  if (set.length === 0) { console.log(L.join('\n')); return 2; }

  const COL = 20;      // wide enough for a Chinese curve name next to the session time
  // CJK glyphs are two cells wide in a console but one character in JS, so String.padStart
  // misaligns a table whose headers are Chinese. Measure the printed width instead.
  const WIDE = /[ᄀ-ᅟ⺀-꓏가-힣豈-﫿︐-︙＀-｠￠-￦]/;
  const wide = (s) => [...String(s)].reduce((n, ch) => n + (WIDE.test(ch) ? 2 : 1), 0);
  const pad = (s, n) => ' '.repeat(Math.max(0, n - wide(s))) + s;
  const row = (name, get, fmt2 = v => (Number.isFinite(v) ? v.toFixed(1) : '—')) => {
    L.push(name.padEnd(24) + set.map(s => pad(fmt2(get(s), s), COL)).join(''));
  };
  const maybe = (v, s) => (Number.isFinite(v) ? Math.round(v) : 0) + (s.exactKills ? '' : '~');
  const maybe2 = (v, s) => (Number.isFinite(v) ? v.toFixed(2) : '—') + (s.exactKills ? '' : '~');
  L.push(' '.padEnd(24) + set.map(s => pad(`${s.label} ${curveNames()[s.sha] || s.sha.slice(0,6)}`, COL)).join(''));
  L.push('');
  row('gun shots', s => s.shots, v => String(Math.round(v)));
  row('aggregate hit %', s => s.hitPct);
  row('long bursts (>=' + LONG_BURST + ')', s => s.longBursts, v => String(Math.round(v)));
  row('  at bodies', s => s.onTargetLong, v => String(Math.round(v)));
  row('  at wall (0 hits)', s => s.wallLong, v => String(Math.round(v)));
  row('  mean length', s => s.avgLen);
  L.push('');
  L.push('--- does the mag keep dropping bodies ---');
  row('kills inside sprays', s => s.killsInSprays, maybe);
  row('kills per spray', s => s.killsPerSpray, maybe2);
  row('sprays with 2+ kills', s => s.sprays2Plus, v => String(Math.round(v)));
  row('bullets per body (med)', s => s.medianBulletsPerBody, v => v.toFixed(0));
  row('bullets per body (mean)', s => s.meanBulletsPerBody);
  L.push('');
  L.push('--- where the hits sit, by magazine depth (share of the hits) ---');
  row('head  1-10 / 11-20 / 21-30', s => s.head1, v => `${fmt(v)}`);
  L.push(' '.padEnd(24) + set.map(s => pad(`${fmt(s.head2)}/${fmt(s.head3)}`, COL)).join(''));
  row('torso 1-10 / 11-20 / 21-30', s => s.torso1);
  L.push(' '.padEnd(24) + set.map(s => pad(`${fmt(s.torso2)}/${fmt(s.torso3)}`, COL)).join(''));
  row('limb  1-10 / 11-20 / 21-30', s => s.limb1);
  L.push(' '.padEnd(24) + set.map(s => pad(`${fmt(s.limb2)}/${fmt(s.limb3)}`, COL)).join(''));
  row('hit%  1-10 / 11-20 / 21-30', s => s.rate1);
  L.push(' '.padEnd(24) + set.map(s => pad(`${fmt(s.rate2)}/${fmt(s.rate3)}`, COL)).join(''));
  L.push('');
  L.push('--- how to read this ---');
  L.push('recoil climbs: head share RISING with depth = under-compensated, limb share rising =');
  L.push('pulled too hard, a flat torso share across all three slices = the mag stayed on the body.');
  L.push('a trailing ~ means that column\'s kills were inferred by spending hits off a nominal');
  L.push('100 HP body (its shots.csv predates the kill column); no ~ = exact player_death ticks.');
  L.push('aggregate hit% is context only: wall sprays dilute it without changing aim.');
  L.push('POV demos have no view angles: this is outcome, not hand kinematics.');

  const out = L.join('\n') + '\n';
  const stamp = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14);
  const outFile = path.join(DATA, 'bursts', stamp + '.txt');
  fs.mkdirSync(path.dirname(outFile), { recursive: true });
  fs.writeFileSync(outFile, out);
  console.log(out);
  console.log('saved: ' + outFile);
  return 0;
}

// Only run when executed directly, so field/test.mjs can import the burst math.
const selfPath = path.resolve(fileURLToPath(import.meta.url)).toLowerCase();
const argPath = process.argv[1] ? path.resolve(process.argv[1]).toLowerCase() : '';
if (selfPath === argPath) process.exit(main());
