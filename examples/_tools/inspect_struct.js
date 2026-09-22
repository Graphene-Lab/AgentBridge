const fs = require('fs');
const path = require('path');
const SRC = path.join(__dirname, '..', '_assets', 'pt-html');
const h = fs.readFileSync(path.join(SRC, 'pitch-deck.html'), 'utf8');
// find the frame and the slide tags with their opening tag text
const frameIdx = h.indexOf('frame-16-9');
console.log('frame-16-9 at', frameIdx);
const tags = [...h.matchAll(/<(div|section|article)\b[^>]*class="slide(?:\s[^"]*)?"[^>]*>/gi)];
console.log('slide tags:', tags.length);
tags.forEach((t, i) => console.log('  ' + (i + 1) + ': <' + t[1] + '> ' + t[0].slice(0, 70)));
// show context around first slide to see parent
const first = tags[0];
console.log('--- 120 chars before first slide ---');
console.log(h.slice(first.index - 120, first.index + 40));
