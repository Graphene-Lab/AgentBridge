const fs = require('fs');
const path = require('path');
const { PNG } = require('pngjs');
const EX = path.join(__dirname, '..', '04-presentations');
const files = [
  ['pitch-deck', 'pitch-deck/img/pitch-deck.png'],
  ['sales-presentation', 'sales-presentation/img/sales-presentation.png'],
  ['training-deck', 'training-deck/img/training-deck.png'],
  ['board-review', 'board-review/img/board-review.png'],
];
for (const [id, rel] of files) {
  const png = PNG.sync.read(fs.readFileSync(path.join(EX, rel)));
  const { width: W, height: H, data } = png;
  let min = 255, max = 0, sum = 0, n = 0;
  const colors = new Set();
  for (let y = 0; y < H; y += Math.floor(H / 40)) {
    for (let x = 0; x < W; x += Math.floor(W / 60)) {
      const i = (y * W + x) * 4;
      const lum = 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
      min = Math.min(min, lum); max = Math.max(max, lum); sum += lum; n++;
      colors.add((data[i] >> 4) + ',' + (data[i + 1] >> 4) + ',' + (data[i + 2] >> 4));
    }
  }
  console.log(id.padEnd(20), 'WxH=' + W + 'x' + H, 'lum min/max/mean=' + min.toFixed(0) + '/' + max.toFixed(0) + '/' + (sum / n).toFixed(0), 'distinct~' + colors.size);
}
