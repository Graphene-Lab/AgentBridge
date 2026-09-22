const fs = require('fs');
const path = require('path');
const SRC = path.join(__dirname, '..', '_assets', 'pt-html');
for (const id of ['pitch-deck', 'sales-presentation', 'training-deck', 'board-review']) {
  const h = fs.readFileSync(path.join(SRC, id + '.html'), 'utf8');
  const slides = h.split(/<[a-z]+[^>]*class="slide(?:\s[^"]*)?"[^>]*>/i).slice(1);
  console.log('=== ' + id + ' (' + slides.length + ' slides) ===');
  slides.forEach((s, i) => {
    const t = (s.match(/<(h1|h2|h3)[^>]*>([^<]+)</) || [])[2] || '(no title)';
    const hasCards = /class="[^"]*(card|stat|grid|bar|table|timeline|org)/.test(s);
    const len = s.replace(/<[^>]+>/g, '').trim().length;
    console.log('  ' + (i + 1) + ': ' + t.slice(0, 42) + ' | cards=' + (hasCards ? 'Y' : 'n') + ' | text=' + len);
  });
}
