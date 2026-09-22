const fs = require('fs');
const path = require('path');
const SRC = path.join(__dirname, '..', '_assets', 'pt-html');
const id = 'board-review';
const h = fs.readFileSync(path.join(SRC, id + '.html'), 'utf8');
const slides = h.split(/<[a-z]+[^>]*class="slide(?:\s[^"]*)?"[^>]*>/i).slice(1);
slides.forEach((s, i) => {
  const txt = s.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
  console.log('--- slide ' + (i + 1) + ' ---');
  console.log(txt.slice(0, 220));
});
