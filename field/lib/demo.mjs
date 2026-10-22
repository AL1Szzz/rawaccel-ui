// Demo -> field report. The only inputs are a CS2 .dem file and (optionally) the
// live driver config read at analysis time. Nothing here touches the game.
//
// Measurement layers and their limits:
//   demo view angles: 64/128 tick (15.6/7.8 ms bins) - cannot see 2 ms jitter
//   nearest enemy:    no visibility/occlusion model; a "nearest enemy" label,
//                      never "the target he was actually aiming at"
//   hits:             player_hurt events are real; misses have no event

import {
  angleDeg, countsPerMs, EYE_HEIGHT_UNITS, mean, median, quantile, sub,
  UNITS_TO_M, viewFromYawPitch,
} from './angles.mjs';
import { analyzeFlickAt, buildViewSeries, indexAtTick, summarizeFlicks } from './flick.mjs';

const isNum = (v) => typeof v === 'number' && Number.isFinite(v);
const asArray = (x) => Array.isArray(x) ? x : (x && typeof x === 'object' ? Object.values(x) : []);
const pick = (obj, keys, fallback = undefined) => {
  if (!obj || typeof obj !== 'object') return fallback;
  for (const k of keys) if (obj[k] !== undefined && obj[k] !== null) return obj[k];
  return fallback;
};
const str = (v) => v === undefined || v === null ? '' : String(v);
const toNum = (v) => {
  const n = typeof v === 'number' ? v : parseFloat(v);
  return Number.isFinite(n) ? n : NaN;
};

// Grenades and knives are not aim samples. The first first report counted 21
// butterfly-knife "shots" and 37 utility throws as flicks; they inflated the
// distance buckets and the hit rate without measuring aim at all.
const UTILITY_RE = /^weapon_(knife|bayonet|c4|taser|smokegrenade|hegrenade|flashbang|incgrenade|molotov|decoy|snowball|healthshot|breachcharge|bumpmine|shield|melee|sensorgrenade)/i;
const isGunWeapon = (w) => {
  const s = str(w);
  if (!s) return true;                 // unknown weapon: do not silently drop the shot
  return /^weapon_/i.test(s) && !UTILITY_RE.test(s);
};
const normWeapon = (w) => str(w).replace(/^weapon_/i, '');

const eventName = (e) => str(pick(e, ['event_name', 'event', 'name'], '')).toLowerCase();
const tickOf = (e) => Math.round(toNum(pick(e, ['tick'], NaN)));
const weaponOf = (e) => str(pick(e, ['weapon', 'weapon_name'], ''));

function actorOf(e, name) {
  if (name === 'player_hurt' || name === 'player_death') {
    return str(pick(e, ['attacker_steamid', 'attacker_steamid64', 'attacker', 'attacker_name'], ''));
  }
  return str(pick(e, ['user_steamid', 'user_steamid64', 'player_steamid', 'steamid', 'user', 'user_name'], ''));
}

function victimOf(e) {
  return str(pick(e, ['user_steamid', 'user_steamid64', 'victim_steamid', 'victim', 'userid', 'user_name'], ''));
}

function playerId(p) {
  return str(pick(p, ['steamid', 'steam_id', 'steamId', 'SteamID', 'user_steamid', 'steamid64'], ''));
}
function playerName(p) {
  return str(pick(p, ['name', 'user_name', 'player_name'], ''));
}
function playerIsBot(p) {
  const b = pick(p, ['is_bot', 'bot', 'isBot'], undefined);
  if (typeof b === 'boolean') return b;
  if (typeof b === 'number') return b !== 0;
  return /^bot\b/i.test(playerName(p)) || playerId(p) === '0';
}

function normalizePlayers(raw) {
  return asArray(raw).map(p => ({
    id: playerId(p),
    name: playerName(p),
    team: toNum(pick(p, ['team_num', 'team_number', 'team'], NaN)),
    isBot: playerIsBot(p),
    raw: p,
  })).filter(p => p.id || p.name);
}

function eventsOf(dp, demo, names, warnings) {
  try {
    const evs = dp.parseEvents(demo, names, [], []);
    const list = asArray(evs);
    if (list.length > 0 && eventName(list[0])) return list;
    // Some builds return an object keyed by event name.
    const flat = [];
    for (const value of Object.values(evs || {})) flat.push(...asArray(value));
    if (flat.length) return flat;
    return list;
  } catch (e) {
    warnings.push('parseEvents failed: ' + e.message + '; falling back to parseEvent per type');
  }
  const out = [];
  for (const n of names) {
    try { out.push(...asArray(dp.parseEvent(demo, n, [], []))); }
    catch (e) { warnings.push(`parseEvent(${n}) failed: ${e.message}`); }
  }
  return out;
}

const PROPS_FULL = ['X', 'Y', 'Z', 'yaw', 'pitch', 'health', 'team_num', 'is_alive', 'active_weapon_name'];
const PROPS_MIN = ['X', 'Y', 'Z', 'yaw', 'pitch'];

function ticksOf(dp, demo, props, wantedTicks, wantedPlayers, warnings) {
  const attempts = [
    props,
    props.filter(p => p !== 'active_weapon_name'),
    props.filter(p => !['active_weapon_name', 'is_alive', 'team_num'].includes(p)),
    PROPS_MIN,
  ];
  let lastError;
  for (const attempt of attempts) {
    try {
      const rows = asArray(dp.parseTicks(demo, attempt, wantedTicks ?? null, wantedPlayers ?? null));
      if (rows.length) return rows;
    } catch (e) { lastError = e; }
  }
  warnings.push('parseTicks failed: ' + (lastError?.message ?? 'no rows'));
  return [];
}

function rowId(r) {
  return str(pick(r, ['steamid', 'steam_id', 'steamId', 'user_steamid'], '')) || str(pick(r, ['name'], ''));
}

function findPlayerInMap(m, id, name) {
  if (!m || m.size === 0) return null;
  if (id && m.get(id)) return m.get(id);
  if (name) {
    for (const r of m.values()) if (str(pick(r, ['name'], '')) === name) return r;
  }
  if (id) {
    for (const [k, r] of m) if (k === id || k.toLowerCase() === id.toLowerCase()) return r;
  }
  return null;
}

function rowMatches(r, id, name) {
  const rid = rowId(r);
  if (id && rid === id) return true;
  const rn = str(pick(r, ['name'], ''));
  return !!(name && rn === name);
}

function buildByTick(rows) {
  const byTick = new Map();
  for (const r of rows) {
    const t = Math.round(toNum(pick(r, ['tick'], NaN)));
    if (!Number.isFinite(t)) continue;
    const id = rowId(r);
    if (!byTick.has(t)) byTick.set(t, new Map());
    byTick.get(t).set(id, r);
  }
  return byTick;
}

function eyeOf(row) {
  return [toNum(row.X), toNum(row.Y), toNum(row.Z) + EYE_HEIGHT_UNITS];
}

function teamOf(row) {
  return toNum(pick(row, ['team_num', 'team_number', 'team'], NaN));
}

function aliveOf(row) {
  const a = pick(row, ['is_alive', 'health'], undefined);
  if (a === undefined) return true;
  if (typeof a === 'boolean') return a;
  const n = toNum(a);
  return !Number.isFinite(n) || n > 0;
}

function bucketOf(distM) {
  if (!Number.isFinite(distM)) return 'unknown';
  if (distM < 5) return '0-5m';
  if (distM < 10) return '5-10m';
  if (distM < 20) return '10-20m';
  if (distM < 30) return '20-30m';
  return '30m+';
}

/**
 * Sustained-fire bursts. A "flick" ending at a shot is mostly a single
 * movement; spray control is a different regime, and it is the one where the
 * Y curve can actually matter. Same weapon, gap <= 160 ms, >= 3 shots.
 */
function analyzeSpray(shots, series, tickRate, opts) {
  const list = shots.filter(s => s.weapon && Number.isFinite(s.tick)).sort((a, b) => a.tick - b.tick);
  const bursts = [];
  let cur = [];
  for (const s of list) {
    const prev = cur[cur.length - 1];
    if (!prev || (s.weapon === prev.weapon && s.tick - prev.tick <= 0.16 * tickRate)) cur.push(s);
    else { if (cur.length >= 3) bursts.push(cur); cur = [s]; }
  }
  if (cur.length >= 3) bursts.push(cur);

  const rows = [];
  for (const b of bursts) {
    const t0 = b[0].tick, t1 = b[b.length - 1].tick;
    const i0 = indexAtTick(series, t0), i1 = indexAtTick(series, t1);
    if (i0 < 0 || i1 < i0) continue;
    let peakPitch = 0, peakYaw = 0;
    for (let i = i0; i <= i1; i++) {
      if (series[i].pitchSpeed > peakPitch) peakPitch = series[i].pitchSpeed;
      if (series[i].yawSpeed > peakYaw) peakYaw = series[i].yawSpeed;
    }
    rows.push({
      shots: b.length,
      durationMs: (t1 - t0) / tickRate * 1000,
      peakPitchCms: countsPerMs(peakPitch, opts.sensitivity ?? 1.5),
      peakYawCms: countsPerMs(peakYaw, opts.sensitivity ?? 1.5),
      hitPct: 100 * b.filter(s => s.hit).length / b.length,
    });
  }
  if (rows.length === 0) return null;
  const xT = opts.xThreshold ?? 3, yT = opts.yThreshold ?? 2.5;
  const totalShots = rows.reduce((s, r) => s + r.shots, 0);
  return {
    bursts: rows.length,
    shots: totalShots,
    durationMedianMs: median(rows.map(r => r.durationMs)),
    // NET view pitch: recoil pushes the view up while the hand pulls down.
    // A well-compensated burst has near-zero NET pitch while the mouse Y is
    // moving. This cannot decide whether the Y curve armed; use 测压枪Y.
    netVerticalP50Cms: median(rows.map(r => r.peakPitchCms)),
    netHorizontalP50Cms: median(rows.map(r => r.peakYawCms)),
    netAboveYPct: 100 * rows.filter(r => r.peakPitchCms >= yT).length / rows.length,
    netAboveXPct: 100 * rows.filter(r => r.peakYawCms >= xT).length / rows.length,
    hitPct: rows.reduce((s, r) => s + r.hitPct * r.shots, 0) / totalShots,
  };
}

function aggregate(shots, opts) {  const valid = shots.filter(s => s);
  const victims = valid.filter(s => s.victimId);
  const hits = valid.filter(s => s.hit);
  const errors = victims.map(s => s.aimErrorDeg).filter(Number.isFinite);
  const hitErrors = hits.map(s => s.aimErrorDeg).filter(Number.isFinite);
  const flicks = valid.map(s => s.flick).filter(Boolean);
  const flickSum = summarizeFlicks(flicks, opts);
  const byDistance = {};
  for (const b of ['0-5m', '5-10m', '10-20m', '20-30m', '30m+', 'unknown']) {
    const g = victims.filter(s => s.distanceBucket === b);
    if (!g.length) continue;
    byDistance[b] = {
      n: g.length,
      hitPct: 100 * g.filter(s => s.hit).length / g.length,
      aimErrorMedianDeg: median(g.map(s => s.aimErrorDeg)),
      peakMedianCms: median(g.map(s => s.flick?.peakCms).filter(Number.isFinite)),
    };
  }
  const byWeapon = {};
  for (const w of [...new Set(valid.map(s => s.weapon).filter(Boolean))]) {
    const g = valid.filter(s => s.weapon === w);
    const gh = g.filter(s => s.victimId);
    byWeapon[w] = {
      n: g.length,
      hits: g.filter(s => s.hit).length,
      hitPct: gh.length ? 100 * g.filter(s => s.hit).length / gh.length : NaN,
      aimErrorMedianDeg: median(gh.map(s => s.aimErrorDeg)),
      headshots: g.filter(s => s.hitgroup === 'head').length,
    };
  }
  return {
    shots: valid.length,
    hits: hits.length,
    kills: opts.kills ?? 0,
    utilityKills: opts.utilityKills ?? 0,
    excludedUtilityShots: opts.excludedUtilityShots ?? 0,
    hitPct: victims.length ? 100 * hits.length / victims.length : NaN,
    shotHitPct: valid.length ? 100 * hits.length / valid.length : NaN,
    headshots: hits.filter(s => s.hitgroup === 'head').length,
    headshotPctOfHits: hits.length ? 100 * hits.filter(s => s.hitgroup === 'head').length / hits.length : NaN,
    aimErrorMedianDeg: median(errors),
    aimErrorMaeDeg: mean(errors.map(Math.abs)),
    aimErrorP95Deg: quantile(errors, 0.95),
    aimErrorMedianOnHitsDeg: median(hitErrors),
    distanceMedianM: median(victims.map(s => s.distanceM)),
    ttkMedianMs: median(opts.ttks ?? []),
    spray: opts.spray ?? null,
    byDistance,
    byWeapon,
    flicks: flickSum,
    configSha: opts.configSha ?? 'unknown',
    sensitivity: opts.sensitivity,
    xThreshold: opts.xThreshold,
    yThreshold: opts.yThreshold,
  };
}

function analyzePovEvents(header, events, opts, warnings) {
  const shotsAll = events.filter(e => eventName(e) === 'weapon_fire');
  const gunShots = shotsAll.filter(e => opts.includeUtility || isGunWeapon(weaponOf(e)));
  const excludedUtilityShots = shotsAll.length - gunShots.length;
  const hurts = events.filter(e => eventName(e) === 'player_hurt');
  const deaths = events.filter(e => eventName(e) === 'player_death');
  const hurtByTick = new Map();
  for (const h of hurts) {
    const t = tickOf(h);
    if (!Number.isFinite(t)) continue;
    if (!hurtByTick.has(t)) hurtByTick.set(t, []);
    hurtByTick.get(t).push(h);
  }

  const shots = [];
  for (const e of gunShots) {
    const tick = tickOf(e);
    const shotW = normWeapon(weaponOf(e));
    let hurt = null;
    for (let d = -2; d <= 2 && !hurt; d++) {
      const list = hurtByTick.get(tick + d);
      if (!list) continue;
      const mine = list.filter(h => {
        const hw = normWeapon(weaponOf(h));
        return !shotW || !hw || hw === shotW;
      });
      if (mine.length) {
        mine.sort((a, b) => toNum(pick(b, ['dmg_health', 'damage'], 0)) - toNum(pick(a, ['dmg_health', 'damage'], 0)));
        hurt = mine[0];
      }
    }
    shots.push({
      tick,
      timeMs: null,
      weapon: weaponOf(e),
      distanceM: null,
      distanceBucket: 'unknown',
      // A POV demo has no victim entities. This synthetic id keeps the
      // denominator "one shot" instead of "shots with a known victim".
      victimId: 'pov',
      victimName: '',
      hit: !!hurt,
      hitgroup: hurt ? str(pick(hurt, ['hitgroup'], '')) : '',
      damage: hurt ? toNum(pick(hurt, ['dmg_health', 'damage'], 0)) : 0,
      // player_hurt carries the victim's health AFTER the hit and player_death
      // fires on the tick the body drops. Together they let a downstream tool
      // split a magazine into bodies exactly, instead of assuming 100 HP.
      hpAfter: hurt ? toNum(pick(hurt, ['health'], NaN)) : NaN,
      kill: 0,
      aimErrorDeg: null,
      peakCms: null, peakDegPerSec: null, peakYawCms: null, peakPitchCms: null,
      overshootDeg: null, reversals: null, corrected: null, direction: '',
      durationMs: null, arcRatio: null, flick: null,
    });
  }

  // Tag the shot that dropped a body. Death ticks are consumed one-to-one so a
  // burst of 30 with 4 deaths gets exactly 4 kill flags.
  const deathTicks = deaths.map(tickOf).filter(Number.isFinite).sort((a, b) => a - b);
  const used = new Set();
  for (const s of shots) {
    for (const t of deathTicks) {
      if (used.has(t) || Math.abs(t - s.tick) > 2) continue;
      s.kill = 1; used.add(t); break;
    }
  }

  const ttks = [];
  let kills = 0, utilityKills = 0;
  for (const d of deaths) {
    const deathW = normWeapon(weaponOf(d));
    if (deathW && !isGunWeapon('weapon_' + deathW)) { utilityKills++; continue; }
    kills++;
  }

  const aggregates = aggregate(shots, {
    kills, ttks, utilityKills, excludedUtilityShots,
    configSha: opts.configSha,
    sensitivity: opts.sensitivity,
    xThreshold: opts.xThreshold,
    yThreshold: opts.yThreshold,
  });

  warnings.push('POV / client-side demo: player entities and view angles are not present. Hit/headshot/kill/TTK come from events only.');
  warnings.push('This cannot produce flick peaks, overshoot or aim error. For those, use a server-side demo (TV) or the GSI logger.');
  warnings.push('Event counts assume every weapon_fire/hurt/death in this local aim map belongs to the recorded player (true for Aim Botz, where bots do not shoot).');
  warnings.push('TTK and distance buckets are not computed in POV mode: there are no victim ids/positions to pair a kill with its first damage.');

  return {
    meta: {
      demo: '', // filled by the caller/report path
      map: str(pick(header, ['map_name', 'map', 'name'], '')) ||
        (str(pick(header, ['addons'], '')) ? 'workshop/' + str(header.addons) : ''),
      tickRate: 64,
      header,
      generatedAt: new Date().toISOString(),
      parser: 'demoparser2 (node bindings)',
      pitchDownPositive: true,
      pov: true,
    },
    player: { id: '', name: str(pick(header, ['client_name'], 'POV player')), team: NaN },
    players: [],
    aggregates,
    shots,
    quality: {
      warnings,
      victims: shots.length,
      shotsWithFlick: 0,
      calibrationHits: 0,
      measurement: 'POV event-level only: no 64/128-tick view angles are available in this demo stream',
    },
  };
}

/**
 * Parse and summarize one demo. Returns a plain object; no file writes.
 */
export function analyzeDemo(dp, demoPath, opts = {}) {
  const warnings = [];
  const header = (() => { try { return dp.parseHeader(demoPath) || {}; } catch (e) { warnings.push('parseHeader: ' + e.message); return {}; } })();
  const players = normalizePlayers((() => { try { return dp.parsePlayerInfo(demoPath); } catch (e) { warnings.push('parsePlayerInfo: ' + e.message); return []; } })());
  const events = eventsOf(dp, demoPath, ['weapon_fire', 'player_hurt', 'player_death', 'round_start', 'round_end'], warnings);
  if (players.length === 0 && events.some(e => eventName(e) === 'weapon_fire')) {
    const pov = analyzePovEvents(header, events, opts, warnings);
    pov.meta.demo = demoPath;
    return pov;
  }

  const map = str(pick(header, ['map_name', 'map', 'name'], '')) ||
    (str(pick(header, ['addons'], '')) ? 'workshop/' + str(header.addons) : '');
  const rawTickRate = toNum(pick(header, ['tick_rate', 'tickrate', 'tickRate'], NaN));
  const tickRate = Number.isFinite(rawTickRate) && rawTickRate > 1 ? rawTickRate : (opts.tickRate ?? 64);
  if (!Number.isFinite(rawTickRate)) warnings.push('header has no tick_rate; assuming ' + tickRate);

  const shotsAll = events.filter(e => eventName(e) === 'weapon_fire');
  const hurts = events.filter(e => eventName(e) === 'player_hurt');
  const deaths = events.filter(e => eventName(e) === 'player_death');
  const shotCounts = new Map();
  for (const e of shotsAll) {
    const a = actorOf(e, 'weapon_fire');
    if (a) shotCounts.set(a, (shotCounts.get(a) || 0) + 1);
  }

  // Pick the player. Explicit > single human > active account > most shots.
  let target = null;
  if (opts.steamid) target = players.find(p => p.id === str(opts.steamid)) || { id: str(opts.steamid), name: str(opts.steamid), team: NaN, isBot: false };
  if (!target) {
    const humans = players.filter(p => !p.isBot);
    if (humans.length === 1) target = humans[0];
  }
  if (!target && opts.activeSteamId) target = players.find(p => p.id === str(opts.activeSteamId)) || null;
  if (!target) {
    const ranked = players.slice().sort((a, b) => (shotCounts.get(b.id) || 0) - (shotCounts.get(a.id) || 0));
    target = ranked[0] || null;
  }
  if (!target) {
    const rankedIds = [...shotCounts.entries()].sort((a, b) => b[1] - a[1]);
    if (rankedIds.length) target = { id: rankedIds[0][0], name: rankedIds[0][0], team: NaN, isBot: false };
  }
  if (!target) throw new Error('no player could be identified in this demo');
  const targetId = str(target.id);
  const targetName = str(target.name);

  let targetRows = ticksOf(dp, demoPath, PROPS_FULL, null, [targetId], warnings);
  if (targetRows.length === 0 && targetName) targetRows = ticksOf(dp, demoPath, PROPS_FULL, null, [targetName], warnings);
  if (targetRows.length === 0) targetRows = ticksOf(dp, demoPath, PROPS_FULL, null, null, warnings);
  targetRows = targetRows.filter(r => rowMatches(r, targetId, targetName));
  if (targetRows.length === 0) {
    // Some parser builds ignore wantedPlayers and return every player; if the
    // id/name filter above was too strict, fall back to the raw set and let the
    // report say which player was used.
    targetRows = ticksOf(dp, demoPath, PROPS_FULL, null, null, warnings);
    if (targetRows.length === 0) throw new Error(`no tick rows for player ${targetId || targetName}`);
    warnings.push('tick rows were not filterable by player id/name; using the first player in the stream');
  }
  const series = buildViewSeries(targetRows, tickRate, true);
  if (series.length < 4) throw new Error('view series has fewer than 4 ticks');

  const shotEventsAll = shotsAll.filter(e => {
    const a = actorOf(e, 'weapon_fire');
    return a === targetId || (targetName && a === targetName);
  });
  const shotEvents = shotEventsAll.filter(e => opts.includeUtility || isGunWeapon(weaponOf(e)));
  const excludedUtilityShots = shotEventsAll.length - shotEvents.length;

  // Enemies around each shot: 64 ticks before/after is enough for a flick and
  // keeps the parse far below "all players, all ticks".
  const wanted = new Set();
  for (const e of shotEvents) {
    const t = tickOf(e);
    if (!Number.isFinite(t)) continue;
    for (let d = -64; d <= 64; d++) wanted.add(t + d);
  }
  const wantedTicks = [...wanted].sort((a, b) => a - b).slice(0, 60000);
  const allRows = wantedTicks.length ? ticksOf(dp, demoPath, PROPS_FULL, wantedTicks, null, warnings) : [];
  const byTick = buildByTick(allRows);

  const hurtByShot = new Map();
  for (const h of hurts) {
    const t = tickOf(h);
    if (!Number.isFinite(t)) continue;
    if (!hurtByShot.has(t)) hurtByShot.set(t, []);
    hurtByShot.get(t).push(h);
  }

  const rawShots = [];
  for (const e of shotEvents) {
    const tick = tickOf(e);
    if (!Number.isFinite(tick)) continue;
    const idx = indexAtTick(series, tick);
    if (idx < 0 || Math.abs(series[idx].tick - tick) > 3) continue;
    const row = series[idx].sample;
    const selfEye = eyeOf(row);
    const selfTeam = teamOf(row);

    // A hurt event within +-2 ticks is the strongest victim evidence, but only
    // the SAME weapon counts: a knife/grenade hurt at the same tick as a gun
    // shot is a different event and used to be paired as a "hit".
    let hurt = null;
    const shotW = normWeapon(weaponOf(e));
    for (let d = -2; d <= 2 && !hurt; d++) {
      const list = hurtByShot.get(tick + d);
      if (!list) continue;
      const mine = list.filter(h => {
        const a = actorOf(h, 'player_hurt');
        if (!(a === targetId || (targetName && a === targetName))) return false;
        const hw = normWeapon(weaponOf(h));
        return !shotW || !hw || hw === shotW;
      });
      if (mine.length) {
        mine.sort((a, b) => toNum(pick(b, ['dmg_health', 'damage'], 0)) - toNum(pick(a, ['dmg_health', 'damage'], 0)));
        hurt = mine[0];
      }
    }

    let victimId = hurt ? victimOf(hurt) : '';
    const victimNameHit = hurt ? str(pick(hurt, ['user_name', 'victim_name'], '')) : '';
    let victimRow = victimId ? findPlayerInMap(nearestTickMap(byTick, tick), victimId, victimNameHit) : null;

    if (!victimId) {
      // nearest alive enemy, only within a 60-degree cone; this is an
      // approximation, not a claim about who he meant to shoot.
      const rowsHere = nearestTickMap(byTick, tick);
      let best = null, bestScore = Infinity;
      const provisionalView = viewFromYawPitch(series[idx].yaw, series[idx].pitch, true);
      for (const [id, r] of rowsHere) {
        if (!id || id === targetId || rowMatches(r, targetId, targetName)) continue;
        if (!aliveOf(r)) continue;
        const team = teamOf(r);
        if (Number.isFinite(selfTeam) && Number.isFinite(team) && team === selfTeam) continue;
        const d = sub(eyeOf(r), selfEye);
        const dist = Math.hypot(d[0], d[1], d[2]);
        if (!(dist > 1)) continue;
        const ang = angleDeg(provisionalView, d);
        if (ang > 60) continue;
        const score = dist * (1 + ang / 90);
        if (score < bestScore) { bestScore = score; best = { id, row: r }; }
      }
      if (best) { victimId = best.id; victimRow = best.row; }
    }

    let aimErrorDeg = NaN, distanceM = NaN;
    let victimTeam = NaN;
    if (victimRow) {
      const d = sub(eyeOf(victimRow), selfEye);
      const distUnits = Math.hypot(d[0], d[1], d[2]);
      distanceM = distUnits * UNITS_TO_M;
      victimTeam = teamOf(victimRow);
      aimErrorDeg = angleDeg(viewFromYawPitch(series[idx].yaw, series[idx].pitch, true), d);
      if (!victimId) victimId = rowId(victimRow);
    }

    rawShots.push({
      tick,
      timeMs: series[idx].timeMs,
      weapon: weaponOf(e) || str(pick(row, ['active_weapon_name'], '')),
      selfEye,
      viewYaw: series[idx].yaw,
      viewPitch: series[idx].pitch,
      victimId: victimId || '',
      victimName: victimRow ? str(pick(victimRow, ['name'], victimId)) : victimNameHit,
      victimRow,
      victimTeam,
      hit: !!hurt,
      hitgroup: hurt ? str(pick(hurt, ['hitgroup'], '')) : '',
      damage: hurt ? toNum(pick(hurt, ['dmg_health', 'damage'], 0)) : 0,
      aimErrorDeg,
      distanceM,
      distanceBucket: bucketOf(distanceM),
      flick: null,
      srcIdx: idx,
    });
  }

  // Calibrate the pitch sign against actual hits instead of assuming it.
  let pitchDownPositive = true;
  const withVictim = rawShots.filter(s => s.victimRow);
  const hitSeries = withVictim.filter(s => s.hit);
  const calibrate = (list) => {
    const t = [], f = [];
    for (const s of list) {
      const d = sub(eyeOf(s.victimRow), s.selfEye);
      t.push(angleDeg(viewFromYawPitch(s.viewYaw, s.viewPitch, true), d));
      f.push(angleDeg(viewFromYawPitch(s.viewYaw, s.viewPitch, false), d));
    }
    return { t: median(t), f: median(f) };
  };
  if (hitSeries.length >= 3) {
    const c = calibrate(hitSeries);
    if (Number.isFinite(c.t) && Number.isFinite(c.f) && c.f < c.t) pitchDownPositive = false;
    if (Number.isFinite(c.t) && Number.isFinite(c.f) && Math.min(c.t, c.f) > 5)
      warnings.push(`pitch calibration still leaves ${Math.min(c.t, c.f).toFixed(1)} deg median error on hits - check yaw/pitch conventions`);
  } else if (withVictim.length >= 3) {
    const c = calibrate(withVictim);
    if (Number.isFinite(c.t) && Number.isFinite(c.f) && c.f < c.t) pitchDownPositive = false;
  }

  const shots = [];
  for (const s of rawShots) {
    let aimErrorDeg = NaN;
    if (s.victimRow) {
      const d = sub(eyeOf(s.victimRow), s.selfEye);
      aimErrorDeg = angleDeg(viewFromYawPitch(s.viewYaw, s.viewPitch, pitchDownPositive), d);
    }
    const flick = analyzeFlickAt(series, s.srcIdx, {
      sensitivity: opts.sensitivity,
      xThreshold: opts.xThreshold,
      yThreshold: opts.yThreshold,
    });
    shots.push({
      tick: s.tick,
      timeMs: Math.round(s.timeMs),
      weapon: s.weapon,
      distanceM: Number.isFinite(s.distanceM) ? +s.distanceM.toFixed(2) : null,
      distanceBucket: s.distanceBucket,
      victimId: s.victimId,
      victimName: s.victimName || (s.victimRow ? str(pick(s.victimRow, ['name'], s.victimId)) : ''),
      hit: s.hit,
      hitgroup: s.hitgroup,
      damage: s.damage,
      aimErrorDeg: Number.isFinite(aimErrorDeg) ? +aimErrorDeg.toFixed(2) : null,
      peakCms: flick ? +flick.peakCms.toFixed(2) : null,
      peakDegPerSec: flick ? +flick.peakDegPerSec.toFixed(1) : null,
      overshootDeg: flick ? +flick.overshootDeg.toFixed(2) : null,
      reversals: flick ? flick.reversals : null,
      corrected: flick ? flick.corrected : null,
      direction: flick ? flick.direction : '',
      durationMs: flick ? Math.round(flick.durationMs) : null,
      arcRatio: flick ? +flick.arcRatio.toFixed(3) : null,
      flick,
    });
  }

  // Kills and time-to-kill: first damage by this player on the victim to death.
  // A grenade/knife kill is not an aim outcome and is counted separately.
  const ttks = [];
  let kills = 0, utilityKills = 0;
  for (const d of deaths) {
    const a = actorOf(d, 'player_death');
    if (a !== targetId && !(targetName && a === targetName)) continue;
    const deathW = normWeapon(weaponOf(d));
    if (deathW && !isGunWeapon('weapon_' + deathW)) { utilityKills++; continue; }
    kills++;
    const v = victimOf(d);
    const tDeath = tickOf(d);
    let first = null;
    for (const h of hurts) {
      if (victimOf(h) !== v) continue;
      const a2 = actorOf(h, 'player_hurt');
      if (a2 !== targetId && !(targetName && a2 === targetName)) continue;
      const t = tickOf(h);
      if (t <= tDeath && tDeath - t <= 256 && (first === null || t < first)) first = t;
    }
    if (first !== null) ttks.push((tDeath - first) / tickRate * 1000);
  }

  const aggregates = aggregate(shots, {
    kills, ttks, utilityKills, excludedUtilityShots,
    spray: analyzeSpray(shots, series, tickRate, opts),
    configSha: opts.configSha,
    sensitivity: opts.sensitivity,
    xThreshold: opts.xThreshold,
    yThreshold: opts.yThreshold,
  });

  const playerList = players.map(p => ({
    id: p.id, name: p.name, team: p.team, isBot: p.isBot,
    shots: shotCounts.get(p.id) || 0,
  })).sort((a, b) => b.shots - a.shots);

  if (shots.length < 20) warnings.push(`only ${shots.length} gun shots identified; fewer than ~30 is weak for a session verdict`);
  if (excludedUtilityShots > 0) warnings.push(`excluded ${excludedUtilityShots} knife/grenade throws from the aim sample`);
  if (victimCount(shots) === 0) warnings.push('no victim positions found; aim-error columns are empty (bots or missing entity props)');
  if (aggregates.hitPct < 1) warnings.push('no hits in this demo; if this is an aim map, check that player_hurt was parsed');
  if (map && /^(de_|cs_)/i.test(map) === false && !/workshop/i.test(map)) {
    // not a warning by itself; the map label is enough
  }

  return {
    meta: {
      demo: demoPath,
      map,
      tickRate,
      header,
      generatedAt: new Date().toISOString(),
      parser: 'demoparser2 (node bindings)',
      pitchDownPositive,
    },
    player: { id: targetId, name: targetName, team: target.team },
    players: playerList,
    aggregates,
    shots,
    quality: {
      warnings,
      victims: victimCount(shots),
      shotsWithFlick: shots.filter(s => s.flick).length,
      calibrationHits: hitSeries.length,
      measurement: `demo ticks are ${tickRate} Hz (${(1000 / tickRate).toFixed(1)} ms bins); sub-tick packet timing is not visible here`,
    },
  };
}

function victimCount(shots) {
  return shots.filter(s => s.victimId).length;
}

function nearestTickMap(byTick, tick) {
  for (let d = 0; d <= 4; d++) {
    for (const t of (d === 0 ? [tick] : [tick - d, tick + d])) {
      const m = byTick.get(t);
      if (m && m.size) return m;
    }
  }
  return new Map();
}
