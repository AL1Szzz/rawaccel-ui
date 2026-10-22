// Offline assertions for the field-analysis math. No demo, no game, no input.
// Run: node field/test.mjs
import {
  angleDeg, countsPerMs, projectOnAim, viewFromYawPitch,
} from './lib/angles.mjs';
import { analyzeFlickAt, buildViewSeries, summarizeFlicks } from './lib/flick.mjs';

let checks = 0, failed = 0;
function check(ok, label, detail = '') {
  checks++;
  if (!ok) failed++;
  console.log(`[${ok ? 'PASS' : 'FAIL'}] ${label}${detail ? '  ' + detail : ''}`);
}

// ---- angle layer ----
{
  const a = viewFromYawPitch(0, 0), b = viewFromYawPitch(10, 0);
  check(Math.abs(angleDeg(a, b) - 10) < 1e-6, 'yaw 0 -> 10 degrees is 10 degrees',
    angleDeg(a, b).toFixed(6));
  const c = viewFromYawPitch(0, 10, true), d = viewFromYawPitch(0, 0, true);
  check(Math.abs(angleDeg(c, d) - 10) < 1e-6, 'pitch sign does not change the angle',
    angleDeg(c, d).toFixed(6));
  check(Math.abs(countsPerMs(100, 1.5) - 3.0303) < 0.01,
    '100 deg/s at sens 1.5 is 3.03 counts/ms', countsPerMs(100, 1.5).toFixed(4));
}
{
  const start = viewFromYawPitch(0, 0), end = viewFromYawPitch(30, 0);
  const mid = viewFromYawPitch(20, 0);
  const p = projectOnAim(start, end, mid);
  check(Math.abs(p.along - 20) < 0.05 && Math.abs(p.perp) < 0.05,
    'projection of a 20-degree point on a 0->30 aim line is (20, 0)',
    `along ${p.along.toFixed(2)}, perp ${p.perp.toFixed(2)}`);
  const off = viewFromYawPitch(20, 5);
  const q = projectOnAim(start, end, off);
  check(Math.abs(q.along - 20) < 0.3 && Math.abs(Math.abs(q.perp) - 5.32) < 0.4,
    'an off-line point keeps its lateral offset (spherical, ~5.3 deg)',
    `along ${q.along.toFixed(2)}, perp ${q.perp.toFixed(2)}`);
}

// ---- synthetic tick series ----
function synth(kind, tickRate = 64) {
  const rows = [];
  let tick = 1000;
  const push = (yaw, pitch = 0) => rows.push({ tick: tick++, yaw, pitch });
  for (let i = 0; i < Math.round(0.4 * tickRate); i++) push(0);
  if (kind === 'clean' || kind === 'over') {
    const n1 = Math.round((kind === 'clean' ? 0.30 : 0.25) * tickRate);
    const reach = kind === 'clean' ? 30 : 35;
    for (let i = 1; i <= n1; i++) {
      const u = i / n1, s = u * u * (3 - 2 * u);
      push(reach * s);
    }
    if (kind === 'over') {
      const n2 = Math.round(0.12 * tickRate);
      for (let i = 1; i <= n2; i++) push(35 - 5 * (i / n2));
    }
    for (let i = 0; i < Math.round(0.12 * tickRate); i++) push(kind === 'clean' ? 30 : 30);
  } else if (kind === 'vertical') {
    const n = Math.round(0.30 * tickRate);
    for (let i = 1; i <= n; i++) {
      const u = i / n, s = u * u * (3 - 2 * u);
      push(0, -20 * s);
    }
    for (let i = 0; i < Math.round(0.12 * tickRate); i++) push(0, -20);
  }
  return rows;
}

function analyze(kind) {
  const rows = synth(kind);
  const series = buildViewSeries(rows, 64);
  const idx = series.length - 1;
  return { series, idx, f: analyzeFlickAt(series, idx, { sensitivity: 1.5 }) };
}

{
  const { f } = analyze('clean');
  check(!!f, 'clean flick is detected');
  if (f) {
    check(f.peakCms > 2 && f.peakCms < 8, 'clean flick peak is in a plausible c/ms band',
      f.peakCms.toFixed(2));
    check(f.overshootDeg < 1.5, 'clean flick does not overshoot the shot direction',
      f.overshootDeg.toFixed(2));
    check(f.corrected === false, 'clean flick is not marked corrected');
    check(f.direction === 'X', 'clean flick is classified horizontal (X)', f.direction);
    check(f.durationMs > 80, 'clean flick has a real duration', `${f.durationMs.toFixed(0)}ms`);
  }
}
{
  const { f } = analyze('over');
  check(!!f, 'overshooting flick is detected');
  if (f) {
    check(f.overshootDeg > 2.5, 'overshoot is measured in degrees past the shot direction',
      f.overshootDeg.toFixed(2));
    check(f.corrected === true, 'straight overshoot-and-return is marked corrected (along-axis reversal)');
    check(f.reversals >= 1, 'the return leg is counted as a reversal', String(f.reversals));
  }
}
{
  const { f } = analyze('vertical');
  check(!!f, 'vertical flick is detected');
  if (f) {
    check(f.direction === 'Y', 'vertical flick is classified Y', f.direction);
    check(f.overshootDeg < 1.5, 'vertical clean flick does not overshoot', f.overshootDeg.toFixed(2));
    const vs = summarizeFlicks([f], { xThreshold: 3.0, yThreshold: 2.5 });
    check(vs.aboveYThresholdPct === 100 && vs.aboveXThresholdPct === 0,
      'a vertical flick engages the Y component and not the X component',
      `X ${vs.aboveXThresholdPct}% Y ${vs.aboveYThresholdPct}%`);
  }
}
{
  const { series, f } = analyze('clean');
  const sum = summarizeFlicks([f], { xThreshold: 3.0, yThreshold: 2.5 });
  check(sum.n === 1 && sum.direction.X.n === 1, 'summary keeps the flick in the X bucket');
  check(sum.aboveXThresholdPct === 100 || sum.aboveXThresholdPct === 0,
    'threshold share is 0 or 100 for one sample', `${sum.aboveXThresholdPct}`);
  check(sum.aboveXThresholdPct === 100 && sum.aboveYThresholdPct === 0,
    'a horizontal flick engages the X component and not the Y component',
    `X ${sum.aboveXThresholdPct}% Y ${sum.aboveYThresholdPct}%`);
  check(sum.peakPitchP50Cms < 1, 'horizontal flick has a near-zero vertical component',
    `${sum.peakPitchP50Cms}`);
  check(series.length > 40, 'series has one row per demo tick', String(series.length));
}

// ---- spray-burst analysis (what decides the aim-map A/B/A) ----
{
  const fs = await import('node:fs');
  const os = await import('node:os');
  const path = await import('node:path');
  const { readShots, bursts, summarize } = await import('./spray-bursts.mjs');

  const HEAD = 'tick,time_ms,weapon,distance_m,distance_bucket,victim_id,victim_name,hit,hitgroup,damage,' +
    'aim_error_deg,peak_cms,peak_deg_s,overshoot_deg,reversals,corrected,direction,duration_ms,arc_ratio,hp_after,kill';
  const row = (tick, hit, group, dmg, hp, kill, name = 'pov') => {
    const c = new Array(21).fill('');
    c[0] = String(tick); c[2] = 'weapon_ak47'; c[4] = 'unknown'; c[5] = 'pov'; c[6] = name;
    c[7] = hit ? '1' : '0'; c[8] = hit ? group : ''; c[9] = hit ? String(dmg) : '0';
    c[19] = hit ? String(hp) : ''; c[20] = kill ? '1' : '0';
    return c.join(',');
  };

  const lines = [HEAD];
  // magazine 1: two bodies (4 bullets, then 5), then 21 shots into nothing
  for (let i = 0; i < 30; i++) {
    const tick = 100 + i * 7;
    if (i < 4) lines.push(row(tick, true, 'chest', 26, 74 - 26 * i, i === 3, i === 0 ? '"Doe, John"' : 'pov'));
    else if (i < 9) lines.push(row(tick, true, 'chest', 20, 80 - 20 * (i - 4), i === 8));
    else lines.push(row(tick, false, '', 0, '', false));
  }
  // magazine 2: a wall spray, 20 shots, zero hits (starts after a real reload gap)
  for (let i = 0; i < 20; i++) lines.push(row(500 + i * 7, false, '', 0, '', false));
  // a short tap burst: must not count as a spray
  for (let i = 0; i < 5; i++) lines.push(row(900 + i * 7, true, 'head', 100, 0, true));

  const file = path.join(os.tmpdir(), `raui-burst-test-${Date.now()}.csv`);
  fs.writeFileSync(file, lines.join('\n') + '\n');
  try {
    const { rows, exactKills } = readShots(file);
    check(rows.length === 55 && exactKills, 'a quoted comma inside a victim name does not shift the columns',
      `${rows.length} rows, exactKills=${exactKills}`);
    const bs = bursts(rows);
    check(bs.length === 3 && bs[0].length === 30 && bs[1].length === 20 && bs[2].length === 5,
      `the tick gap splits three bursts of 30/20/5 (${bs.map(b => b.length).join('/')})`);

    const s = summarize(rows, exactKills);
    check(s.longBursts === 2 && s.onTargetLong === 1 && s.wallLong === 1,
      'the wall magazine is counted separately from the ones that hit bodies',
      `long ${s.longBursts}, bodies ${s.onTargetLong}, wall ${s.wallLong}`);
    check(s.killsInSprays === 2 && Math.abs(s.killsPerSpray - 2) < 0.01,
      `kill flags segment the magazine into bodies (${s.killsInSprays} in ${s.onTargetLong} spray)`);
    check(s.medianBulletsPerBody === 5 && Math.abs(s.meanBulletsPerBody - 4.5) < 0.01,
      `bullets per body come from the segments, not from the whole mag (${s.medianBulletsPerBody} / ${s.meanBulletsPerBody})`);
    check(Math.abs(s.rate1 - 90) < 0.01 && s.rate3 === 0,
      `depth buckets: 9 of the first 10 hit, none of 21-30 (${s.rate1} / ${s.rate3})`);
    check(s.torso1 === 100 && s.head1 === 0 && s.limb1 === 0,
      'the depth hitgroup share is a share OF THE HITS in that slice');
    check(Math.abs(s.hitPct - 100 * 14 / 55) < 0.01,
      `aggregate hit% counts every gun shot, wall magazine included (${s.hitPct.toFixed(1)}%)`);

    const inferred = summarize(rows, false);
    check(inferred.killsInSprays === 2,
      'the pre-kill-column fallback (spend hits off 100 HP) agrees with the kill flags here',
      `${inferred.killsInSprays}`);
  } finally {
    try { fs.unlinkSync(file); } catch { }
}
}

console.log('');
console.log(failed === 0 ? `ALL ${checks} FIELD-MATH CHECKS PASSED` : `${failed}/${checks} CHECKS FAILED`);
process.exit(failed === 0 ? 0 : 1);
