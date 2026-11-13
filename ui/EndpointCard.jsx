import React from 'react';
import { Icon } from './Icons.jsx';

// One audio endpoint (speaker or microphone): optional device picker, volume and mute.
// `message(kind, value)` builds the command for this endpoint, `send` takes the message object.
export function EndpointCard({ target, endpoint, message, send, disabled = false, device = null }) {
  const isMic = target === 'mic';
  const present = Boolean(endpoint?.Present);
  const fixed = present && endpoint.Adjustable === false;
  const muted = Boolean(endpoint?.Muted);
  const off = disabled || !present;
  const volume = endpoint?.Volume;
  const icon = (isMic ? 'mic' : 'speaker') + (muted ? 'Muted' : '');
  return <section className="endpoint" data-muted={muted} data-off={off}>
    <header>
      <span className="endpoint-icon"><Icon name={icon} size={20} /></span>
      <h3>{isMic ? 'Микрофон' : 'Динамик'}</h3>
      <output className="endpoint-level">{!present || fixed || volume == null ? '' : volume + '%'}</output>
    </header>
    {device}
    <input type="range" min="0" max="100" value={volume ?? 0} disabled={off || fixed}
      aria-label={isMic ? 'Громкость микрофона' : 'Громкость динамика'}
      style={{ '--fill': (volume ?? 0) + '%' }}
      onChange={(event) => send(message('Volume', Number(event.target.value)))} />
    <footer>
      <button type="button" className="endpoint-mute" aria-pressed={muted} disabled={off}
        onClick={() => send(message('Mute', !muted))}>
        {muted ? (isMic ? 'Микрофон выключен. Включить' : 'Звук выключен. Включить') : (isMic ? 'Выключить микрофон' : 'Выключить звук')}
      </button>
    </footer>
    {!present && <p className="endpoint-note">Устройство недоступно</p>}
    {fixed && <p className="endpoint-note">Громкость этого устройства фиксирована, доступно только отключение звука.</p>}
  </section>;
}
