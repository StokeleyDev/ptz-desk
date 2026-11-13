import React, { useState } from 'react';
import { Icon } from './Icons.jsx';
import { EndpointCard } from './EndpointCard.jsx';
import { HoldButton } from './HoldButton.jsx';

export function RoomsDialog({ hub, send, close }) {
  const [name, setName] = useState('');
  const [link, setLink] = useState('');
  const rooms = hub?.rooms ?? [];
  const add = (event) => {
    event.preventDefault();
    send('hubAdd', { name, link });
    setLink('');
  };
  return <dialog ref={(node) => { if (node && !node.open) node.showModal(); }} className="sound-dialog rooms-dialog"
    aria-labelledby="rooms-title" onCancel={(event) => { event.preventDefault(); close(); }}>
    <header>
      <h2 id="rooms-title">Аудитории</h2>
      <button type="button" className="sound-close" onClick={close} aria-label="Закрыть"><Icon name="close" size={14} /></button>
    </header>
    <div className="sound-body">
      {rooms.length > 0 && <p className="rooms-summary">
        <span>Всего <b>{rooms.length}</b></span>
        <span data-tone="online">В сети <b>{rooms.filter((r) => r.online).length}</b></span>
        {rooms.some((r) => !r.online) && <span data-tone="offline">Нет связи <b>{rooms.filter((r) => !r.online).length}</b></span>}
      </p>}
      {rooms.length === 0 && <p className="sound-empty">Аудиторий пока нет. Добавьте панель по ссылке привязки ниже.</p>}
      {rooms.map((room) => <article key={room.id} className="room" data-online={room.online}>
        <header>
          <h3>{room.name}</h3>
          <span className="room-status" data-online={room.online}><i aria-hidden="true" />{room.online ? 'В сети' : room.status}</span>
          <HoldButton className="icon-button room-remove" label={'Удалить аудиторию ' + room.name}
            onConfirm={() => send('hubRemove', { id: room.id })}><Icon name="trash" /></HoldButton>
        </header>
        <p className="room-address">{room.address}</p>
        <div className="room-endpoints">
          {['speaker', 'mic'].map((target) => <EndpointCard key={target} target={target} endpoint={room.audio?.[target]}
            disabled={!room.online}
            message={(kind, value) => ({ type: 'roomAudio' + kind, roomId: room.id, target, value })}
            send={({ type, ...data }) => send(type, data)} />)}
        </div>
      </article>)}
      <form className="room-add" onSubmit={add}>
        <h3>Добавить аудиторию</h3>
        <label><span>Название</span>
          <input value={name} maxLength={40} placeholder="Например, Аудитория 301" onChange={(e) => setName(e.target.value)} /></label>
        <label><span>Ссылка привязки</span>
          <input value={link} placeholder="http://172.21.x.x:8787/pair#code=…" onChange={(e) => setLink(e.target.value)} /></label>
        <p className="sound-hint">На панели откройте «Удалённое управление», включите доступ и скопируйте «Ссылку привязки». Она одноразовая и действует 2 минуты. Панель запоминается, повторно вводить ссылку не нужно.</p>
        <button disabled={hub?.busy || !link.trim()}>{hub?.busy ? 'Привязка…' : 'Привязать'}</button>
        {hub?.error && <p role="alert" className="sound-error">{hub.error}</p>}
      </form>
    </div>
    <footer className="sound-footer"><button type="button" onClick={close}>Закрыть</button></footer>
  </dialog>;
}
