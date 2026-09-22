const fs = require('fs');
const path = require('path');
const SRC = path.join(__dirname, '..', '_assets', 'pt-html');
for (const id of ['sales-presentation', 'training-deck']) {
  const h = fs.readFileSync(path.join(SRC, id + '.html'), 'utf8');
  const fi = h.indexOf('frame-16-9');
  const seg = h.slice(fi, fi + 400);
  console.log('=== ' + id + ' frame opening context ===');
  console.log(seg.replace(/\s+/g, ' ').slice(0, 360));
  // list direct children tags right after frame open
  const after = h.slice(h.indexOf('>', h.indexOf('frame-16-9')) + 1);
  const kids = [...after.matchAll(/<div\b[^>]*class="([^"]*)"/g)].slice(0, 10);
  console.log('first child classes:', kids.map(k => k[1]).join(' | '));
}
