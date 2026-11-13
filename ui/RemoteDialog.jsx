import React,{useEffect,useRef,useState} from 'react';
import {Button} from '@astryxdesign/core/Button';
import { Icon } from './Icons.jsx';

export function RemoteDialog({state,send,close}){
  const dialog=useRef(null);
  const [port,setPort]=useState(String(state?.port??8787));
  const [seconds,setSeconds]=useState(0);
  const [copied,setCopied]=useState(false);
  useEffect(()=>{const el=dialog.current;const trigger=document.activeElement;el.showModal();
    return()=>{el.close();trigger?.focus();};},[]);
  useEffect(()=>{setPort(String(state?.port??8787));},[state?.port]);
  useEffect(()=>{
    if(!state?.offer){setSeconds(0);return;}
    const deadline=performance.now()+state.offer.secondsLeft*1000;
    const tick=()=>setSeconds(Math.max(0,Math.ceil((deadline-performance.now())/1000)));
    tick();const timer=setInterval(tick,250);return()=>clearInterval(timer);
  },[state?.offer?.qr,state?.offer?.secondsLeft]);
  const adapters=state?.adapters??[];
  const selected=adapters.find(a=>a.id===state?.adapterId);
  const applyPort=(event)=>{
    const value=Number(event.currentTarget.value);
    if(Number.isInteger(value)&&value>=1025&&value<=65535)send('remoteSetPort',{value});
    else setPort(String(state?.port??8787));
  };
  async function copy(){
    try {await navigator.clipboard.writeText(state.address);setCopied(true);}
    catch {const el=dialog.current.querySelector('.remote-address input');el?.select();setCopied(false);}
  }
  return <dialog className="camera-editor remote-dialog" ref={dialog} aria-labelledby="remote-title"
    onCancel={e=>{e.preventDefault();close();}}>
    <section className="remote-dialog-layout">
      <header className="remote-dialog-head"><h2 id="remote-title">Удалённое управление</h2>
        <button type="button" className="sound-close" onClick={close} aria-label="Закрыть"><Icon name="close" size={14} /></button></header>
      <section className="editor-body connection-form">
        <label className="remote-toggle">Доступ по Wi-Fi
          <input type="checkbox" role="switch" className="preview-switch" checked={Boolean(state?.enabled)}
            disabled={!state||state.applying} onChange={e=>send('remoteSetEnabled',{value:Number(e.target.checked)})}/></label>
        <section className="remote-network-fields">
          <label>Сеть<select value={state?.adapterId??''} disabled={!state||state.applying}
            onChange={e=>send('remoteSetAdapter',{id:e.target.value})}>
            <option value="">Выберите адаптер</option>
            {Array.from(new Map(adapters.map(a=>[a.id,a])).values()).map(a=>
              <option key={a.id} value={a.id}>{a.name} · {a.address}</option>)}
          </select></label>
          <label>Порт<input type="number" min="1025" max="65535" value={port} disabled={!state||state.applying}
            onChange={e=>setPort(e.target.value)} onBlur={applyPort}
            onKeyDown={e=>{if(e.key==='Enter'){e.preventDefault();e.currentTarget.blur();}}}/></label>
        </section>
        {selected?.reason&&<p className="hint">{selected.reason}</p>}
        <label className="remote-toggle">Принимать подключения из других частных подсетей
          <input type="checkbox" role="switch" className="preview-switch" checked={Boolean(state?.allowRouted)}
            disabled={!state||state.applying} onChange={e=>send('remoteSetRouted',{value:Number(e.target.checked)})}/></label>
        <p className="hint">Нужно, если контроллер-хаб и панель в разных подсетях колледжа. Разрешены только адреса 10.x, 172.16–31.x и 192.168.x.</p>
        {state?.address&&<section className="remote-address">
          <input aria-label="Адрес сервера" readOnly value={state.address} onFocus={e=>e.target.select()}/>
          <Button label={copied?'Скопировано':'Копировать'} size="sm" onClick={copy}/>
        </section>}
        {state?.address&&<section className="remote-offer">
          {state.offer&&seconds>0&&<img src={state.offer.qr} alt="QR для привязки устройства"/>}
          <section><p>{state.offer&&seconds>0?`Код действует ещё ${Math.floor(seconds/60)}:${String(seconds%60).padStart(2,'0')}`:
            state.offer?'Код истёк':'Код не активен'}</p>
            <Button label="Новый код привязки" size="sm" onClick={()=>send('remoteNewCode')}/>
            {state.offer&&seconds>0&&state.offer.url&&<input aria-label="Ссылка привязки для хаба" readOnly value={state.offer.url}
              onFocus={e=>e.target.select()}/>}
            {state.offer&&seconds>0&&state.offer.url&&<p className="hint">Ссылку можно вставить в хабе другого ПК («Аудитории»).</p>}</section>
        </section>}
        <p role="status">{state?.status??'Загрузка…'}{state?.address&&` · Подключено: ${state.sessions} · Управляет: ${state.owner.kind==='desktop'?'ПК':state.owner.name||'—'}`}</p>
        <section className="remote-devices" aria-label="Привязанные устройства">
          {(state?.devices??[]).map(d=><section key={d.id}><span>{d.name}<small>{new Date(d.lastSeen).toLocaleString('ru')}</small></span>
            <Button label="Отозвать" size="sm" onClick={()=>send('remoteRevoke',{id:d.id})}/></section>)}
        </section>
        <p className="hint">Используйте доверенную частную сеть. Соединение не шифруется. Если телефон не видит ПК, проверьте брандмауэр. Если IP ПК изменится, отсканируйте QR заново.</p>
      </section>
      <footer className="remote-dialog-footer"><Button label="Закрыть" onClick={close}/></footer>
    </section>
  </dialog>;
}
