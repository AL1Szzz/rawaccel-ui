// Angle / vector helpers for CS2 view-angle analysis.
// CS2 demo angles are in degrees: yaw around +Z, pitch positive = looking down.
// Nothing here assumes a particular pitch sign until a caller says so; the
// report calibrates it against actual hits instead of guessing.

export const DEG = Math.PI / 180;
export const RAD = 180 / Math.PI;

export const clamp = (x, lo, hi) => Math.min(hi, Math.max(lo, x));
export const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

export function cross(a, b) {
  return [
    a[1] * b[2] - a[2] * b[1],
    a[2] * b[0] - a[0] * b[2],
    a[0] * b[1] - a[1] * b[0],
  ];
}

export const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
export const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
export const scale = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
export const length = (a) => Math.sqrt(dot(a, a));

export function norm(a) {
  const n = length(a);
  return n > 1e-12 ? [a[0] / n, a[1] / n, a[2] / n] : [0, 0, 0];
}

/** Angle between two vectors, in degrees. */
export function angleDeg(a, b) {
  const na = norm(a), nb = norm(b);
  return Math.acos(clamp(dot(na, nb), -1, 1)) * RAD;
}

/**
 * View vector from demo yaw/pitch (degrees).
 * Source convention: yaw 0 = +X, yaw 90 = +Y; pitch positive = down.
 */
export function viewFromYawPitch(yawDeg, pitchDeg, pitchDownPositive = true) {
  const y = yawDeg * DEG, p = pitchDeg * DEG;
  const cp = Math.cos(p);
  return [
    cp * Math.cos(y),
    cp * Math.sin(y),
    (pitchDownPositive ? -1 : 1) * Math.sin(p),
  ];
}

/** Shortest signed difference a-b for yaw-like values, in degrees (-180,180]. */
export function yawDiff(a, b) {
  let d = (a - b) % 360;
  if (d > 180) d -= 360;
  if (d <= -180) d += 360;
  return d;
}

/**
 * Project a view vector into the (start -> end) aim frame on the unit sphere.
 * Returns along/perp in degrees, evaluated at `start`. For angles below ~60 deg
 * this is the usual tangent-plane decomposition; it degrades gracefully above.
 */
export function projectOnAim(startDir, endDir, v) {
  const s = norm(startDir), e = norm(endDir);
  const de = dot(e, s);
  let ortho = sub(e, scale(s, de));
  if (length(ortho) < 1e-9) return { along: 0, perp: 0 };
  ortho = norm(ortho);
  const lat = norm(cross(s, ortho));
  const x = dot(v, s);
  if (Math.abs(x) < 1e-9) return { along: 0, perp: 0 };
  return {
    along: Math.atan2(dot(v, ortho), x) * RAD,
    perp: Math.atan2(dot(v, lat), x) * RAD,
  };
}

export function median(values) {
  const a = values.filter(Number.isFinite).sort((x, y) => x - y);
  if (a.length === 0) return NaN;
  const n = a.length;
  return n % 2 ? a[(n - 1) / 2] : (a[n / 2 - 1] + a[n / 2]) / 2;
}

export function quantile(values, q) {
  const a = values.filter(Number.isFinite).sort((x, y) => x - y);
  if (a.length === 0) return NaN;
  const idx = clamp(q, 0, 1) * (a.length - 1);
  const lo = Math.floor(idx), hi = Math.ceil(idx);
  return a[lo] + (a[hi] - a[lo]) * (idx - lo);
}

export function mean(values) {
  const a = values.filter(Number.isFinite);
  return a.length ? a.reduce((s, x) => s + x, 0) / a.length : NaN;
}

export function stddev(values) {
  const a = values.filter(Number.isFinite);
  if (a.length < 2) return NaN;
  const m = mean(a);
  return Math.sqrt(a.reduce((s, x) => s + (x - m) * (x - m), 0) / (a.length - 1));
}

/** Median filter; delivery/tick jitter makes single samples useless. */
export function medianSmooth(values, width = 3) {
  if (values.length < width) return values.slice();
  const half = Math.floor(width / 2);
  const out = new Array(values.length);
  for (let i = 0; i < values.length; i++) {
    const lo = Math.max(0, i - half), hi = Math.min(values.length - 1, i + half);
    out[i] = median(values.slice(lo, hi + 1));
  }
  return out;
}

/**
 * The driver's own unit conversion, at a fixed in-game sensitivity:
 * counts/ms = deg/s / (m_yaw * sens) / 1000.
 * 100 deg/s at sens 1.5 is 3.03 counts/ms, which is exactly the X threshold.
 */
export function countsPerMs(degPerSec, sensitivity = 1.5) {
  if (!(sensitivity > 0)) return NaN;
  return degPerSec / (0.022 * sensitivity) / 1000;
}

/** World units (Source) to metres; a player is ~72 units tall. */
export const UNITS_TO_M = 0.0254;
export const EYE_HEIGHT_UNITS = 64;
