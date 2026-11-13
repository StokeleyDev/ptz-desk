import React from 'react';

// Volume / mute controls for the local speakerphone or, with roomId, for a room reached through the hub.
export function PhoneAudio({audio, send, disabled = false, roomId = null}) {
  const message = (kind, target, value) => roomId ?
    {type: 'roomAudio' + kind, roomId, target, value} : {type: 'audio' + kind, target, value};
  return <section className="audio-controls" aria-label="Звук">
    {[['speaker','Динамик'],['mic','Микрофон']].map(([target,label]) => {
      const endpoint = audio?.[target];
      const unavailable = disabled || !endpoint?.Present;
      const fixed = endpoint?.Present && endpoint.Adjustable === false;
      return <section key={target}>
        <h2>{label}</h2>
        <label>Громкость <output>{endpoint?.Volume == null || fixed ? '' : endpoint.Volume + '%'}</output>
          <input type="range" min="0" max="100" value={endpoint?.Volume ?? 0} disabled={unavailable || fixed}
            onChange={event => send(message('Volume', target, Number(event.target.value)))}/>
        </label>
        <button type="button" disabled={unavailable} aria-pressed={Boolean(endpoint?.Muted)}
          onClick={() => send(message('Mute', target, !endpoint.Muted))}>
          {endpoint?.Muted ? 'Включить звук' : 'Выключить звук'}
        </button>
        {!endpoint?.Present && <p>Устройство недоступно</p>}
        {fixed && <p>Громкость фиксирована, доступно только отключение звука</p>}
      </section>;
    })}
  </section>;
}
