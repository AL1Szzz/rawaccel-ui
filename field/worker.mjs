// Isolated parser worker. A Rust panic in demoparser2 aborts the process, so
// this must never be the process that writes the user-facing report: the CLI
// spawns it and turns a crash into a diagnostic file.
import fs from 'node:fs';
import { createRequire } from 'node:module';
import { analyzeDemo } from './lib/demo.mjs';

const require = createRequire(import.meta.url);
const jobPath = process.argv[2];
if (!jobPath) { console.error('worker: no job file'); process.exit(2); }

let job;
try { job = JSON.parse(fs.readFileSync(jobPath, 'utf8')); }
catch (e) { console.error('worker: bad job file: ' + e.message); process.exit(2); }

try {
  const dp = require('@laihoe/demoparser2');
  const result = analyzeDemo(dp, job.demo, job.opts || {});
  fs.writeFileSync(job.out, JSON.stringify(result));
  process.exit(0);
} catch (e) {
  try {
    fs.writeFileSync(job.out, JSON.stringify({
      error: e && e.message ? e.message : String(e),
      stack: e && e.stack ? e.stack : '',
    }));
  } catch { /* the CLI will report the missing result */ }
  console.error(e && e.stack ? e.stack : String(e));
  process.exit(1);
}
