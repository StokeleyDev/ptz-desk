import React from 'react';
import { Icon } from './Icons.jsx';
import { EndpointCard } from './EndpointCard.jsx';

function DevicePicker({ target, audio, send }) {
  const list = audio?.[target === 'speaker' ? 'speakers' : 'mics'] ?? [];
  const chosen = audio?.settings?.[target] ?? '';
  return <label className="endpoint-device">
    <span>{target === 'speaker' ? 'Устройство вывода' : 'Устройство ввода'}</span>
    <select value={chosen} onChange={(event) => send('audioDevice', { target, id: event.target.value })}>
      <option value="">Устройство связи по умолчанию</option>
      {list.map((device) => <option key={device.Id} value={device.Id}>{device.Name}</option>)}
      {chosen && !list.some((device) => device.Id === chosen) && <option value={chosen}>Выбранное устройство недоступно</option>}
    </select>
  </label>;
}

export function AudioDialog({ audio, send, close }) {
  return <dialog ref={(node) => { if (node && !node.open) node.showModal(); }} className="sound-dialog"
    aria-labelledby="audio-title" onCancel={(event) => { event.preventDefault(); close(); }}>
    <header>
      <h2 id="audio-title">Звук</h2>
      <button type="button" className="sound-close" onClick={close} aria-label="Закрыть"><Icon name="close" size={14} /></button>
    </header>
    <div className="sound-body">
      {['speaker', 'mic'].map((target) => <EndpointCard key={target} target={target} endpoint={audio?.[target]}
        device={<DevicePicker target={target} audio={audio} send={send} />}
        message={(kind, value) => ({ type: 'audio' + kind, target, value })}
        send={({ type, ...data }) => send(type, data)} />)}
      <label className="sound-autostart">
        <span><strong>Режим «Панель» при входе в Windows</strong>
          <small>Приложение запускается в трее и управляет звуком этой аудитории.</small></span>
        <input type="checkbox" role="switch" className="preview-switch" checked={Boolean(audio?.autostart)}
          onChange={(event) => send('audioAutostart', { value: event.target.checked })} />
      </label>
      {audio?.error && <p role="alert" className="sound-error">{audio.error}</p>}
    </div>
    <footer className="sound-footer"><button type="button" onClick={close}>Закрыть</button></footer>
  </dialog>;
}
