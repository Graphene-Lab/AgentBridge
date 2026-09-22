// Generate QR codes for the AgentBridge promo (GitHub repo + Telegram contact).
const QR = require('qrcode');
const fs = require('fs');
const OUT = 'C:/Users/andre/OneDrive/Sorgenti/Automate-Your-Business-with-AI/assets';
const opts = { errorCorrectionLevel: 'H', margin: 2, width: 512,
  color: { dark: '#0b1220', light: '#ffffff' } };
async function gen(text, file) {
  await QR.toFile(OUT + '/' + file, text, opts);
  console.log('wrote', file, '<-', text);
}
(async () => {
  await gen('https://github.com/Graphene-Lab/AgentBridge/', 'qr-agentbridge-github.png');
  await gen('https://t.me/yd8j9', 'qr-agentbridge-telegram.png');
})();
