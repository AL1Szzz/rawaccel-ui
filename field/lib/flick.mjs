// Flick kinematics from CS2 view angles (demo ticks).
// The tick-quantised view is the highest-resolution game-side truth we have:
// 64 tick = 15.6 ms bins, 128 tick = 7.8 ms. It cannot see 2 ms packet jitter.

import {
  angleDeg, countsPerMs, median, medianSmooth, projectOnAim, quantile, RAD,
  viewFromYawPitch, yawDiff,
} from './angles.mjs';

const isNum = (v) => typeof v === 'number' && Number.isFinite(v);

/**
 * rows: [{tick, yaw, pitch, ...}] for ONE player, any order.
 * Returns an array with timeMs, view, speed (deg/s). speed[i] belongs to the
 * step (i-1 -> i), so speed[0] is 0.
 * anglesAreRadians exists only for a parser that is proven to return radians;
 * a per-value auto-detect would mistake a genuine 3-degree yaw for 3 radians.
 */
export function buildViewSeries(rows, tickRate = 64, pitchDownPositive = true, anglesAreRadians = false) {
  const clean = rows
    .filter(r => r && isNum(r.tick) && isNum(r.yaw) && isNum(r.pitch))
    .sort((a, b) => a.tick - b.tick);
  if (clean.length === 0) return [];
  const t0 = clean[0].tick;
  const tr = tickRate > 1 ? tickRate : 64;
  const series = clean.map(r => ({
    tick: r.tick,
    timeMs: (r.tick - t0) / tr * 1000,
    yaw: anglesAreRadians ? r.yaw * RAD : r.yaw,
    pitch: anglesAreRadians ? r.pitch * RAD : r.pitch,
    sample: r,
  }));
  for (const s of series) s.view = viewFromYawPitch(s.yaw, s.pitch, pitchDownPositive);

  const raw = new Array(series.length).fill(0);
  const rawYaw = new Array(series.length).fill(0);
  const rawPitch = new Array(series.length).fill(0);
  for (let i = 1; i < series.length; i++) {
    const dt = series[i].timeMs - series[i - 1].timeMs;
    if (!(dt > 0)) continue;
    raw[i] = angleDeg(series[i - 1].view, series[i].view) / dt * 1000;
    rawYaw[i] = Math.abs(yawDiff(series[i].yaw, series[i - 1].yaw)) / dt * 1000;
    rawPitch[i] = Math.abs(series[i].pitch - series[i - 1].pitch) / dt * 1000;
  }
  const sm = medianSmooth(raw, 3);
  const smYaw = medianSmooth(rawYaw, 3);
  const smPitch = medianSmooth(rawPitch, 3);
  for (let i = 0; i < series.length; i++) {
    series[i].speed = sm[i];
    series[i].yawSpeed = smYaw[i];
    series[i].pitchSpeed = smPitch[i];
  }
  return series;
}

/** Index of the sample nearest to a tick (series is sorted). */
export function indexAtTick(series, tick) {
  if (series.length === 0) return -1;
  let lo = 0, hi = series.length - 1;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (series[mid].tick < tick) lo = mid + 1; else hi = mid;
  }
  if (lo > 0 && Math.abs(series[lo - 1].tick - tick) <= Math.abs(series[lo].tick - tick)) return lo - 1;
  return lo;
}

function countReversals(perp, minLeg = 0.35) {
  let reversals = 0;
  let extreme = perp[0], trend = 0;
  for (let i = 1; i < perp.length; i++) {
    const v = perp[i];
    if (trend === 0) {
      if (Math.abs(v - extreme) < minLeg) continue;
      trend = v > extreme ? 1 : -1;
      extreme = v;
      continue;
    }
    if (trend > 0) {
      if (v > extreme) extreme = v;
      else if (extreme - v >= minLeg) { reversals++; trend = -1; extreme = v; }
    } else {
      if (v < extreme) extreme = v;
      else if (v - extreme >= minLeg) { reversals++; trend = 1; extreme = v; }
    }
  }
  return reversals;
}

/**
 * The flick that ends at the shot at shotIdx.
 * - peak: smoothed view speed before the shot
 * - overshoot: how far past the SHOT direction the view travelled first
 * - reversals / arcRatio: the path shape the endpoint cannot see
 * Returns null when there is not enough movement to call it a flick.
 */
export function analyzeFlickAt(series, shotIdx, opts = {}) {
  const windowMs = opts.windowMs ?? 800;
  const minPeak = opts.minPeakDegPerSec ?? 20;
  if (!series || shotIdx < 1 || shotIdx >= series.length) return null;

  const shotTime = series[shotIdx].timeMs;
  let start = shotIdx;
  while (start > 0 && shotTime - series[start - 1].timeMs <= windowMs) start--;

  let peakIdx = start;
  let peakYaw = 0, peakPitch = 0;
  for (let i = start; i <= shotIdx; i++) {
    if (series[i].speed > series[peakIdx].speed) peakIdx = i;
    if (series[i].yawSpeed > peakYaw) peakYaw = series[i].yawSpeed;
    if (series[i].pitchSpeed > peakPitch) peakPitch = series[i].pitchSpeed;
  }
  const peak = series[peakIdx].speed;
  if (!(peak >= minPeak)) return null;

  let onset = start;
  const low = peak * 0.15;
  for (let i = peakIdx; i > start; i--) {
    if (series[i].speed >= low) onset = i; else break;
  }
  if (onset >= peakIdx) onset = Math.max(start, peakIdx - 1);

  const onsetDir = series[onset].view;
  const shotDir = series[shotIdx].view;
  const perp = [];
  const alongValues = [];
  let maxAlong = -Infinity;
  for (let i = onset; i <= shotIdx; i++) {
    const p = projectOnAim(onsetDir, shotDir, series[i].view);
    perp.push(p.perp);
    alongValues.push(p.along);
    if (p.along > maxAlong) maxAlong = p.along;
  }
  const finalAlong = projectOnAim(onsetDir, shotDir, shotDir).along;
  const overshoot = Math.max(0, maxAlong - finalAlong);

  let lateralPath = 0;
  for (let i = 1; i < perp.length; i++) lateralPath += Math.abs(perp[i] - perp[i - 1]);
  // A straight overshoot-and-return has no lateral motion at all: the reversal
  // is on the ALONG axis. Counting only perpendicular legs silently reported
  // such a flick as "no correction" in the first version.
  const reversals = countReversals(perp) + countReversals(alongValues);

  let pathDeg = 0;
  for (let i = onset + 1; i <= shotIdx; i++)
    pathDeg += angleDeg(series[i - 1].view, series[i].view);

  const dyaw = Math.abs(yawDiff(series[shotIdx].yaw, series[onset].yaw));
  const dpitch = Math.abs(series[shotIdx].pitch - series[onset].pitch);
  const direction = dyaw < 1 && dpitch < 1 ? 'still'
    : dyaw >= 2 * dpitch ? 'X'
    : dpitch >= 2 * dyaw ? 'Y'
    : 'diag';

  return {
    onsetTimeMs: series[onset].timeMs,
    shotTimeMs: shotTime,
    durationMs: shotTime - series[onset].timeMs,
    peakDegPerSec: peak,
    peakCms: countsPerMs(peak, opts.sensitivity ?? 1.5),
    peakYawDegPerSec: peakYaw,
    peakPitchDegPerSec: peakPitch,
    peakYawCms: countsPerMs(peakYaw, opts.sensitivity ?? 1.5),
    peakPitchCms: countsPerMs(peakPitch, opts.sensitivity ?? 1.5),
    overshootDeg: overshoot,
    reversals,
    corrected: reversals >= 1 && overshoot > 0.3,
    lateralPathDeg: lateralPath,
    arcRatio: lateralPath / Math.max(finalAlong, 1),
    pathDeg,
    finalAlongDeg: finalAlong,
    direction,
    samples: shotIdx - onset + 1,
  };
}

/** Aggregate flick metrics for a report. */
export function summarizeFlicks(flicks, opts = {}) {
  const xThreshold = opts.xThreshold ?? 3.0;
  const yThreshold = opts.yThreshold ?? 2.5;
  const valid = flicks.filter(Boolean);
  const peaks = valid.map(f => f.peakCms).filter(Number.isFinite);
  const peeksYaw = valid.map(f => f.peakYawCms).filter(Number.isFinite);
  const peeksPitch = valid.map(f => f.peakPitchCms).filter(Number.isFinite);
  const byDir = (d) => valid.filter(f => f.direction === d);
  const dirStats = {};
  for (const d of ['X', 'Y', 'diag']) {
    const g = byDir(d);
    dirStats[d] = {
      n: g.length,
      peakMedian: median(g.map(f => f.peakCms)),
      yawMedian: median(g.map(f => f.peakYawCms)),
      pitchMedian: median(g.map(f => f.peakPitchCms)),
      overshootMedian: median(g.map(f => f.overshootDeg)),
    };
  }
  return {
    n: valid.length,
    peakMedianCms: median(peaks),
    peakP95Cms: quantile(peaks, 0.95),
    peakYawP50Cms: median(peeksYaw),
    peakPitchP50Cms: median(peeksPitch),
    // The driver applies the X curve to the horizontal component and the Y
    // curve to the vertical component. A diagonal/3D peak cannot answer
    // "did the Y curve engage": a fast horizontal flick would count for Y too.
    aboveXThresholdPct: peeksYaw.length ? 100 * peeksYaw.filter(v => v >= xThreshold).length / peeksYaw.length : NaN,
    aboveYThresholdPct: peeksPitch.length ? 100 * peeksPitch.filter(v => v >= yThreshold).length / peeksPitch.length : NaN,
    overshootMedianDeg: median(valid.map(f => f.overshootDeg)),
    overshootMaxDeg: valid.length ? Math.max(...valid.map(f => f.overshootDeg)) : NaN,
    reversalsMean: valid.length ? valid.reduce((s, f) => s + f.reversals, 0) / valid.length : NaN,
    correctedPct: valid.length ? 100 * valid.filter(f => f.corrected).length / valid.length : NaN,
    arcRatioMedian: median(valid.map(f => f.arcRatio)),
    durationMedianMs: median(valid.map(f => f.durationMs)),
    direction: dirStats,
    xThreshold,
    yThreshold,
  };
}
