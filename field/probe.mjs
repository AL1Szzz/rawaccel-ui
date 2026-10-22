// Raw structure dump for the first CS2 demo. This is the calibration tool:
// it prints exactly which fields the parser returns before the report tool
// relies on them. Run: node field/probe.mjs "<demo.dem>"
import { createRequire } from 'node:module';
import fs from 'node:fs';

const require = createRequire(import.meta.url);
const demo = process.argv[2];
if (!demo) {
  console.log('usage: node field/probe.mjs <demo.dem>');
  process.exit(2);
}
if (!fs.existsSync(demo)) {
  console.log('not found: ' + demo);
  process.exit(2);
}

let dp;
try { dp = require('@laihoe/demoparser2'); }
catch (e) { console.log('cannot load parser: ' + e.message); process.exit(2); }

const show = (label, value, max = 4000) => {
  console.log('\n===== ' + label + ' =====');
  let s;
  try { s = JSON.stringify(value, (k, v) => typeof v === 'bigint' ? String(v) : v, 2); }
  catch (e) { s = String(value); }
  console.log(s.length > max ? s.slice(0, max) + `\n... (${s.length} chars total)` : s);
};

try {
  show('header', dp.parseHeader(demo));
} catch (e) { console.log('parseHeader failed: ' + e.message); }

try {
  const players = dp.parsePlayerInfo(demo);
  show('players', players);
} catch (e) { console.log('parsePlayerInfo failed: ' + e.message); }

try {
  const evs = dp.listGameEvents(demo);
  const arr = Array.isArray(evs) ? evs : Object.keys(evs || {});
  show('game events (names)', arr.slice(0, 300), 8000);
} catch (e) { console.log('listGameEvents failed: ' + e.message); }

let events = [];
try {
  events = dp.parseEvents(demo, ['weapon_fire', 'player_hurt', 'player_death'], [], []);
  const list = Array.isArray(events) ? events : Object.values(events || {}).flat();
  console.log(`\n===== events: ${list.length} =====`);
  const by = {};
  for (const e of list) {
    const n = e.event_name || e.event || e.name || '?';
    by[n] = (by[n] || 0) + 1;
  }
  console.log(by);
  for (const n of ['weapon_fire', 'player_hurt', 'player_death']) {
    const one = list.find(e => (e.event_name || e.event || e.name) === n);
    if (one) show('first ' + n, one, 3000);
  }

  const firstShot = list.find(e => (e.event_name || e.event || e.name) === 'weapon_fire');
  if (firstShot && Number.isFinite(Number(firstShot.tick))) {
    const t = Number(firstShot.tick);
    const ticks = [];
    for (let d = -32; d <= 32; d++) ticks.push(t + d);
    const rows = dp.parseTicks(demo, ['X', 'Y', 'Z', 'yaw', 'pitch', 'health', 'team_num', 'is_alive', 'active_weapon_name'], ticks, null);
    console.log(`\n===== ticks around first shot (tick ${t}): ${Array.isArray(rows) ? rows.length : '?'} rows =====`);
    show('first 8 tick rows', (Array.isArray(rows) ? rows : []).slice(0, 8), 8000);
  }
} catch (e) {
  console.log('parseEvents failed: ' + e.message);
  try {
    const one = dp.parseEvent(demo, 'weapon_fire', [], []);
    show('parseEvent weapon_fire sample', Array.isArray(one) ? one.slice(0, 3) : one);
  } catch (e2) { console.log('parseEvent fallback failed: ' + e2.message); }
}
