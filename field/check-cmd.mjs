#!/usr/bin/env node
// Guard the .cmd launchers in this repo.
//
// Two failure modes that actually happened here, both silent:
//  1. UTF-8 Chinese inside a .cmd. cmd.exe decodes the file in the OEM codepage,
//     so the bytes come out as garbage - and garbage bytes can decode to a caret,
//     which then splices the FOLLOWING line into this one. File names of scripts
//     are safe; file *contents* are not.
//  2. A bare arrow in an echo line. In cmd, ">" is a redirection operator, so
//     "echo ratio 1.0 -> 1.25" printed "ratio 1.0 -" and quietly created a file
//     named "1.25" (and "1.875") in the repo. A rem line is fine: cmd never
//     parses redirection in rem.
//
// Usage: node field/check-cmd.mjs [dir]      exit 0 = clean, 1 = findings
// Run it from publish.cmd so a broken launcher cannot ship.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = process.argv[2] || path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

function cmdFiles(dir, depth = 0, out = []) {
  if (depth > 2) return out;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory()) {
      if (['node_modules', 'bin', 'obj', 'app', 'logs', '.git', 'field'].includes(e.name) && depth > 0) continue;
      cmdFiles(path.join(dir, e.name), depth + 1, out);
    } else if (/\.cmd$/i.test(e.name)) out.push(path.join(dir, e.name));
  }
  return out;
}

const findings = [];
const files = cmdFiles(ROOT);

for (const f of files) {
  const buf = fs.readFileSync(f);
  const rel = path.relative(ROOT, f);

  // rule 1: no non-ASCII bytes in the content
  const bad = [];
  for (let i = 0; i < buf.length; i++) if (buf[i] > 0x7f) { bad.push(i); if (bad.length > 40) break; }
  if (bad.length) findings.push(`${rel}: ${bad.length} non-ASCII byte(s) at offset ${bad[0]} - cmd.exe reads this file in the OEM codepage; keep the content ASCII and refer to other scripts by description, not by their Chinese file name`);

  const lines = buf.toString('latin1').split(/\r?\n/);
  lines.forEach((raw, i) => {
    const line = raw.trim();
    const n = i + 1;

    // rule 2: redirection metacharacters on an echo line
    const m = /^(?:@)?echo\b(.*)$/i.exec(line);
    if (m) {
      let body = m[1];
      // strip caret-escapes so "^>" does not count, then look for the rest
      body = body.replace(/\^[<>|&]/g, ' ');
      // a quoted region does suppress redirection in cmd; only complain outside quotes
      let outside = '', q = false;
      for (const ch of body) { if (ch === '"') q = !q; else if (!q) outside += ch; }
      if (/[<>|&]/.test(outside))
        findings.push(`${rel}:${n} echo line carries an unescaped redirection character: ${line.slice(0, 80)}`);
    }

    // rule 3: a bare %VAR% that is not a known expansion pattern
    const pct = /%[^%*\r\n]{0,20}%/g;
    for (const hit of line.matchAll(pct)) {
      const name = hit[0].slice(1, -1);
      if (!/^(~?\w+|~dp0|~nx1|PATH|TEMP|ERRORLEVEL|NODE|CSGO|LOCALAPPDATA)$/i.test(name))
        findings.push(`${rel}:${n} suspicious expansion ${hit[0]} - if this is inside an echo it will be eaten when the line is parsed`);
    }
  });

  // rule 4: LF-only line endings are tolerated by cmd, but a file whose LAST line
  // has no newline can lose its final command. Say so instead of letting it bite later.
  if (buf.length && buf[buf.length - 1] !== 0x0a) findings.push(`${rel}: no trailing newline - cmd can drop the last line`);
}

for (const f of findings) console.log('FINDING: ' + f);
console.log(findings.length ? `checked ${files.length} .cmd file(s): ${findings.length} finding(s)`
                            : `checked ${files.length} .cmd file(s): clean`);
process.exit(findings.length ? 1 : 0);
