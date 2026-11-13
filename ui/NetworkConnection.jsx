import { useState } from 'react';
import { Button } from '@astryxdesign/core/Button';

export function ConnectionButton({ connected, connecting, canConnect, submit = false, onConnect, onDisconnect }) {
  return <Button className={connected ? undefined : 'connect-primary'}
    label={connected ? 'Отключить' : connecting ? 'Подключение…' : 'Подключить'}
    variant={connected ? 'secondary' : 'primary'}
    type={submit && !connected ? 'submit' : 'button'}
    disabled={!connected && (!canConnect || connecting)}
    onClick={connected ? (event) => {
      event.preventDefault();
      onDisconnect();
    } : onConnect} />;
}

export function RefreshButton({ disabled, onClick }) {
  return <Button label="Обновить" variant="secondary" type="button" disabled={disabled} onClick={onClick} />;
}

export function CameraSummary({ name, type, known, canMove, canZoom }) {
  const capability = (supported) => known ? (supported ? 'Да' : 'Нет') : 'Неизвестно';
  return <section className="camera-summary" aria-label="Выбранная камера">
    <h2 title={name || undefined}>{name || 'Камера не выбрана'}</h2>
    <dl>
      <dt>Тип</dt><dd>{type}</dd>
      <dt>Pan/Tilt</dt><dd>{capability(canMove)}</dd>
      <dt>Zoom</dt><dd>{capability(canZoom)}</dd>
    </dl>
  </section>;
}

export function NetworkConnection({ network, send, onDisconnect, canMove, canZoom }) {
  const [address, setAddress] = useState('');
  const [selected, setSelected] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [passwordVisible, setPasswordVisible] = useState(false);
  const connectedIndex = network.connected
    ? network.cameras.findIndex((camera) => camera.Address === network.address) : -1;
  const displayedSelection = network.connected ? (connectedIndex >= 0 ? String(connectedIndex) : 'connected') : selected;
  const summaryName = network.connected ? (network.name || network.address) :
    (selected ? network.cameras[Number(selected)]?.Name : null) || address.trim();

  function connect(event) {
    event.preventDefault();
    if (!address.trim() || network.connecting || network.connected) return;
    send('networkConnect', {
      address: address.trim(),
      selection: selected === '' ? null : Number(selected),
      username: username.trim(),
      password,
    });
  }

  return (<>
    <form className="connection-form" aria-label="Сетевое подключение" onSubmit={connect}>
      <section className="discovery-field">
        <label htmlFor="network-camera-list">Найденные камеры</label>
        <section className="discovery-select" data-searching={network.searching} aria-label="Результаты поиска">
        <select id="network-camera-list" value={displayedSelection}
          title={network.connected ? network.name : selected ? network.cameras[Number(selected)]?.Name : undefined}
          disabled={network.connected || network.cameras.length === 0} aria-busy={network.searching}
          onChange={(event) => {
            setSelected(event.target.value);
            if (event.target.value !== '') setAddress(network.cameras[Number(event.target.value)].Address);
          }}>
          <option value="">{network.searching ? 'Поиск…' : 'Выберите камеру'}</option>
          {network.connected && connectedIndex < 0 && <option value="connected">
            {network.name ?? 'Камера ONVIF'}{network.address && network.name !== network.address ? ` (${network.address})` : ''}
          </option>}
          {network.cameras.map((camera, index) =>
            <option key={camera.Address} value={index}>{camera.Name} ({camera.Address})</option>)}
        </select>
        {network.searching && <i className="select-spinner" aria-hidden="true" />}
        </section>
      </section>
      <label>
        IP или адрес ONVIF
        <input type="text" value={network.connected ? network.address ?? address : address}
          readOnly={network.connected} required maxLength={512} placeholder="192.168.1.50"
          onChange={(event) => { setAddress(event.target.value); setSelected(''); }} />
      </label>
      <fieldset className="network-credentials" aria-label="Учётные данные камеры">
        <label>Логин <input type="text" value={network.connected ? network.username ?? username : username}
          readOnly={network.connected} autoComplete="username"
          onChange={(event) => setUsername(event.target.value)} /></label>
        <label htmlFor="camera-password">Пароль</label>
        <section className="password-field" aria-label="Пароль камеры">
          <input id="camera-password" type={passwordVisible ? 'text' : 'password'} value={password}
            readOnly={network.connected} autoComplete="current-password" onChange={(event) => setPassword(event.target.value)} />
          <Button className="password-toggle" label={passwordVisible ? 'Скрыть пароль' : 'Показать пароль'}
            size="sm" variant="ghost" type="button" onClick={() => setPasswordVisible((visible) => !visible)}>
            <i className="eye-icon" data-hidden={!passwordVisible} aria-hidden="true" />
          </Button>
        </section>
      </fieldset>
      <section className="connection-actions" aria-label="Действия с подключением">
        <RefreshButton disabled={network.searching} onClick={() => send('networkSearch')} />
        <ConnectionButton connected={network.connected} connecting={network.connecting}
          canConnect={Boolean(address.trim())} submit onDisconnect={onDisconnect} />
      </section>
    </form>
    <CameraSummary name={summaryName} type="ONVIF" known={network.connected}
      canMove={canMove} canZoom={canZoom} />
  </>);
}
