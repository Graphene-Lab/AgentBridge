const fs = require('fs'), zlib = require('zlib');
function parse(b){let o=8,idat=[];while(o<b.length){const l=b.readUInt32BE(o),t=b.slice(o+4,o+8).toString();if(t==='IDAT')idat.push(b.slice(o+8,o+8+l));o+=12+l;}const ih=b.slice(16,29);return{w:ih.readUInt32BE(0),h:ih.readUInt32BE(4),ct:ih[9],idat:Buffer.concat(idat)};}
function unf(r,w,h,bpp){const s=w*bpp,out=Buffer.alloc(h*s);let p=0;for(let y=0;y<h;y++){const f=r[p++],row=r.slice(p,p+s);p+=s;const cu=y*s,pv=y>0?(y-1)*s:-1;for(let x=0;x<s;x++){const a=x>=bpp?out[cu+x-bpp]:0,bu=pv>=0?out[pv+x]:0,cc=(x>=bpp&&pv>=0)?out[pv+x-bpp]:0;let v=row[x];if(f===1)v+=a;else if(f===2)v+=bu;else if(f===3)v+=((a+bu)>>1);else if(f===4){const P=a+bu-cc,pa=Math.abs(P-a),pb=Math.abs(P-bu),pc=Math.abs(P-cc);v+=(pa<=pb&&pa<=pc)?a:pb<=pc?bu:cc;}out[cu+x]=v&255;}}return out;}
const files=['02-documents/client-invoice/img/client-invoice.png','02-documents/service-contract/img/service-contract.png','02-documents/business-letter/img/business-letter.png','02-documents/project-proposal/img/project-proposal.png','02-documents/meeting-minutes/img/meeting-minutes.png','05-pdf-reports/financial-report/img/financial-report.png','05-pdf-reports/market-analysis/img/market-analysis.png','05-pdf-reports/research-report/img/research-report.png'];
const R='C:/Users/andre/OneDrive/Sorgenti/AgentBridge/examples/';
for(const f of files){
  const b=fs.readFileSync(R+f);const p=parse(b);const bpp=p.ct===6?4:3;
  const img=unf(zlib.inflateSync(p.idat),p.w,p.h,bpp);
  let mag=0;for(let i=0;i<img.length;i+=bpp){const r=img[i],g=img[i+1],bl=img[i+2];if(r>230&&g<40&&bl>230)mag++;}
  let sr=0,sg=0,sb=0;const base=(p.h-1)*p.w*bpp;
  for(let x=0;x<p.w;x++){sr+=img[base+x*bpp];sg+=img[base+x*bpp+1];sb+=img[base+x*bpp+2];}
  console.log(f.split('/')[1].padEnd(18), p.w+'x'+p.h, 'magenta='+mag, 'bottomAvg='+Math.round(sr/p.w)+','+Math.round(sg/p.w)+','+Math.round(sb/p.w));
}
