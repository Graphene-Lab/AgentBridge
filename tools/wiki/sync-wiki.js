#!/usr/bin/env node
// Syncs the user-facing docs/ tree into a GitHub wiki checkout.
//
// GitHub wikis are FLAT: every page is <PageName>.md at the repo root, linked by
// name. This script mirrors docs/**/*.md into the wiki root, naming each page by the
// file's basename (without .md), and rewrites every relative markdown link so it
// points at the flat page name instead of the docs/ path. INDEX.md becomes Home.
//
// Usage: node sync-wiki.js <docsDir> <wikiDir>
const fs = require('fs');
const path = require('path');

const docsDir = path.resolve(process.argv[2] || 'docs');
const wikiDir = path.resolve(process.argv[3] || '.');
fs.mkdirSync(wikiDir, { recursive: true });

// Recursively collect .md files under docsDir.
function walk(dir) {
  const out = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) out.push(...walk(full));
    else if (entry.isFile() && entry.name.toLowerCase().endsWith('.md')) out.push(full);
  }
  return out;
}

// Page name for a docs file: basename without .md; INDEX -> Home.
function pageName(file) {
  const base = path.basename(file, '.md');
  return base === 'INDEX' ? 'Home' : base;
}

// Rewrite markdown links whose target is a local .md file (optionally with an
// anchor) so they resolve to the flat wiki page name. Leaves http(s), mailto,
// images and non-md links untouched.
function rewriteLinks(content) {
  // [text](target) and [text](target#anchor), target ending in .md
  return content.replace(/\[([^\]]*)\]\(([^)\s]+?\.md)(#[^)]*)?\)/g, (m, text, target, anchor) => {
    if (/^(https?:|mailto:|\/\/)/i.test(target)) return m;
    const base = path.basename(target, '.md');
    const name = base === 'INDEX' ? 'Home' : base;
    return `[${text}](${name}${anchor || ''})`;
  });
}

const files = walk(docsDir);
const pages = [];
for (const file of files) {
  const name = pageName(file);
  const content = rewriteLinks(fs.readFileSync(file, 'utf8'));
  fs.writeFileSync(path.join(wikiDir, name + '.md'), content);
  pages.push(name);
}

// Sidebar: the beginner-guide series first (numeric order), then the reference docs.
const guides = pages.filter(p => /^\d\d-/.test(p)).sort();
const refs = pages.filter(p => p !== 'Home' && !/^\d\d-/.test(p)).sort();
const label = (p) => p.replace(/^\d\d-/, '').replace(/-/g, ' ');
const sidebar =
  `**Guides**\n\n` +
  guides.map(p => `- [${label(p)}](${p})`).join('\n') +
  `\n\n**Reference**\n\n` +
  refs.map(p => `- [${label(p)}](${p})`).join('\n') +
  `\n`;
fs.writeFileSync(path.join(wikiDir, 'Sidebar.md'), sidebar);

console.log(`synced ${pages.length} pages into ${wikiDir}`);
