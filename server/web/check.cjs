// Frontend interaction test with an explicit synthetic API; not a network acceptance test.
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const fs=require('fs'),http=require('http'),path=require('path'),assert=require('assert');
const devices=[{id:'one',name:'workstation',os:'Windows',ip:'100.80.1.2',lanIp:'192.168.20.10',state:'active',role:'admin',connected:true,networks:['192.168.20.0/24'],applications:[{name:'TCP 服务',port:8080}]},{id:'two',name:'laptop',os:'Windows',ip:'100.80.1.3',state:'active',role:'member',connected:true,networks:['192.168.30.0/24'],applications:[{name:'TCP 服务',port:9000}]}];
const state={devices,entryId:'one',mappings:[],events:[],csrf:'fixture'};
const server=http.createServer(async(req,res)=>{let data='';for await(const chunk of req)data+=chunk;const body=data?JSON.parse(data):{};let result={ok:true};
 if(req.url==='/api/state')result=state;
 else if(req.url==='/api/joins')result={code:'SYNTHETIC-TEST-CODE',server:'https://203.0.113.1:24443'};
 else if(req.url==='/api/device'){if(body.action==='entry')state.entryId=body.id;else devices.find(d=>d.id===body.id).state='paused';}
 else if(req.url==='/api/mappings'){state.mappings.push({...body,id:'mapping',entryPort:22000,state:'pending'});}
 else if(req.url==='/api/mappings/mapping'&&req.method==='DELETE')state.mappings=[];
 else{const files={'/':'index.html','/app.js':'app.js','/style.css':'style.css'};if(!files[req.url]){res.writeHead(404);return res.end();}res.setHeader('Content-Type',req.url.endsWith('.js')?'application/javascript':req.url.endsWith('.css')?'text/css':'text/html');return res.end(fs.readFileSync(path.join(__dirname,files[req.url])));}
 res.setHeader('Content-Type','application/json');res.end(JSON.stringify(result));});
(async()=>{await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const browser=await chromium.launch({channel:'chrome',headless:true});const page=await browser.newPage({viewport:{width:1200,height:800}});const errors=[];page.on('pageerror',e=>errors.push(e.message));page.on('dialog',d=>d.accept());
 try{await page.goto('http://127.0.0.1:'+server.address().port);await page.locator('td').filter({hasText:'workstation'}).first().waitFor();
 await page.getByRole('button',{name:'＋ 添加设备'}).click();await page.getByRole('button',{name:'生成加入码'}).click();assert.equal(await page.locator('textarea').inputValue(),'SYNTHETIC-TEST-CODE');await page.getByRole('button',{name:'关闭',exact:true}).click();
 await page.getByRole('button',{name:'服务映射',exact:true}).click();await page.getByRole('button',{name:'＋ 添加映射'}).click();await page.locator('#mapping-name').fill('web-preview');await page.locator('#mapping-device').selectOption('two');await page.getByRole('button',{name:'添加映射',exact:true}).click();await page.locator('td').filter({hasText:'web-preview'}).first().waitFor();assert.equal(state.mappings[0].port,9000);
 await page.getByRole('button',{name:'详情',exact:true}).click();assert((await page.locator('textarea').inputValue()).includes('22000'));await page.getByRole('button',{name:'关闭',exact:true}).click();
 await page.getByRole('button',{name:'设备',exact:true}).click();fs.mkdirSync(path.join(__dirname,'../../artifacts/qa'),{recursive:true});await page.screenshot({path:path.join(__dirname,'../../artifacts/qa/admin.png'),fullPage:true});
 await page.setViewportSize({width:375,height:812});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));assert.deepEqual(errors,[]);console.log('PASS: device list, one-time join dialog, application mapping, generated configuration, mobile layout, no JS errors');
 }finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);process.exitCode=1;server.close();});
