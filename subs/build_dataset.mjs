// Builds RedMoonCappuccino/Resources/submarine_routes.json from its two sources:
//
//   subs/FFXIV Submersible Route Planner.html   loot pools and yields up to rank 130
//   resources/subs_data/stats_breakpoints.xlsx  rank table and breakpoints to 145,
//                                               plus the Northern Empty map
//
// Run it after either source changes:
//
//   node subs/build_dataset.mjs
//
// The output is regenerated from scratch every time, so running it twice gives
// the same file. No dependencies — the xlsx is unzipped with zlib.

import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const htmlPath = path.join(root, 'subs', 'FFXIV Submersible Route Planner.html');
const xlsxPath = path.join(root, 'resources', 'subs_data', 'stats_breakpoints.xlsx');
const outPath  = path.join(root, 'RedMoonCappuccino', 'Resources', 'submarine_routes.json');

// Yields for the newest map have not been measured yet. Every drop there is
// flagged `est` and stands in with one unit at a 50% chance, so the planner can
// route the map while the UI keeps saying the numbers are placeholders.
const PLACEHOLDER_CHANCE = 0.5;
const PLACEHOLDER_YIELD  = 1;

const STAT_NAMES = ['surveillance', 'retrieval', 'speed', 'range', 'favor'];

// Known errors in the source spreadsheet, applied on top of what it says. Every
// correction is printed on each run so none of them rots unnoticed; delete an
// entry once the spreadsheet itself carries the right value.
const RANK_ERRATA = {
  // The sheet reports Speed 90 at rank 144, between 98 at 143 and 100 at 145.
  // Confirmed by the community as a reporting error — the curve does not dip.
  144: { speed: 99 },
};

// Tier weights are not published per sector either. These are the average of
// the twenty South Indigo Deep sectors — the closest comparable endgame map —
// so the split across loot tiers stays realistic.
const PLACEHOLDER_BLOCKS = {
  lo: [[1, 0, 0], [1, 0, 0], 0.825],
  md: [[0.8385, 0.1615, 0], [0.8239, 0.1761, 0], 0.825],
  hi: [[0.6909, 0.2461, 0.063], [0.7219, 0.2164, 0.0617], 0.8255],
};

// ── minimal zip + xlsx reading ───────────────────────────────────────────────

function readZip(file) {
  const buf = fs.readFileSync(file);

  // End of central directory, scanned backwards past any comment.
  let eocd = buf.length - 22;
  while (eocd >= 0 && buf.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  if (eocd < 0) throw new Error(`${file} is not a zip archive`);

  const count  = buf.readUInt16LE(eocd + 10);
  let offset   = buf.readUInt32LE(eocd + 16);
  const files  = new Map();

  for (let i = 0; i < count; i++) {
    if (buf.readUInt32LE(offset) !== 0x02014b50) throw new Error('bad central directory entry');

    const method     = buf.readUInt16LE(offset + 10);
    const compressed = buf.readUInt32LE(offset + 20);
    const nameLen    = buf.readUInt16LE(offset + 28);
    const extraLen   = buf.readUInt16LE(offset + 30);
    const commentLen = buf.readUInt16LE(offset + 32);
    const localAt    = buf.readUInt32LE(offset + 42);
    const name       = buf.toString('utf8', offset + 46, offset + 46 + nameLen);

    const localNameLen  = buf.readUInt16LE(localAt + 26);
    const localExtraLen = buf.readUInt16LE(localAt + 28);
    const dataAt        = localAt + 30 + localNameLen + localExtraLen;
    const raw           = buf.subarray(dataAt, dataAt + compressed);

    files.set(name, method === 0 ? raw : zlib.inflateRawSync(raw));
    offset += 46 + nameLen + extraLen + commentLen;
  }

  return files;
}

const ENTITIES = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&apos;': "'" };
const decode = s => s.replace(/&(amp|lt|gt|quot|apos);/g, m => ENTITIES[m]);

/** Sheet XML to { rowNumber: { columnLetter: value } }. */
function readSheet(xml) {
  const rows = {};
  const rowRe = /<row[^>]*r="(\d+)"[^>]*>([\s\S]*?)<\/row>/g;
  let row;

  while ((row = rowRe.exec(xml))) {
    const cells = {};
    const cellRe = /<c r="([A-Z]+)\d+"([^>]*)>([\s\S]*?)<\/c>/g;
    let cell;

    while ((cell = cellRe.exec(row[2]))) {
      const [, column, attributes, body] = cell;
      const match = /t="inlineStr"/.test(attributes)
        ? /<t[^>]*>([\s\S]*?)<\/t>/.exec(body)
        : /<v>([\s\S]*?)<\/v>/.exec(body);
      cells[column] = match ? decode(match[1]).trim() : '';
    }

    rows[+row[1]] = cells;
  }

  return rows;
}

// ── item naming ──────────────────────────────────────────────────────────────

// The loot sheet is hand-written: casing drifts between rows and orchestrion
// rolls are abbreviated three different ways. Fold those together so one item
// does not end up as three materials.
function tidy(name) {
  return name
    .replace(/\s+/g, ' ')
    .replace(/\bOrch\.?\s*rolls?\b/gi, 'Orchestrion Roll')
    .replace(/\borch\.\B|\borch\.$/gi, 'Orchestrion Roll')
    .trim();
}

// Capitalises word starts but leaves letters after an apostrophe alone, so
// "br'aax hides" becomes "Br'aax Hides" rather than "Br'Aax Hides".
const titleCase = s => s.replace(/(?<!['\w])[a-z]/g, c => c.toUpperCase());
const capitals  = s => (s.match(/[A-Z]/g) || []).length;

// ── read the sources ─────────────────────────────────────────────────────────

const htmlLine = fs.readFileSync(htmlPath, 'utf8')
  .split('\n')
  .find(line => line.startsWith('const DATA = '));
if (!htmlLine) throw new Error('DATA blob not found in the planner page');

const data = JSON.parse(htmlLine.replace(/^const DATA = /, '').replace(/;\s*$/, ''));

const sheets = readZip(xlsxPath);
const stats  = readSheet(sheets.get('xl/worksheets/sheet1.xml').toString('utf8'));
const loot   = readSheet(sheets.get('xl/worksheets/sheet2.xml').toString('utf8'));

// ── 1. rank table ────────────────────────────────────────────────────────────

// "Submarines base stats": rank in column E, the five stats in F..J.
let rankRows = 0;
for (const key of Object.keys(stats)) {
  const cells = stats[key];
  const rank  = Number(cells.E);
  if (!Number.isInteger(rank) || rank < 1 || rank > 200) continue;
  if (['F', 'G', 'H', 'I', 'J'].some(c => cells[c] === undefined || cells[c] === '')) continue;

  data.rank[String(rank)] = ['F', 'G', 'H', 'I', 'J'].map(c => Number(cells[c]));
  rankRows++;
}

const errata = [];
for (const [rank, fixes] of Object.entries(RANK_ERRATA)) {
  const row = data.rank[rank];
  if (!row) continue;

  for (const [stat, value] of Object.entries(fixes)) {
    const index = STAT_NAMES.indexOf(stat);
    if (index < 0) throw new Error(`unknown stat "${stat}" in the rank errata`);
    if (row[index] === value) continue;

    errata.push(`rank ${rank} ${stat} ${row[index]} -> ${value}`);
    row[index] = value;
  }
}

// A stat that goes backwards as rank goes up is almost always a typo in the
// source, so surface it here instead of shipping it.
const ranks = Object.keys(data.rank).map(Number).sort((a, b) => a - b);
const dips = [];
for (let i = 1; i < ranks.length; i++) {
  const previous = data.rank[String(ranks[i - 1])];
  const current  = data.rank[String(ranks[i])];
  for (let k = 0; k < 5; k++)
    if (current[k] < previous[k])
      dips.push(`rank ${ranks[i]} ${STAT_NAMES[k]} ${previous[k]} -> ${current[k]}`);
}

// ── 2. breakpoints ───────────────────────────────────────────────────────────

// Sector blocks: a map name on its own row, a header row, then "L : Name" rows
// with rank in F and the five breakpoints in G..K.
const blocks = [];
let current = null;

for (const key of Object.keys(stats).map(Number).sort((a, b) => a - b)) {
  const cells = stats[key];
  const label = cells.E ?? '';

  if (label && cells.F === undefined) {
    current = label === 'Submarines base stats' ? null : { map: label, sectors: [] };
    if (current) blocks.push(current);
    continue;
  }

  if (!current || cells.F === 'rank' || !label.includes(' : ')) continue;

  const [letter, ...rest] = label.split(' : ');
  current.sectors.push({
    letter: letter.trim(),
    name:   rest.join(' : ').trim(),
    rank:   Number(cells.F),
    bp:     ['G', 'H', 'I', 'J', 'K'].map(c => Number(cells[c])),
  });
}

const mapAliases = { 'The South Indigo Deep': 'South Indigo Deep' };
let updated = 0;
let newMap  = null;

for (const block of blocks) {
  const name  = mapAliases[block.map] ?? block.map;
  const index = data.maps.indexOf(name);

  if (index < 0) {
    newMap = block;
    continue;
  }

  for (const sector of block.sectors) {
    const target = data.sectors.find(s => s.m === index && s.L === sector.letter);
    if (!target) throw new Error(`${name} has no sector ${sector.letter}`);
    target.bp = sector.bp;
    target.rk = sector.rank;
    updated++;
  }
}

// ── 3. the new map ───────────────────────────────────────────────────────────

const names   = data.names;
const byLower = new Map(names.map((n, i) => [n.toLowerCase(), i]));
const variants = new Map();

/** Interns a material name, folding case variants onto one entry. */
function materialIndex(raw) {
  let name = tidy(raw);

  // Several loot rows are typed entirely in lower case. Materials already known
  // from the base dataset keep their proper spelling because the lookup below
  // is case-insensitive; genuinely new ones get title cased here.
  if (capitals(name) === 0) name = titleCase(name);

  const key = name.toLowerCase();

  if (!byLower.has(key)) {
    byLower.set(key, names.push(name) - 1);
    variants.set(key, name);
    return byLower.get(key);
  }

  // Keep the best-capitalised spelling seen for a name this build introduced.
  const index = byLower.get(key);
  if (variants.has(key) && capitals(name) > capitals(variants.get(key))) {
    variants.set(key, name);
    names[index] = name;
  }

  return index;
}

const placeholderDrop = index => ({
  i: index,
  p: PLACEHOLDER_CHANCE,
  y: [PLACEHOLDER_YIELD, PLACEHOLDER_YIELD, PLACEHOLDER_YIELD],
  r: [PLACEHOLDER_YIELD, PLACEHOLDER_YIELD, PLACEHOLDER_YIELD, PLACEHOLDER_YIELD, PLACEHOLDER_YIELD, PLACEHOLDER_YIELD],
  N: 0,
  est: true,
});

let addedSectors = 0;

if (newMap) {
  const mapIndex = data.maps.push(newMap.map) - 1;

  // Loot sheet: sector in A, raw materials in D, crafted in E, the rare in F.
  const lootByLetter = new Map();
  for (const key of Object.keys(loot)) {
    const cells = loot[key];
    const label = cells.A ?? '';
    if (!label.includes(' : ') || cells.D === undefined) continue;
    lootByLetter.set(label.split(' : ')[0].trim(), cells);
  }

  for (const sector of newMap.sectors) {
    const cells = lootByLetter.get(sector.letter);

    // Raw materials are the common tier, crafted the middle one, and the single
    // rare sits in the top tier — the same shape as every other sector.
    const tiers = !cells ? [[], [], []] : [
      (cells.D ?? '').split(',').map(s => s.trim()).filter(Boolean),
      (cells.E ?? '').split(',').map(s => s.trim()).filter(Boolean),
      (cells.F ?? '').trim() ? [cells.F.trim()] : [],
    ];

    data.sectors.push({
      n:  sector.name,
      L:  sector.letter,
      m:  mapIndex,
      rk: sector.rank,
      bp: sector.bp,
      hi: PLACEHOLDER_BLOCKS.hi,
      md: PLACEHOLDER_BLOCKS.md,
      lo: PLACEHOLDER_BLOCKS.lo,
      it: tiers.map(tier => tier.map(name => placeholderDrop(materialIndex(name)))),
    });

    addedSectors++;
  }
}

// ── write ────────────────────────────────────────────────────────────────────

fs.writeFileSync(outPath, JSON.stringify(data));

const estimated = data.sectors.filter(s => s.it.some(t => t.some(d => d.est))).length;
console.log(`maps       ${data.maps.length} (${data.maps.join(', ')})`);
console.log(`sectors    ${data.sectors.length} (+${addedSectors} new, ${updated} breakpoints refreshed, ${estimated} on placeholder yields)`);
console.log(`materials  ${data.names.length}`);
console.log(`ranks      ${Object.keys(data.rank).length} (${rankRows} from the spreadsheet)`);
for (const fix of errata) console.log(`errata     ${fix}`);
for (const dip of dips)   console.log(`WARNING    stat goes backwards: ${dip}`);
console.log(`written    ${path.relative(root, outPath)}`);
