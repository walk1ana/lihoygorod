// Сервер игры «Ромчик»: раздаёт index.html и пересылает сообщения между игроками (WebSocket).
// Без зависимостей. Запуск:  node server.js        (порт 1220, или PORT=3000 node server.js)
// Файл игры лежит рядом и называется index.html (или: node server.js другой.html)
const http=require('http'),fs=require('fs'),path=require('path'),crypto=require('crypto'),os=require('os');
const PORT=+process.env.PORT||1220,FILE=path.resolve(__dirname,process.argv[2]||'index.html');
const peers=new Map(),pairs=new Map(); // id -> {ws}, cid -> {a,b}
const rid=()=>'p'+crypto.randomBytes(5).toString('hex');

const srv=http.createServer((req,res)=>{
 if(req.url.split('?')[0]==='/health'){res.end('ok '+peers.size);return}
 fs.readFile(FILE,'utf8',(e,html)=>{if(e){res.writeHead(500,{'content-type':'text/plain;charset=utf-8'});res.end('Не найден '+FILE);return}
  const inj='<script>window.LG_RELAY=(location.protocol==="https:"?"wss://":"ws://")+location.host+"/ws";</script>';
  html=html.replace('<head>','<head>'+inj);res.writeHead(200,{'content-type':'text/html;charset=utf-8','cache-control':'no-store'});res.end(html)})});

// ---- минимальный WebSocket (RFC 6455) ----
function frame(op,buf){const n=buf.length;let h;if(n<126){h=Buffer.from([0x80|op,n])}else if(n<65536){h=Buffer.alloc(4);h[0]=0x80|op;h[1]=126;h.writeUInt16BE(n,2)}else{h=Buffer.alloc(10);h[0]=0x80|op;h[1]=127;h.writeBigUInt64BE(BigInt(n),2)}return Buffer.concat([h,buf])}
srv.on('upgrade',(req,sock)=>{
 if(req.url.split('?')[0]!=='/ws'||!req.headers['sec-websocket-key']){sock.destroy();return}
 const acc=crypto.createHash('sha1').update(req.headers['sec-websocket-key']+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
 sock.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: '+acc+'\r\n\r\n');
 sock.setNoDelay(true);
 const c={sock,id:null,buf:Buffer.alloc(0),frag:[],send(o){if(!sock.destroyed)try{sock.write(frame(1,Buffer.from(JSON.stringify(o))))}catch(e){}}};
 sock.on('data',d=>{c.buf=Buffer.concat([c.buf,d]);if(c.buf.length>4e6){sock.destroy();return}
  for(;;){const b=c.buf;if(b.length<2)return;const op=b[0]&15,fin=b[0]&128,mk=b[1]&128;let n=b[1]&127,o=2;
   if(n===126){if(b.length<4)return;n=b.readUInt16BE(2);o=4}else if(n===127){if(b.length<10)return;n=Number(b.readBigUInt64BE(2));o=10}
   if(!mk){sock.destroy();return}if(b.length<o+4+n)return;
   const key=b.subarray(o,o+4),p=Buffer.from(b.subarray(o+4,o+4+n));for(let i=0;i<n;i++)p[i]^=key[i&3];c.buf=b.subarray(o+4+n);
   if(op===8){sock.end(frame(8,Buffer.alloc(0)));return}
   if(op===9){sock.write(frame(10,p));continue}
   if(op===10)continue;
   if(op===1||op===2||op===0){c.frag.push(p);if(fin){const t=Buffer.concat(c.frag).toString('utf8');c.frag=[];msg(c,t)}}}});
 const gone=()=>{if(c.dead)return;c.dead=1;if(c.id&&peers.get(c.id)===c)peers.delete(c.id);
  for(const[cid,pr]of pairs){if(pr.a===c.id||pr.b===c.id){const o=peers.get(pr.a===c.id?pr.b:pr.a);pairs.delete(cid);if(o)o.send({t:'close',cid})}}};
 sock.on('close',gone);sock.on('error',gone)});
function msg(c,t){let m;try{m=JSON.parse(t)}catch(e){return}
 if(m.t==='reg'){if(c.id)return;let id=typeof m.id==='string'?m.id.slice(0,64):null;if(id&&!/^[\w-]+$/.test(id))id=null;if(id&&peers.has(id)){c.send({t:'err',type:'unavailable-id'});return}if(!id)do{id=rid()}while(peers.has(id));c.id=id;peers.set(id,c);c.send({t:'ok',id});return}
 if(!c.id)return;
 if(m.t==='conn'){const o=peers.get(m.to);if(!o||o===c){c.send({t:'nopeer'});return}pairs.set(m.cid,{a:c.id,b:m.to});o.send({t:'conn',from:c.id,cid:m.cid});c.send({t:'open',cid:m.cid});return}
 const pr=pairs.get(m.cid);if(!pr||(pr.a!==c.id&&pr.b!==c.id))return;const o=peers.get(pr.a===c.id?pr.b:pr.a);
 if(m.t==='data'){if(o)o.send({t:'data',cid:m.cid,d:m.d})}
 else if(m.t==='close'){pairs.delete(m.cid);if(o)o.send({t:'close',cid:m.cid})}}
srv.listen(PORT,'0.0.0.0',()=>{console.log('Игра запущена. Открой на ПК и на телефоне (в одной Wi-Fi сети):');
 console.log('  http://localhost:'+PORT);for(const l of Object.values(os.networkInterfaces()))for(const a of l)if(a.family==='IPv4'&&!a.internal)console.log('  http://'+a.address+':'+PORT);
 if(!fs.existsSync(FILE))console.log('ВНИМАНИЕ: не найден файл игры '+FILE)});
