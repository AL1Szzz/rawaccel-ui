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

console.log('');
console.log(failed === 0 ? `ALL ${checks} FIELD-MATH CHECKS PASSED` : `${failed}/${checks} CHECKS FAILED`);
process.exit(failed === 0 ? 0 : 1);
