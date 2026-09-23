'use strict';
let state={devices:[],mappings:[],events:[],entryId:''},page='devices',csrf='',busy=false;
const content=document.getElementById('content'),dialog=document.getElementById('dialog');
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const names={devices:'设备',entry:'网络入口',mappings:'服务映射',events:'操作记录'};
const device=id=>state.devices.find(d=>d.id===id);
function status(d){return d.state==='disabled'?'已停用':d.state==='paused'?'已断开':d.connected?'在线':'离线';}
function lanBadge(d){const lan=d.layer2;let text='未报告',kind='';
 if(lan){if(!d.connected)text=lan.enabled?'已开启 · 离线待确认':'未开启 · 离线';
 else if(!lan.enabled)text='未开启';
 else{kind='lan-on';text=lan.state==='attached'?'已取得地址':lan.state==='entry-ready'?'入口已就绪':['blocked','cleanup-failed','error'].includes(lan.state)?'已开启 · 需处理':'已开启 · 等待接入';}}
 return `<span class="badge lan-badge ${kind}" title="${esc(lan?.message||'设备报告的局域网接入状态')}">局域网接入 · ${text}</span>`;
}
function online(d){return `<span class="dot ${d.connected?'':'off'}"></span>${status(d)}`;}
function button(label,action,id='',cls=''){return `<button class="${cls}" data-action="${action}" data-id="${esc(id)}">${label}</button>`;}
function toast(text){const el=document.getElementById('toast');el.textContent=text;el.hidden=false;setTimeout(()=>el.hidden=true,3500);}
async function api(path,method='GET',body){const response=await fetch(path,{method,credentials:'same-origin',headers:{'Content-Type':'application/json','X-Link-CSRF':csrf},body:body===undefined?undefined:JSON.stringify(body)});const data=await response.json();if(!response.ok)throw new Error(data.error||'操作失败');return data;}
function modal(title,body){document.getElementById('dialog-title').textContent=title;document.getElementById('dialog-body').innerHTML=body;dialog.showModal();}
function heading(title,sub,action=''){return `<div class="heading"><div><h1>${title}</h1><p>${sub}</p></div>${action}</div>`;}
function render(){document.querySelectorAll('[data-page]').forEach(b=>b.classList.toggle('active',b.dataset.page===page));document.getElementById('breadcrumb').textContent='My network / '+names[page];const entry=device(state.entryId);let html='';
 if(page==='devices')html=heading('设备',`${state.devices.length} 台已注册 · ${state.devices.filter(d=>d.connected).length} 台在线`,button('＋ 添加设备','join','','primary'))+`<div class="entry"><strong>默认网络</strong><span class="muted">入口 ${esc(entry?.name||'未设置')}</span>${button('管理入口','entry-page')}</div>`+(state.devices.length?`<div class="table-wrap"><table><thead><tr><th>设备名称</th><th>状态</th><th class="address">网络地址</th><th class="role">角色</th><th></th></tr></thead><tbody>${state.devices.map(d=>`<tr><td>${esc(d.name)}<small>${esc(d.os)}</small>${lanBadge(d)}</td><td>${online(d)}</td><td class="address mono">${esc(d.ip||'等待分配')}</td><td class="role">${d.id===state.entryId?'<span class="badge">网络入口</span>':'普通设备'}</td><td>${button('管理','device',d.id)}</td></tr>`).join('')}</tbody></table></div>`:'<div class="empty">尚无设备</div>');
 if(page==='entry')html=heading('网络入口','通过入口设备访问它所在网络中的资源。',button('选择入口','choose-entry'))+`<div class="panel"><div class="row"><span>当前入口</span><strong>${esc(entry?.name||'未设置')}</strong></div><div class="row"><span>状态</span><span>${entry?online(entry):'等待设置'}</span></div><div class="row"><span>已识别网络</span><span class="mono">${(entry?.networks||[]).map(esc).join('<br>')||'等待设备报告'}</span></div><div class="row"><span>网络配置</span><span>自动同步</span></div></div>`;
 if(page==='mappings')html=heading('服务映射','通过网络入口访问设备上的服务。',button('＋ 添加映射','add-mapping','','primary'))+(state.mappings.length?`<div class="table-wrap"><table><thead><tr><th>服务</th><th>目标设备</th><th>状态</th><th></th></tr></thead><tbody>${state.mappings.map(m=>`<tr><td>${esc(m.name)}<small>TCP ${m.port}</small></td><td>${esc(device(m.deviceId)?.name||'设备不可用')}</td><td>${({ready:'可用',pending:'等待入口应用配置',offline:'设备离线',error:'转发失败'})[m.state]||'待检查'}</td><td>${button('详情','mapping',m.id)}</td></tr>`).join('')}</tbody></table></div>`:'<div class="empty">尚未添加服务映射</div>')+'<p class="note">入口端口自动分配。状态由入口设备报告。</p>';
 if(page==='events')html=heading('操作记录','设备与网络配置的近期变更。')+state.events.map(e=>`<div class="row"><span>${esc(({entry:'设为网络入口',kick:'踢下线',disable:'停用设备',enable:'启用设备'})[e.action]||e.action)} · ${esc(e.device)}</span><small>${esc(new Date(e.time).toLocaleString())}</small></div>`).join('');
 if(page==='entry'){
 const bridged=state.networkMode==='bridged',info=entry?.network;
 html+=`<div class="panel"><div class="row"><span>物理网卡</span><span>${esc(info?.adapterName||'未确认')} · ${esc(entry?.lanIp||'无可用地址')}</span></div><div class="row"><span>代理 / TUN</span><span>${info?.tunDetected?'已检测到':'未检测到'}</span></div><div class="row"><span>接入模式</span><strong>${bridged?'二层局域网接入（预览）':'路由与端口映射'}</strong></div><p class="note">${esc(info?.warning||'二层模式需要独立组件和有线入口，保持普通上网出口不变。')}</p>${button(bridged?'切回路由模式':'启用二层接入','network-mode',bridged?'routed':'bridged')}</div>`;
 if(!state.layer2Configured)html+='<p class="note">二层服务端组件尚未配置；此时不能启用二层接入。</p>';
 else if(entry&&!entry.layer2?.enabled)html+='<p class="note">请在入口设备的 Link 客户端打开“功能与组件”，安装并启用“局域网接入”。其他设备也可在自己的客户端选择是否启用。</p>';
}
if(page==='mappings'&&state.networkMode==='bridged')html=heading('局域网服务','使用设备的局域网地址和应用实际端口，无需逐个添加映射。')+state.devices.map(d=>`<div class="panel"><strong>${esc(d.name)}</strong><p class="mono">${esc(d.layer2?.ip||'等待地址')}</p><p>${esc(d.layer2?.message||'等待设备报告')}</p><p class="note">取得地址不等于业务已经可用；应用仍需监听可达地址。</p></div>`).join('');
content.innerHTML=html;
const enable=content.querySelector('[data-action="network-mode"][data-id="bridged"]');if(enable)enable.disabled=!state.layer2Configured||!entry?.connected||!entry?.network?.bridgeEligible||!entry?.layer2?.enabled||!entry?.layer2?.prepared||state.mappings.length>0;
}
let events=null,streamEpoch='',revision=-1;const retiredEpochs=new Set();
function receive(next){
 if(next.streamEpoch){
  if(next.streamEpoch!==streamEpoch){if(retiredEpochs.has(next.streamEpoch))return;if(streamEpoch)retiredEpochs.add(streamEpoch);if(retiredEpochs.size>8)retiredEpochs.delete(retiredEpochs.values().next().value);streamEpoch=next.streamEpoch;revision=-1;}
  if(next.revision<revision)return;revision=next.revision;
 }
 state=next;csrf=state.csrf;document.getElementById('status').textContent='已连接';if(!dialog.open)render();
}
async function refresh(){try{receive(await api('/api/state'));return true;}catch(e){document.getElementById('status').textContent='连接不可用';if(!csrf)content.innerHTML=`<h1>打开管理中心</h1><p>${esc(e.message)}</p>`;return false;}}
function startEvents(){
 if(events)events.close();events=new EventSource('/api/events');
 events.addEventListener('state',e=>{try{receive(JSON.parse(e.data));}catch{document.getElementById('status').textContent='状态更新失败';}});
 events.addEventListener('revoked',()=>{events.close();csrf='';dialog.close();content.innerHTML='<h1>连接授权已失效</h1><p>请从客户端重新打开管理中心。</p>';document.getElementById('status').textContent='已断开';});
 events.onerror=()=>{document.getElementById('status').textContent=events.readyState===EventSource.CLOSED?'连接不可用，请从客户端重新打开':'连接中断，正在重连';};
}
window.addEventListener('pagehide',()=>{if(events)events.close();});
window.addEventListener('pageshow',e=>{if(e.persisted&&csrf)startEvents();});
dialog.addEventListener('close',()=>{if(csrf)render();});
document.querySelector('nav').addEventListener('click',e=>{const b=e.target.closest('[data-page]');if(b){page=b.dataset.page;render();}});
document.getElementById('close-dialog').onclick=()=>dialog.close();
document.body.addEventListener('click',async e=>{const b=e.target.closest('[data-action]');if(!b||busy)return;const id=b.dataset.id,a=b.dataset.action;try{
 if(a==='network-mode'){if(!confirm('切换接入模式会重建专用连接。继续？'))return;await api('/api/network-mode','POST',{mode:id});toast('模式已更新，等待设备报告实际状态');return;}
 if(a==='entry-page'){page='entry';render();return;}
 if(a==='join'){modal('添加设备',`<p>生成一次性加入码，交给新设备使用。</p><label>成员权限<select id="join-role"><option value="member">普通成员</option><option value="admin">管理员</option></select></label><p class="note">普通成员使用专用网络；管理员还可管理设备、入口和网络配置。此角色不授予其他设备的文件访问权限。</p><div class="actions">${button('生成加入码','generate-join','','primary')}</div>`);return;}
 if(a==='generate-join'){const value=await api('/api/joins','POST',{role:document.getElementById('join-role').value});dialog.close();modal('加入信息',`<label>服务端地址<input readonly value="${esc(value.server)}"></label><label>一次性加入码<textarea readonly>${esc(value.code)}</textarea></label><p>15 分钟内有效，仅可使用一次。请通过可信方式分享。</p>`);return;}
 if(a==='device'){const d=device(id);modal(d.name,`<div class="row"><span>状态</span><span>${online(d)}</span></div><div class="row"><span>网络地址</span><span class="mono">${esc(d.ip||'未分配')}</span></div><div class="row"><span>设备标识</span><span class="mono">${esc(d.id)}</span></div><div class="row"><span>最后活动</span><span>${d.lastSeen&&!d.lastSeen.startsWith('0001')?esc(new Date(d.lastSeen).toLocaleString()):'尚未连接'}</span></div><div class="row"><span>权限</span><span>${d.role==='admin'?'管理员':'普通成员'}</span></div><div class="actions">${button('设为入口','set-entry',id)}${d.state==='disabled'?button('启用','enable',id):button('停用','disable',id,'danger')}${button('踢下线','kick',id)}${button('删除设备','delete-device',id,'danger')}</div>`);return;}
 if(a==='delete-device'){if(!confirm('删除此设备并撤销访问权限？关联服务映射将移除；删除入口会断开依赖它的局域网接入。再次加入需使用新加入码。'))return;await api('/api/device','POST',{id,action:'delete'});dialog.close();toast('设备已删除');return;}
 if(a==='choose-entry'){modal('选择网络入口',`<label>在线设备<select id="entry-device">${state.devices.filter(d=>d.connected&&d.networks?.length).map(d=>`<option value="${esc(d.id)}">${esc(d.name)}</option>`).join('')}</select></label><p>入口设备需要持续在线。</p><div class="actions">${button('设为入口','apply-entry','','primary')}</div>`);return;}
 if(['set-entry','apply-entry','kick','disable','enable'].includes(a)){const selected=a==='apply-entry'?document.getElementById('entry-device').value:id;if(!selected)throw new Error('没有可用的入口设备');const action=['set-entry','apply-entry'].includes(a)?'entry':a;if(['kick','disable'].includes(action)&&!confirm(action==='kick'?'断开此设备？需要在客户端手动重新连接。':'停用此设备？访问权限将被撤销。'))return;await api('/api/device','POST',{id:selected,action});dialog.close();toast('操作已完成');return;}
 if(a==='add-mapping'){modal('添加服务映射',`<label>名称<input id="mapping-name" maxlength="64" placeholder="例如 web-preview"></label><label>目标设备<select id="mapping-device">${state.devices.filter(d=>d.connected).map(d=>`<option value="${esc(d.id)}">${esc(d.name)}</option>`).join('')}</select></label><label>本机应用<select id="mapping-port"></select></label><p>请先启动目标应用，客户端会报告监听中的服务。</p><div class="actions">${button('添加映射','save-mapping','','primary')}</div>`);const update=()=>{const d=device(document.getElementById('mapping-device').value);document.getElementById('mapping-port').innerHTML=(d?.applications||[]).map(p=>`<option value="${p.port}">${esc(p.name)} · ${p.port}</option>`).join('');};document.getElementById('mapping-device').onchange=update;update();return;}
 if(a==='save-mapping'){await api('/api/mappings','POST',{name:document.getElementById('mapping-name').value.trim(),deviceId:document.getElementById('mapping-device').value,port:Number(document.getElementById('mapping-port').value)});dialog.close();toast('已添加，等待入口设备建立映射');return;}
 if(a==='mapping'){const m=state.mappings.find(x=>x.id===id),entry=device(state.entryId);modal(m.name,`<div class="row"><span>目标设备</span><span>${esc(device(m.deviceId)?.name)}</span></div><div class="row"><span>本机端口</span><span>${m.port}</span></div><div class="row"><span>入口地址</span><span class="mono">${esc(entry?.lanIp||'等待入口')}:${m.entryPort}</span></div><p class="note">应用需要公布访问地址时，请使用此入口地址。具体设置取决于应用自身。</p><label>应用公布地址<textarea readonly>${esc(entry?.lanIp||'')}:${m.entryPort}</textarea></label><div class="actions">${button('移除映射','remove-mapping',id,'danger')}</div>`);return;}
 if(a==='remove-mapping'){if(!confirm('移除此服务映射？'))return;await api('/api/mappings/'+encodeURIComponent(id),'DELETE');dialog.close();toast('映射已移除');}
 }catch(err){toast(err.message);}});
(async()=>{const hash=new URLSearchParams(location.hash.slice(1)),ticket=hash.get('ticket');history.replaceState(null,'',location.pathname);if(ticket){try{const result=await api('/session','POST',{ticket});csrf=result.csrf;}catch(e){toast(e.message);}}await refresh();startEvents();})();
