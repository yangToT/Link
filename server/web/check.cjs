// Frontend interaction test with an explicit synthetic API; not a network acceptance test.
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const fs=require('fs'),http=require('http'),path=require('path'),assert=require('assert');
const devices=[{id:'one',name:'workstation',os:'Windows',ip:'100.80.1.2',lanIp:'192.168.20.10',state:'active',role:'admin',connected:true,networks:['192.168.20.0/24'],applications:[{name:'TCP 服务',port:8080}]},{id:'two',name:'laptop',os:'Windows',ip:'100.80.1.3',state:'active',role:'member',connected:true,networks:['192.168.30.0/24'],applications:[{name:'TCP 服务',port:9000}]}];
const state={devices,entryId:'one',mappings:[],events:[],csrf:'fixture',streamEpoch:'test',revision:0};
const streams=new Set();let stateRequests=0,eventRequests=0;function broadcast(){state.revision++;for(const stream of streams)stream.write('event: state\ndata: '+JSON.stringify(state)+'\n\n');}
devices[0].network={adapterName:'Ethernet',bridgeEligible:true,tunDetected:true};
devices[1].layer2={state:'attached',ip:'192.168.20.50',message:'已获得局域网地址'};
const server=http.createServer(async(req,res)=>{if(req.url==='/api/events'){eventRequests++;res.writeHead(200,{'Content-Type':'text/event-stream','Cache-Control':'no-store'});res.write('retry: 100\nevent: state\ndata: '+JSON.stringify(state)+'\n\n');streams.add(res);res.on('close',()=>streams.delete(res));return;}let data='';for await(const chunk of req)data+=chunk;const body=data?JSON.parse(data):{};let result={ok:true};
 if(req.url==='/api/state'){stateRequests++;result=state;}
 else if(req.url==='/api/network-mode')state.networkMode=body.mode;
 else if(req.url==='/api/joins')result={code:'SYNTHETIC-TEST-CODE',server:'https://203.0.113.1:24443'};
 else if(req.url==='/api/device'){if(body.action==='entry')state.entryId=body.id;else if(body.action==='delete'){const index=devices.findIndex(d=>d.id===body.id);devices.splice(index,1);state.mappings=state.mappings.filter(m=>m.deviceId!==body.id);}else devices.find(d=>d.id===body.id).state='paused';}
 else if(req.url==='/api/mappings'){state.mappings.push({...body,id:'mapping',entryPort:22000,state:'pending'});}
 else if(req.url==='/api/mappings/mapping'&&req.method==='DELETE')state.mappings=[];
 else{const files={'/':'index.html','/app.js':'app.js','/style.css':'style.css'};if(!files[req.url]){res.writeHead(404);return res.end();}res.setHeader('Content-Type',req.url.endsWith('.js')?'application/javascript':req.url.endsWith('.css')?'text/css':'text/html');return res.end(fs.readFileSync(path.join(__dirname,files[req.url])));}
 if(req.method!=='GET')broadcast();res.setHeader('Content-Type','application/json');res.end(JSON.stringify(result));});
(async()=>{await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const browser=await chromium.launch({channel:'chrome',headless:true});const page=await browser.newPage({viewport:{width:1200,height:800}});const errors=[];page.on('pageerror',e=>errors.push(e.message));page.on('dialog',d=>d.accept());
 try{await page.goto('http://127.0.0.1:'+server.address().port);await page.locator('td').filter({hasText:'workstation'}).first().waitFor();
 for(const [lan,connected,expected] of [[{enabled:true,state:'attached'},true,'已取得地址'],[{enabled:true,state:'entry-ready'},true,'入口已就绪'],[{enabled:true,state:'blocked'},true,'需处理'],[{enabled:true,state:'attached'},false,'离线待确认'],[{enabled:false,prepared:true},true,'未开启']]){
  devices[0].layer2=lan;devices[0].connected=connected;broadcast();await page.waitForFunction(expected=>document.querySelector('.lan-badge').textContent.includes(expected),expected);
 }
 devices[0].layer2={enabled:false};devices[0].connected=true;broadcast();
 await page.getByRole('button',{name:'＋ 添加设备'}).click();await page.getByRole('button',{name:'生成加入码'}).click();assert.equal(await page.locator('textarea').inputValue(),'SYNTHETIC-TEST-CODE');await page.getByRole('button',{name:'关闭',exact:true}).click();
 await page.getByRole('button',{name:'服务映射',exact:true}).click();await page.getByRole('button',{name:'＋ 添加映射'}).click();await page.locator('#mapping-name').fill('web-preview');await page.locator('#mapping-device').selectOption('two');await page.getByRole('button',{name:'添加映射',exact:true}).click();await page.locator('td').filter({hasText:'web-preview'}).first().waitFor();assert.equal(state.mappings[0].port,9000);
 await page.getByRole('button',{name:'详情',exact:true}).click();assert((await page.locator('textarea').inputValue()).includes('22000'));await page.getByRole('button',{name:'关闭',exact:true}).click();
 await page.getByRole('button',{name:'设备',exact:true}).click();fs.mkdirSync(path.join(__dirname,'../../artifacts/qa'),{recursive:true});await page.screenshot({path:path.join(__dirname,'../../artifacts/qa/admin.png'),fullPage:true});
 await page.getByRole('button',{name:'网络入口',exact:true}).click();assert(await page.getByRole('button',{name:'启用二层接入',exact:true}).isDisabled());
 state.mappings=[];state.layer2Configured=true;await page.reload();await page.getByRole('button',{name:'网络入口',exact:true}).click();
 assert(await page.getByRole('button',{name:'启用二层接入',exact:true}).isDisabled(),'entry opted out but mode could be enabled');
 devices[0].layer2={enabled:true,prepared:true,state:'off'};broadcast();await page.waitForFunction(()=>!document.querySelector('[data-action="network-mode"]').disabled);
 await page.getByRole('button',{name:'启用二层接入',exact:true}).click();await page.getByRole('button',{name:'切回路由模式',exact:true}).waitFor();assert.equal(state.networkMode,'bridged');
 await page.getByRole('button',{name:'服务映射',exact:true}).click();await page.getByRole('heading',{name:'局域网服务'}).waitFor();assert.equal(await page.getByRole('button',{name:'＋ 添加映射'}).count(),0);assert((await page.locator('#content').innerText()).includes('192.168.20.50'));
 await page.screenshot({path:path.join(__dirname,'../../artifacts/qa/layer2-admin.png'),fullPage:true});
 const requestsBefore=stateRequests;await page.waitForTimeout(5500);assert.equal(stateRequests,requestsBefore,'unexpected periodic state polling');
 const previousEvents=eventRequests;for(const stream of streams)stream.end();await page.waitForFunction(()=>document.getElementById('status').textContent.includes('重连'));await new Promise(resolve=>setTimeout(resolve,400));assert(eventRequests>previousEvents,'SSE did not reconnect');
 devices[1].layer2.ip='192.168.20.51';broadcast();await page.getByText('192.168.20.51',{exact:true}).waitFor();assert.equal(stateRequests,requestsBefore,'push fetched a polling snapshot');
 await page.getByRole('button',{name:'设备',exact:true}).click();await page.locator('[data-action=\"device\"][data-id=\"two\"]').click();await page.getByRole('button',{name:'删除设备',exact:true}).click();await page.waitForFunction(()=>!document.querySelector('[data-action=\"device\"][data-id=\"two\"]'));assert.equal(devices.length,1);
 for(const stream of streams){stream.write('event: revoked\ndata: {}\n\n');stream.end();}await page.getByRole('heading',{name:'连接授权已失效'}).waitFor();
 await page.setViewportSize({width:375,height:812});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));assert.deepEqual(errors,[]);console.log('PASS: device list, join, mapping, generic address, layer2 mode guards, SSE push/reconnect/revocation, no 5s polling, mobile layout, no JS errors');
 }finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);process.exitCode=1;server.close();});
