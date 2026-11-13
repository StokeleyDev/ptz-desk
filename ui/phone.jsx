import { PhoneAudio } from './PhoneAudio.jsx';
import { RoomList } from './RoomList.jsx';
import { Icon } from './Icons.jsx';
import { useScheme, SchemeToggle } from './Scheme.jsx';
import React, {useEffect, useRef, useState} from 'react';
import {createRoot} from 'react-dom/client';
import '@fontsource-variable/inter';
import './phone.css';

const query = new URLSearchParams(location.search);
const demo = query.get('demo') === '1';
let code = new URLSearchParams(location.hash.slice(1)).get('code');
if (location.hash) history.replaceState(null, '', location.href.split('#')[0]);
const cameras = [
  {id:'usb',name:'Общий план',kind:'usb',color:'teal',status:'connected'},
  {id:'net',name:'Трибуна',kind:'onvif',color:'amber',status:'idle'},
  {id:'back',name:'Зал',kind:'onvif',color:'blue',status:'idle'},
];
const demoState = {cameras,activeId:'usb',generation:1,canMove:true,canZoom:true,speed:5,pan:12,tilt:-4,
  zoom:{mode:'absolute',value:42,min:0,max:100},canPreset:true,presets:[true,false,true,false,false,false,false,false],status:'Подключена',connectionState:'connected',
  owner:{kind:'none',isYou:false},
  rooms:[{id:'r301',name:'Аудитория 301',online:true,audio:{speaker:{Present:true,Volume:55,Muted:false},mic:{Present:true,Volume:40,Muted:true}}},
    {id:'r205',name:'Аудитория 205',online:false,audio:null}]};
const statuses = {loading:'Проверяем связь с ПК…', offline:'Нет связи с ПК',
  unpaired:'Отсканируйте QR на экране ПК', revoked:'Доступ отозван. Отсканируйте QR на ПК заново',
  limited:'Слишком много попыток. Подождите минуту', pair:'Введите имя устройства'};

function Phone(){
  const scenario = demo ? query.get('state') : null;
  const [screen,setScreen]=useState(demo ? (scenario || 'live') : 'loading');
  const [state,setState]=useState(demo ? {...demoState,owner:scenario==='busy' ?
    {kind:'remote',name:'Телефон оператора',isYou:false} : demoState.owner} : null);
  const [scheme,toggleScheme]=useScheme();
  const setSpeed=(value)=>{value=Math.max(1,Math.min(10,value));send({type:'speed',value});setState(s=>({...s,speed:value}));};
  const [tab,setTab]=useState(null);
  const [notice,setNotice]=useState('');
  const [presetNote,setPresetNote]=useState('');
  const [pairing,setPairing]=useState(false);
  const [visual,setVisual]=useState({x:0,y:0,z:0});
  const live=useRef(null), retry=useRef(null), socket=useRef(null), seq=useRef(0);
  const hold=useRef({stick:null,zoom:null,x:0,y:0,z:0});
  const blocked=state?.owner.kind==='desktop' || state?.owner.kind==='remote' && !state.owner.isYou;
  const available=(screen==='live'||screen==='busy') && !!state && !blocked && !notice;
  const send=(message)=>{
    if(demo){
      window.dispatchEvent(new CustomEvent('ptz-demo-command',{detail:message}));
      if(message.type==='bankActivate') setState(s=>({...s,activeId:message.cameraId,generation:s.generation+1,
        cameras:s.cameras.map(c=>({...c,status:c.id===message.cameraId?'connected':'idle'})),
        zoom:{...s.zoom,mode:message.cameraId==='net'?'spring':'absolute'}}));
      if(message.type==='speed')setState(s=>({...s,speed:message.value}));
      if(message.type==='zoom'||message.type==='zoomSeek')setState(s=>({...s,zoom:{...s.zoom,value:message.value}}));
    } else if(socket.current?.readyState===WebSocket.OPEN && socket.current.bufferedAmount<4096)
      socket.current.send(JSON.stringify(message));
  };
  function input(x,y,z){
    const s=live.current;
    if(!s?.activeId)return;
    send({type:'input',seq:++seq.current,cameraId:s.activeId,generation:s.generation,stick:{x,y},zoomHold:z});
  }
  function reset(notify=true){
    const previous=hold.current;
    hold.current={stick:null,zoom:null,x:0,y:0,z:0};
    setVisual({x:0,y:0,z:0});
    if(notify && (previous.stick!==null || previous.zoom!==null)){
      input(0,0,0);send({type:'stop'});
    }
  }
  const zoomCommit=useRef(null);
  function commitZoom(type,value){
    if(type==='zoomSeek'){
      clearTimeout(zoomCommit.current);
      zoomCommit.current=setTimeout(()=>send({type,value}),100);
    } else send({type,value});
  }
  useEffect(()=>{
    let closed=false, attempt=0;
    const schedule=(delay)=>{clearTimeout(retry.current);retry.current=setTimeout(connect,delay);};
    const backoff=()=>schedule(Math.min(10,2**attempt++)*1000);
    async function connect(){
      try{
        const response=await fetch('/api/session',{cache:'no-store'});
        const result=await response.json();
        if(closed)return;
        if(response.status===429){
          setScreen('limited');
          schedule(Math.max(1,Number(response.headers.get('Retry-After'))||60)*1000);
          return;
        }
        if(!response.ok){
          if(code && ['unpaired','revoked'].includes(result.reason))setScreen('pair');
          else if(['unpaired','revoked'].includes(result.reason))setScreen(result.reason);
          else throw Error();
          return;
        }
        const ws=new WebSocket('ws://'+location.host+'/ws');
        socket.current=ws;
        ws.onopen=()=>ws.send(JSON.stringify({type:'hello',protocol:1}));
        ws.onmessage=({data})=>{
          if(closed||socket.current!==ws)return;
          const next=JSON.parse(data);
          if(next.type==='state'){
            const previous=live.current;
            if(previous && (previous.generation!==next.generation ||
              (next.owner.kind!=='none'&&!next.owner.isYou))){
              clearTimeout(zoomCommit.current);reset(false);
            }
            live.current=next;setState(next);setScreen('live');attempt=0;
          }else if(next.type==='error'&&next.code==='taken_by_pc'){
            reset(false);setNotice('Управление перехвачено ПК. Отпустите управление и нажмите снова');
          }
        };
        ws.onclose=()=>{
          if(closed||socket.current!==ws)return;
          clearTimeout(zoomCommit.current);reset(false);setScreen('offline');backoff();
        };
      }catch{if(!closed){setScreen('offline');backoff();}}
    }
    retryConnect.current=connect;
    if(!demo)void connect();
    const stop=()=>{clearTimeout(zoomCommit.current);reset();};
    const hide=()=>{if(document.hidden)stop();};
    const tick=setInterval(()=>{const h=hold.current;if(h.stick!==null||h.zoom!==null)input(h.x,h.y,h.z);},50);
    window.addEventListener('blur',stop);window.addEventListener('pagehide',stop);
    document.addEventListener('visibilitychange',hide);
    return()=>{closed=true;clearInterval(tick);clearTimeout(retry.current);clearTimeout(zoomCommit.current);socket.current?.close();
      window.removeEventListener('blur',stop);window.removeEventListener('pagehide',stop);document.removeEventListener('visibilitychange',hide);};
  },[]);
  const retryConnect=useRef(null);
  useEffect(()=>{live.current=state;},[state]);

  async function pair(event){
    event.preventDefault();if(!code||pairing)return;
    setPairing(true);
    try{
      const response=await fetch('/api/pair',{method:'POST',headers:{'Content-Type':'application/json'},
        body:JSON.stringify({code,name:new FormData(event.currentTarget).get('name')})});
      if(response.ok){code=null;setScreen('loading');void retryConnect.current();return;}
      if(response.status===429){
        const delay=Math.max(1,Number(response.headers.get('Retry-After'))||60)*1000;
        setNotice(statuses.limited);setTimeout(()=>{setPairing(false);setNotice('');},delay);return;
      }
      const result=await response.json();
      setNotice(result.reason==='device_limit'?'Достигнут лимит устройств. Удалите одно на ПК':
        'Код недействителен или устарел. Покажите новый QR на ПК');
    }catch{setNotice(statuses.offline);}
    setPairing(false);
  }
  const active=state?.cameras.find(c=>c.id===state.activeId);
  function pointer(channel,vector,enabled){
    const apply=(e)=>{
      const h=hold.current;
      Object.assign(h,vector(e));setVisual({x:h.x,y:h.y,z:h.z});
    };
    const release=(e)=>{
      const h=hold.current;if(h[channel]!==e.pointerId)return;
      h[channel]=null;if(channel==='stick')h.x=h.y=0;else h.z=0;
      setVisual({x:h.x,y:h.y,z:h.z});input(h.x,h.y,h.z);
      if(h.stick===null&&h.zoom===null)send({type:'stop'});
    };
    return {onPointerDown:e=>{if(!enabled||hold.current[channel]!==null)return;
      e.currentTarget.setPointerCapture(e.pointerId);hold.current[channel]=e.pointerId;apply(e);
      const h=hold.current;input(h.x,h.y,h.z);},
      onPointerMove:e=>{if(hold.current[channel]===e.pointerId)apply(e);},
      onPointerUp:release,onPointerCancel:()=>reset(),onLostPointerCapture:e=>{
        if(hold.current[channel]===e.pointerId)reset();
      }};
  }
  const joystick=pointer('stick',e=>{const r=e.currentTarget.getBoundingClientRect();
    const x=(e.clientX-r.left-r.width/2)/(r.width*.42),y=-(e.clientY-r.top-r.height/2)/(r.height*.42);
    const length=Math.max(1,Math.hypot(x,y));return {x:x/length,y:y/length};},available&&state?.canMove);
  const spring=pointer('zoom',e=>{const r=e.currentTarget.getBoundingClientRect();
    return {z:Math.max(-1,Math.min(1,2*(e.clientX-r.left)/r.width-1))};},available&&state?.canZoom);
  const zoom=state?.zoom;
  // Tap recalls a saved preset, a long press stores the current position into the slot.
  function presetPress(slot,saved){
    let timer=null,longPress=false;
    const clear=()=>{clearTimeout(timer);timer=null;};
    return {
      onPointerDown:()=>{longPress=false;clear();timer=setTimeout(()=>{longPress=true;send({type:'presetSave',value:slot});
        setPresetNote('Пресет '+slot+' запомнен');setTimeout(()=>setPresetNote(''),2500);},700);},
      onPointerUp:()=>{clear();if(!longPress&&saved)send({type:'presetGo',value:slot});},
      onPointerLeave:clear,onPointerCancel:clear,onContextMenu:e=>e.preventDefault(),
    };
  }
  if(!state || ['unpaired','revoked','pair','loading','limited'].includes(screen))
    return <article className="phone-card"><h1>AGL PTZ</h1><p role="status">{notice||statuses[screen]}</p>
      {screen==='pair'&&<form onSubmit={pair}><label>Имя устройства<input name="name" maxLength={40}/></label>
        <button disabled={pairing}>Привязать</button></form>}</article>;
  return <>
    <header className="phone-header"><strong>AGL PTZ</strong><span data-status={screen==='offline'?'error':state.connectionState}>
      {screen==='offline'?statuses.offline:state.status}</span><SchemeToggle scheme={scheme} toggle={toggleScheme}/></header>
    {(state.audio || state.rooms?.length || demo) && <nav className="phone-audio-tabs" aria-label="Режим">
      <button aria-pressed={(tab ?? (state.cameras.length ? 'camera' : 'audio')) === 'camera'} onClick={() => setTab('camera')}>Камера</button>
      <button aria-pressed={(tab ?? (state.cameras.length ? 'camera' : 'audio')) === 'audio'} onClick={() => {reset();setTab('audio');}}>Звук</button>
    </nav>}
    {(tab ?? ((state.audio || state.rooms?.length) && !state.cameras.length ? 'audio' : 'camera')) === 'audio' ?
      <>{(state.audio || demo) && <PhoneAudio audio={state.audio ?? (demo ? {speaker:{Present:true,Volume:62,Muted:false},mic:{Present:true,Volume:48,Muted:false}} : null)} send={send} disabled={screen==='offline'}/>}
      {state.rooms?.length > 0 && <section className="phone-rooms"><h2>Аудитории</h2><RoomList rooms={state.rooms} send={send} disabled={screen==='offline'}/></section>}</> : <>
    <nav className="phone-cameras" aria-label="Камеры">{state.cameras.map(c=><button key={c.id} data-color={c.color}
      aria-pressed={c.id===state.activeId} disabled={!available} onClick={()=>{reset();send({type:'bankActivate',
        requestId:String(++seq.current),cameraId:c.id});}}><i data-status={c.status}/>{c.name}</button>)}</nav>
    {(notice||blocked)&&<p className="phone-notice" role="status">{notice||('Занято: '+(state.owner.name||'ПК'))}
        {notice&&<button onClick={()=>{input(0,0,0);setNotice('');}}>Понятно</button>}</p>}
    <section className="phone-console" onContextMenu={e=>{if(e.target.closest('.gesture'))e.preventDefault();}}>
      <section className="phone-movement"><h1>{active?.name||'Выберите камеру'} <small>{active?.kind.toUpperCase()}</small></h1>
        <p className="phone-axes">Pan {state.pan??'—'} · Tilt {state.tilt??'—'}</p>
        <section className="phone-dpad" aria-label="Управление направлением">
          {[[0,1,'0deg','Вверх'],[-1,0,'270deg','Влево'],[1,0,'90deg','Вправо'],[0,-1,'180deg','Вниз']].map(([x,y,arrow,label],i)=>
            <button key={label} className={'gesture direction-'+i} aria-label={label}
              disabled={!available||!state.canMove} {...pointer('stick',()=>({x,y}),available&&state.canMove)}><Icon name="chevron" size={20} style={{rotate:arrow}}/></button>)}
          <button className="gesture phone-stick" aria-label="Джойстик" disabled={!available||!state.canMove} {...joystick}>
            <i style={{translate:`${visual.x*125}% ${-visual.y*125}%`}} data-held={visual.x!==0||visual.y!==0}/></button>
        </section>
        <button disabled={!available||!state.canMove||active?.kind!=='usb'} onClick={()=>send({type:'center'})}>Центр</button>
        {(!state.canMove||!available)&&<p className="phone-hint">{!active?'Выберите камеру':'Движение недоступно'}</p>}
      </section>
      <section className="phone-tools">
        <h2>Пресеты</h2>
        <section className="phone-presets">{Array.from({length:8},(_,i)=>{
          const saved=Boolean(state.presets?.[i]);
          return <button key={i} data-saved={saved} disabled={!available||!state.canPreset}
            aria-label={'Пресет '+(i+1)+(saved?', сохранён':', пусто')}
            {...presetPress(i+1,saved)}>{i+1}</button>;})}</section>
        <p className="phone-hint">{presetNote||(!state.canPreset?'Пресеты недоступны':'Нажмите: вызвать. Удерживайте: запомнить текущую позицию')}</p>
              <h2>Зум</h2>
        <section className="phone-range">
          <button aria-label="Отдалить" disabled={!available||!state.canZoom} onClick={()=>send({type:'zoomStep',value:-1})}>−</button>
          {zoom.mode==='spring'?<button className="gesture spring-zoom" aria-label="Пружинный зум"
            disabled={!available||!state.canZoom} {...spring}><i style={{left:`${50+visual.z*44}%`}}/></button>:
            <input className="gesture" aria-label="Зум" type="range" min={zoom.min??0} max={zoom.max??100}
              disabled={!available||!state.canZoom} value={zoom.value??zoom.percent??0}
              onChange={e=>{const value=Number(e.target.value);commitZoom(active?.kind==='usb'?'zoom':'zoomSeek',value);
                setState(s=>({...s,zoom:{...s.zoom,value}}));}} onPointerUp={()=>{if(active?.kind==='usb')send({type:'flushZoom'});}}/>}
          <button aria-label="Приблизить" disabled={!available||!state.canZoom} onClick={()=>send({type:'zoomStep',value:1})}>+</button>
        </section>
        {zoom.mode==='none'&&<p className="phone-hint">Зум недоступен</p>}
        <p className="phone-speed"><span>Скорость</span><output>{state.speed}/10</output></p>
        <section className="phone-range">
          <button aria-label="Медленнее" disabled={!available||!active||state.speed<=1} onClick={()=>setSpeed(state.speed-1)}>−</button>
          <input className="gesture" aria-label="Скорость" type="range" min="1" max="10" disabled={!available||!active}
            value={state.speed} onChange={e=>setSpeed(Number(e.target.value))}/>
          <button aria-label="Быстрее" disabled={!available||!active||state.speed>=10} onClick={()=>setSpeed(state.speed+1)}>+</button>
        </section>
</section>
    </section></>}
  </>;
}

createRoot(document.querySelector('#root')).render(<Phone/>);

