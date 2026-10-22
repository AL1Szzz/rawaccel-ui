#!/usr/bin/env node
// Field report CLI: newest demo -> analysis -> text/JSON/CSV.
// Read-only with respect to CS2 and the driver (the driver is only read for
// the config hash and thresholds; nothing is written).
//
// Example:
//   node field/field.mjs --latest
//   node field/field.mjs --demo "E:\...\raui_session.dem" --steamid 7656...

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..');
const DATA = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'RawAccelUI', 'field');
const CSGO = 'E:/Steam/steamapps/common/Counter-Strike Global Offensive/game/csgo';
const REPLAYS = path.join(CSGO, 'replays');
const INBOX = path.join(DATA, 'inbox');
const DEMO_CACHE = 'E:/demos';

const fmt = (v, digits = 2) => Number.isFinite(v) ? Number(v).toFixed(digits) : '—';
const pct = (v) => Number.isFinite(v) ? v.toFixed(1) + '%' : '—';

function parseArgs(argv) {
  const o = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--latest') o.latest = true;
    else if (a === '--latest-5e' || a === '--latest-cache') o.latest5e = true;
    else if (a === '--list') o.list = true;
    else if (a === '--config') o.showConfig = true;
    else if (a === '--demo') o.demo = argv[++i];
    else if (a === '--steamid') o.steamid = argv[++i];
    else if (a === '--active-steamid') o.activeSteamId = argv[++i];
    else if (a === '--out') o.out = argv[++i];
    else if (a === '--sensitivity') o.sensitivity = Number(argv[++i]);
    else if (a === '--x-threshold') o.xThreshold = Number(argv[++i]);
    else if (a === '--y-threshold') o.yThreshold = Number(argv[++i]);
    else if (a === '--tick-rate') o.tickRate = Number(argv[++i]);
    else if (a === '--no-rename') o.noRename = true;
    else if (!a.startsWith('--')) o.demo = a;
  }
  return o;
}

function stamp(d = new Date()) {
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}_${p(d.getHours())}-${p(d.getMinutes())}-${p(d.getSeconds())}`;
}

function readJson(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return null; }
}

function readPrefs() {
  return readJson(path.join(path.dirname(DATA), 'raui.ui.json')) || {};
}

function readActiveSteamId() {
  try {
    const text = fs.readFileSync('E:/Steam/config/loginusers.vdf', 'utf8');
    const blocks = [...text.matchAll(/"(\d{17})"\s*\{([\s\S]*?)\n\t\}/g)];
    let best = null, bestTs = -1;
    for (const m of blocks) {
      const id = m[1], body = m[2];
      const auto = /"AutoLogin"\s*"1"/.test(body);
      const ts = Number((/"timestamp"\s*"(\d+)"/.exec(body) || [])[1] || 0);
      if ((auto && ts > bestTs) || (best === null && ts > bestTs)) { best = id; bestTs = ts; }
    }
    return best;
  } catch { return null; }
}

function readDriverInfo() {
  const candidates = [
    path.join(ROOT, 'app', 'RaProbe.exe'),
    path.join(ROOT, 'RaProbe', 'bin', 'Release', 'net8.0', 'RaProbe.exe'),
  ];
  let out = '';
  let live = '';
  let lastError = '';
  for (const exe of candidates) {
    if (!fs.existsSync(exe)) continue;
    try {
      out = execFileSync(exe, [], { encoding: 'utf8', timeout: 15000, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
      // The default mode prints thresholds but not the config hash; the
      // read-only apply-settings mode prints "driver now: ... sha <12>".
      const curve = path.join(ROOT, 'curves', 'A-accel0.06.json');
      if (fs.existsSync(curve)) {
        live = execFileSync(exe, ['--apply-settings', curve], {
          encoding: 'utf8', timeout: 15000, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
        });
      }
      if (out && out.trim()) break;
    } catch (e) { lastError = e.message; }
  }
  const sha = /driver now\s*:\s*.*?sha\s+([0-9a-f]{12})/i.exec(live)?.[1] || '';
  const xLive = /X\(offset\s+([0-9.]+)/.exec(live)?.[1];
  const yLive = /Y\(offset\s+([0-9.]+)/.exec(live)?.[1];
  const x = /accel_x:[^\n]*in_off=([0-9.]+)/.exec(out)?.[1] ?? xLive;
  const y = /accel_y:[^\n]*in_off=([0-9.]+)/.exec(out)?.[1] ?? yLive;
  const version = /driver\s*=\s*([0-9.]+)/.exec(out)?.[1] || '';
  return {
    sha, version,
    xThreshold: x !== undefined ? Number(x) : undefined,
    yThreshold: y !== undefined ? Number(y) : undefined,
    error: lastError,
  };
}

function listDemos() {
  const out = [];
  for (const dir of [CSGO, REPLAYS, INBOX]) {
    try {
      if (!fs.existsSync(dir)) continue;
      for (const f of fs.readdirSync(dir)) {
        if (!/\.dem$/i.test(f)) continue;
        const p = path.join(dir, f);
        const st = fs.statSync(p);
        out.push({ path: p, name: f, mtime: st.mtimeMs, size: st.size });
      }
    } catch { /* one unreadable folder must not hide the other */ }
  }
  return out.sort((a, b) => b.mtime - a.mtime);
}

function listCache() {
  const out = [];
  try {
    if (fs.existsSync(DEMO_CACHE)) {
      for (const f of fs.readdirSync(DEMO_CACHE)) {
        if (!/\.dem$/i.test(f)) continue;
        const p = path.join(DEMO_CACHE, f);
        const st = fs.statSync(p);
        out.push({ path: p, name: f, mtime: st.mtimeMs, size: st.size });
      }
    }
  } catch { /* ignore */ }
  return out.sort((a, b) => b.mtime - a.mtime);
}

function writeText(result) {
  const a = result.aggregates, p = result.player, m = result.meta;
  const L = [];
  L.push('RawAccel Field Report');
  L.push('=====================');
  L.push(`demo        : ${path.basename(m.demo)}`);
  L.push(`demo path   : ${m.demo}`);
  L.push(`map         : ${m.map || '—'}`);
  L.push(`tick rate   : ${m.tickRate} Hz  (${(1000 / m.tickRate).toFixed(1)} ms bins)`);
  L.push(`player      : ${p.name || '—'} [${p.id || '—'}] team ${Number.isFinite(p.team) ? p.team : '—'}`);
  L.push(`config sha  : ${a.configSha}  (read at ANALYSIS time; if you switched curves after recording, this is the wrong curve)`);
  L.push(`sensitivity : ${a.sensitivity}`);
  L.push(`thresholds  : X ${fmt(a.xThreshold)} c/ms / Y ${fmt(a.yThreshold)} c/ms (input thresholds; flick peaks below are POST-driver output converted through deg/s)`);
  L.push(`generated   : ${m.generatedAt}`);
  L.push('');
  L.push('--- counts ---');
  L.push(`gun shots ${a.shots}   hits ${a.hits}   shot hit ${pct(a.shotHitPct)}   victim hit ${pct(a.hitPct)}   gun kills ${a.kills}   utility kills ${a.utilityKills}`);
  L.push(`headshots ${a.headshots} (${pct(a.headshotPctOfHits)} of hits)   excluded knife/grenade throws ${a.excludedUtilityShots}`);
  L.push(`distance median ${fmt(a.distanceMedianM)} m   TTK median ${Number.isFinite(a.ttkMedianMs) ? Math.round(a.ttkMedianMs) + ' ms' : '—'}`);
  L.push('');
  L.push('--- aim error (angle to the victim; for misses, nearest enemy in a 60-degree cone) ---');
  L.push(`median ${fmt(a.aimErrorMedianDeg)} deg   MAE ${fmt(a.aimErrorMaeDeg)}   p95 ${fmt(a.aimErrorP95Deg)}   on hits ${fmt(a.aimErrorMedianOnHitsDeg)}`);
  if (a.aimErrorMedianOnHitsDeg > 5) L.push('  note: hits should be near 0 deg; a large value means the yaw/pitch or eye-height convention is wrong, not that he missed');
  L.push('');
  if (a.spray) {
    L.push('--- spray bursts (same weapon, gap <= 160 ms, >= 3 shots) ---');
    L.push(`bursts ${a.spray.bursts}   shots ${a.spray.shots}   duration p50 ${Math.round(a.spray.durationMedianMs)} ms   hit ${pct(a.spray.hitPct)}`);
    L.push(`NET view pitch p50 ${fmt(a.spray.netVerticalP50Cms)} c/ms   net above Y threshold ${pct(a.spray.netAboveYPct)}   net horizontal p50 ${fmt(a.spray.netHorizontalP50Cms)}   net above X ${pct(a.spray.netAboveXPct)}`);
    L.push('  NET view = recoil + mouse compensation. A well-compensated spray can have near-zero net pitch while the mouse Y is moving;');
    L.push('  this does NOT tell you whether the Y curve armed. For that use 测压枪Y (raw mouse Y during a wall spray).');
  } else {
    L.push('--- spray bursts ---');
    L.push('  not enough sustained bursts in this demo (or POV mode has no view angles)');
  }
  L.push('');
  L.push('--- by distance ---');
  for (const [k, v] of Object.entries(a.byDistance)) {
    L.push(`  ${k.padEnd(7)} n=${String(v.n).padStart(3)}  hit ${pct(v.hitPct).padStart(6)}  aimErr med ${fmt(v.aimErrorMedianDeg).padStart(6)} deg  peak p50 ${fmt(v.peakMedianCms).padStart(5)} c/ms`);
  }
  L.push('');
  L.push('--- flicks (view-angle kinematics; shot = end of the flick) ---');
  const f = a.flicks;
  L.push(`n=${f.n}   peak p50 ${fmt(f.peakMedianCms)} c/ms   p95 ${fmt(f.peakP95Cms)}   duration p50 ${Number.isFinite(f.durationMedianMs) ? Math.round(f.durationMedianMs) + ' ms' : '—'}`);
  L.push(`components: horizontal (X) p50 ${fmt(f.peakYawP50Cms)} c/ms   vertical (Y) p50 ${fmt(f.peakPitchP50Cms)} c/ms`);
  L.push(`above X threshold ${pct(f.aboveXThresholdPct)} of flicks (horizontal component)   above Y threshold ${pct(f.aboveYThresholdPct)} (vertical component)`);
  L.push(`  both thresholds are INPUT speeds; the peaks above are POST-driver output, so these shares are an upper bound on engagement`);
  L.push(`overshoot past shot direction: median ${fmt(f.overshootMedianDeg)} deg   max ${fmt(f.overshootMaxDeg)} deg   reversals ${fmt(f.reversalsMean, 1)}/flick   corrected ${pct(f.correctedPct)}`);
  L.push(`arc ratio ${fmt(f.arcRatioMedian, 2)}x   direction X ${f.direction.X.n} / Y ${f.direction.Y.n} / diag ${f.direction.diag.n}`);
  for (const d of ['X', 'Y', 'diag']) {
    const g = f.direction[d];
    if (g.n) L.push(`  ${d}: n=${g.n}  peak p50 ${fmt(g.peakMedian)} c/ms  horiz p50 ${fmt(g.yawMedian)}  vert p50 ${fmt(g.pitchMedian)}  overshoot p50 ${fmt(g.overshootMedian)} deg`);
  }
  L.push('');
  L.push('--- by weapon ---');
  for (const [w, v] of Object.entries(a.byWeapon).sort((x, y) => y[1].n - x[1].n)) {
    L.push(`  ${w.padEnd(22)} n=${String(v.n).padStart(3)}  hit ${pct(v.hitPct).padStart(6)}  aimErr med ${fmt(v.aimErrorMedianDeg).padStart(6)} deg  head ${v.headshots}`);
  }
  L.push('');
  L.push('--- quality / how to read this ---');
  L.push(`* ${result.quality.measurement}`);
  L.push(`* flick peak is POST-driver output (deg/s converted through m_yaw*sens); the profile thresholds are INPUT speeds.`);
  L.push(`  Because the curve gain is >= 1, "above threshold" is an upper bound on engagement, not a hand-speed claim.`);
  L.push(`* "above X/Y" is computed on the horizontal/vertical component, not on the 3D peak: a fast horizontal flick does not count as Y engagement.`);
  L.push(`* the single-flick vertical component is a VIEW measure; for spray Y engagement the demo cannot separate recoil from mouse Y.`);
  L.push(`* aim error uses the victim when a player_hurt is present; for misses it is the nearest enemy, not a claim about intent`);
  L.push(`* visibility/occlusion is NOT modelled; a wall between them does not remove the "nearest enemy" label`);
  L.push(`* config sha is read when this report is generated, not embedded in the demo`);
  for (const w of result.quality.warnings) L.push('* ' + w);
  L.push('');
  L.push('machine JSON and per-shot CSV are next to this file; the assistant reads them.');
  return L.join('\n');
}

function writeCsv(result) {
  const head = ['tick', 'time_ms', 'weapon', 'distance_m', 'distance_bucket', 'victim_id', 'victim_name',
    'hit', 'hitgroup', 'damage', 'aim_error_deg', 'peak_cms', 'peak_deg_s', 'overshoot_deg',
    'reversals', 'corrected', 'direction', 'duration_ms', 'arc_ratio', 'hp_after', 'kill'];
  const esc = (v) => {
    if (typeof v === 'number' && !Number.isFinite(v)) return '';
    const s = v === null || v === undefined ? '' : String(v);
    return /[",\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
  };
  const lines = [head.join(',')];
  for (const s of result.shots) {
    lines.push([
      s.tick, s.timeMs, s.weapon, s.distanceM, s.distanceBucket, s.victimId, s.victimName,
      s.hit ? 1 : 0, s.hitgroup, s.damage, s.aimErrorDeg, s.peakCms, s.peakDegPerSec,
      s.overshootDeg, s.reversals, s.corrected === null ? '' : (s.corrected ? 1 : 0),
      s.direction, s.durationMs, s.arcRatio, s.hpAfter, s.kill,
    ].map(esc).join(','));
  }
  return lines.join('\n') + '\n';
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  fs.mkdirSync(DATA, { recursive: true });
  fs.mkdirSync(INBOX, { recursive: true });

  const demos = listDemos();
  const cache5e = listCache();
  if (args.list) {
    if (demos.length === 0) console.log('no .dem files in ' + CSGO + ', its replays, or ' + INBOX);
    for (const d of demos) console.log(`${new Date(d.mtime).toLocaleString()}  ${(d.size / 1048576).toFixed(1)} MB  ${d.path}`);
    if (cache5e.length) {
      console.log('');
      console.log('demo cache (' + DEMO_CACHE + '):');
      for (const d of cache5e) console.log(`  ${new Date(d.mtime).toLocaleString()}  ${(d.size / 1048576).toFixed(1)} MB  ${d.path}`);
    }
    return 0;
  }
  if (args.showConfig) {
    const d = readDriverInfo();
    console.log(JSON.stringify(d, null, 2));
    return d.sha ? 0 : 1;
  }

  let demo = args.demo;
  if (!demo && args.latest5e) demo = cache5e[0]?.path;
  if (!demo) demo = demos[0]?.path;
  if (!demo) {
    console.log('No CS2 demo found.');
    console.log('  game/csgo : ' + CSGO);
    console.log('  replays   : ' + REPLAYS);
    console.log('  inbox     : ' + INBOX);
    console.log('  demo cache  : ' + DEMO_CACHE);
    console.log('');
    console.log('Record one: launch CS2 -> load Aim Botz -> console (~) -> exec raui_record -> F8 start, F9 stop.');
    console.log('Or download a match demo from Watch; it lands in replays or the demo cache.');
    return 2;
  }
  if (!fs.existsSync(demo)) {
    console.log('Demo not found: ' + demo);
    return 2;
  }

  const prefs = readPrefs();
  const driver = readDriverInfo();
  const sensitivity = Number.isFinite(args.sensitivity) ? args.sensitivity
    : (Number.isFinite(Number(prefs.gameSensitivity)) ? Number(prefs.gameSensitivity) : 1.5);
  const xThreshold = Number.isFinite(args.xThreshold) ? args.xThreshold
    : (Number.isFinite(driver.xThreshold) ? driver.xThreshold : 3.0);
  const yThreshold = Number.isFinite(args.yThreshold) ? args.yThreshold
    : (Number.isFinite(driver.yThreshold) ? driver.yThreshold : 2.5);
  const activeSteamId = args.activeSteamId || readActiveSteamId();

  const outStamp = stamp();
  const base = path.basename(demo).replace(/\.dem$/i, '');
  const outDir = args.out ? path.resolve(args.out) : path.join(DATA, 'reports', `${outStamp}-${base}`);
  fs.mkdirSync(outDir, { recursive: true });

  let result;
  const st = (() => { try { return fs.statSync(demo); } catch { return null; } })();
  const recentlyWritten = st ? Date.now() - st.mtimeMs < 5000 : false;
  if (recentlyWritten) {
    console.log('NOTE: the demo was modified seconds ago. If you are still recording, press F9 (stop) first;');
    console.log('      a demo is only complete after Stop, and a partial file makes the parser reject it.');
  }

  const jobPath = path.join(outDir, 'job.json');
  const resultPath = path.join(outDir, 'result.json');
  fs.writeFileSync(jobPath, JSON.stringify({
    demo,
    out: resultPath,
    opts: {
      steamid: args.steamid,
      activeSteamId,
      sensitivity,
      xThreshold,
      yThreshold,
      tickRate: args.tickRate,
      configSha: driver.sha || 'unknown',
    },
  }, null, 2));

  console.log('Parsing the demo (single-threaded; this can take 10-60 s for an aim session)...');
  const cp = spawnSync(process.execPath, [path.join(HERE, 'worker.mjs'), jobPath], {
    encoding: 'utf8', timeout: 300000, windowsHide: true,
  });

  let workerPayload = null;
  try { workerPayload = JSON.parse(fs.readFileSync(resultPath, 'utf8')); } catch { /* panic or missing */ }
  if (cp.status === 0 && workerPayload && !workerPayload.error) {
    result = workerPayload;
  } else {
    const detail = [
      `exit status: ${cp.status}${cp.signal ? ' signal ' + cp.signal : ''}`,
      cp.error ? 'spawn error: ' + cp.error.message : '',
      workerPayload?.error ? 'worker error: ' + workerPayload.error : '',
      workerPayload?.stack ? workerPayload.stack : '',
      cp.stdout ? 'stdout:\n' + cp.stdout : '',
      cp.stderr ? 'stderr:\n' + cp.stderr : '',
      recentlyWritten ? 'The demo was modified seconds ago; it may still be open/incomplete (press F9 first).' : '',
      'If this file is a complete recording and still fails, send error.txt: it contains the parser panic line.',
    ].filter(Boolean).join('\n\n');
    fs.writeFileSync(path.join(outDir, 'error.txt'), detail + '\n');
    console.log('ANALYSIS FAILED: the parser rejected the demo.');
    console.log('Diagnostic: ' + path.join(outDir, 'error.txt'));
    return 1;
  }
  fs.rmSync(jobPath, { force: true });
  fs.rmSync(resultPath, { force: true });

  if (driver.error) result.quality.warnings.push('driver read at analysis time failed: ' + driver.error);
  if (!driver.sha) result.quality.warnings.push('config sha unknown; the report cannot prove which curve was active');
  if (driver.version) result.quality.warnings.push('driver version at analysis time: ' + driver.version);
  const demoTs = st ? st.mtimeMs : 0;
  const ageDays = demoTs ? (Date.now() - demoTs) / 86400000 : NaN;
  result.meta.demoMtime = demoTs ? new Date(demoTs).toISOString() : null;
  result.meta.demoAgeDays = Number.isFinite(ageDays) ? +ageDays.toFixed(1) : null;
  if (Number.isFinite(ageDays) && ageDays > 3)
    result.quality.warnings.push(`demo is ${ageDays.toFixed(0)} days old; the config sha was read at analysis time, not at match time - treat this as a historical baseline, not a controlled arm`);

  // Protect a fixed-name recording from being overwritten by the next session.
  let archivedAs = '';
  const demoBase = path.basename(demo);
  if (!args.noRename && /^raui_session/i.test(demoBase) && !/^raui_session-\d{4}-\d{2}-\d{2}_/.test(demoBase)) {
    const target = path.join(path.dirname(demo), path.basename(demo).replace(/\.dem$/i, `-${outStamp}.dem`));
    try { fs.renameSync(demo, target); archivedAs = target; result.meta.demo = target; }
    catch (e) { result.quality.warnings.push('could not rename the recording; analyze before recording over it: ' + e.message); }
  }

  result.meta.archivedAs = archivedAs;
  result.meta.reportDir = outDir;
  const jsonPath = path.join(outDir, 'report.json');
  const txtPath = path.join(outDir, 'report.txt');
  const csvPath = path.join(outDir, 'shots.csv');
  fs.writeFileSync(jsonPath, JSON.stringify(result, null, 2));
  fs.writeFileSync(txtPath, writeText(result) + '\n');
  fs.writeFileSync(csvPath, writeCsv(result));

  const a = result.aggregates;
  const summary = {
    at: new Date().toISOString(),
    demo: result.meta.demo,
    map: result.meta.map,
    tickRate: result.meta.tickRate,
    player: result.player,
    configSha: a.configSha,
    sensitivity: a.sensitivity,
    xThreshold: a.xThreshold,
    yThreshold: a.yThreshold,
    demoAgeDays: result.meta.demoAgeDays,
    shots: a.shots,
    hits: a.hits,
    hitPct: a.hitPct,
    shotHitPct: a.shotHitPct,
    kills: a.kills,
    aimErrorMedianDeg: a.aimErrorMedianDeg,
    aimErrorMedianOnHitsDeg: a.aimErrorMedianOnHitsDeg,
    flickPeakMedianCms: a.flicks.peakMedianCms,
    aboveXThresholdPct: a.flicks.aboveXThresholdPct,
    aboveYThresholdPct: a.flicks.aboveYThresholdPct,
    overshootMedianDeg: a.flicks.overshootMedianDeg,
    reportDir: outDir,
    warnings: result.quality.warnings,
  };
  fs.appendFileSync(path.join(DATA, 'index.jsonl'), JSON.stringify(summary) + '\n');

  console.log('REPORT: ' + txtPath);
  console.log('JSON  : ' + jsonPath);
  console.log('CSV   : ' + csvPath);
  console.log(`shots=${a.shots} hits=${a.hits} shotHit=${pct(a.shotHitPct)} aimErrMed=${fmt(a.aimErrorMedianDeg)} flickP50=${fmt(a.flicks.peakMedianCms)} horizP50=${fmt(a.flicks.peakYawP50Cms)} vertP50=${fmt(a.flicks.peakPitchP50Cms)} aboveX=${pct(a.flicks.aboveXThresholdPct)} aboveY=${pct(a.flicks.aboveYThresholdPct)}`);
  if (result.quality.warnings.length) console.log('WARN  : ' + result.quality.warnings.join(' | '));
  return 0;
}

process.exit(main());
