const fs = require('fs'), zlib = require('zlib');
function parse(b){let o=8,idat=[];while(o<b.length){const l=b.readUInt32BE(o),t=b.slice(o+4,o+8).toString();if(t==='IDAT')idat.push(b.slice(o+8,o+8+l));o+=12+l;}const ih=b.slice(16,29);return{w:ih.readUInt32BE(0),h:ih.readUInt32BE(4),ct:ih[9],idat:Buffer.concat(idat)};}
function unf(r,w,h,bpp){const s=w*bpp,out=Buffer.alloc(h*s);let p=0;for(let y=0;y<h;y++){const f=r[p++],row=r.slice(p,p+s);p+=s;const cu=y*s,pv=y>0?(y-1)*s:-1;for(let x=0;x<s;x++){const a=x>=bpp?out[cu+x-bpp]:0,bu=pv>=0?out[pv+x]:0,cc=(x>=bpp&&pv>=0)?out[pv+x-bpp]:0;let v=row[x];if(f===1)v+=a;else if(f===2)v+=bu;else if(f===3)v+=((a+bu)>>1);else if(f===4){const P=a+bu-cc,pa=Math.abs(P-a),pb=Math.abs(P-bu),pc=Math.abs(P-cc);v+=(pa<=pb&&pa<=pc)?a:pb<=pc?bu:cc;}out[cu+x]=v&255;}}return out;}
const R='C:/Users/andre/OneDrive/Sorgenti/AgentBridge/examples/';
const files=['04-presentations/pitch-deck/img/pitch-deck.png','04-presentations/sales-presentation/img/sales-presentation.png','04-presentations/training-deck/img/training-deck.png'];
for(const f of files){
  const b=fs.readFileSync(R+f);const p=parse(b);const bpp=p.ct===6?4:3;
  const img=unf(zlib.inflateSync(p.idat),p.w,p.h,bpp);
  // sample every 40th pixel; compute per-channel mean and stddev, and count distinct-ish colors
  let n=0,sr=0,sg=0,sb=0,sr2=0,sg2=0,sb2=0;const colors=new Set();
  for(let y=0;y<p.h;y+=40)for(let x=0;x<p.w;x+=40){const i=(y*p.w+x)*bpp;const r=img[i],g=img[i+1],bl=img[i+2];n++;sr+=r;sg+=g;sb+=bl;sr2+=r*r;sg2+=g*g;sb2+=bl*bl;colors.add((r>>4)+','+(g>>4)+','+(bl>>4));}
  const mr=sr/n,mg=sg/n,mb=sb/n;
  const vr=Math.sqrt(sr2/n-mr*mr),vg=Math.sqrt(sg2/n-mg*mg),vb=Math.sqrt(sb2/n-mb*mb);
  console.log(f.split('/')[1].padEnd(20),'mean='+Math.round(mr)+','+Math.round(mg)+','+Math.round(mb),'std='+vr.toFixed(1)+','+vg.toFixed(1)+','+vb.toFixed(1),'colors~'+colors.size);
}
