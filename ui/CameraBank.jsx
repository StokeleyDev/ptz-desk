import { Icon } from './Icons.jsx';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Button } from '@astryxdesign/core/Button';

const colors = { amber: 'Песочный', teal: 'Мятный', blue: 'Синий', rose: 'Розовый',
  violet: 'Фиолетовый', lime: 'Лаймовый', slate: 'Серый' };
const palette = Object.keys(colors);
export const nextCameraColor = (profiles) => palette[(palette.indexOf(profiles.at(-1)?.color) + 1) % palette.length];
export const virtualCameraWarning = 'Виртуальная камера может зависеть от запущенной программы';
export const cameraStates = { idle: 'Не подключена', connecting: 'Подключение…', connected: 'Подключена', error: 'Ошибка' };
const shortError = (message = '') => /прочитать пароль/i.test(message) ? 'Ошибка пароля' :
  /неверный (onvif-логин или )?пароль|доступ запрещён/i.test(message) ? 'Неверный пароль' :
  /занята|другой программой/i.test(message) ? 'Занята' : 'Недоступна';

const menuItems = [
  { label: 'Переименовать', icon: 'edit', flags: { rename: true } },
  { label: 'Изменить', icon: 'gear', flags: {} },
  { label: 'Удалить', icon: 'trash', flags: { remove: true } },
];

function CameraMenu({ anchor, camera, close, onEdit }) {
  const menu = useRef(null);
  useLayoutEffect(() => {
    const element = menu.current;
    const position = () => {
      const rect = anchor.getBoundingClientRect();
      const width = element.offsetWidth;
      const height = element.offsetHeight;
      const gutter = 8;
      const x = Math.min(innerWidth - width - gutter, Math.max(gutter, rect.right - width));
      const below = rect.bottom + 4;
      const preferredY = below + height + gutter <= innerHeight ? below : rect.top - height - 4;
      const y = Math.min(innerHeight - height - gutter, Math.max(gutter, preferredY));
      element.style.left = `${x}px`;
      element.style.top = `${y}px`;
    };
    function outside(event) {
      if (!element.contains(event.target) && !anchor.contains(event.target)) close();
    }
    position();
    element.querySelector('button')?.focus({ preventScroll: true });
    window.addEventListener('resize', position);
    window.addEventListener('scroll', position, true);
    document.addEventListener('pointerdown', outside, true);
    return () => {
      window.removeEventListener('resize', position);
      window.removeEventListener('scroll', position, true);
      document.removeEventListener('pointerdown', outside, true);
    };
  }, [anchor, close]);

  function navigate(event) {
    event.stopPropagation();
    if (event.key === 'Escape' || event.key === 'Tab') {
      event.preventDefault();
      close();
      return;
    }
    if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const items = [...menu.current.querySelectorAll('button')];
    const current = items.indexOf(document.activeElement);
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 :
      (current + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
    items[next].focus({ preventScroll: true });
  }

  return createPortal(
    <section className="bank-popover" ref={menu} role="menu" aria-label={`Действия: ${camera.name}`}
      onKeyDown={navigate}>
      {menuItems.map(({ label, icon, flags }) => <button key={label} type="button" role="menuitem"
        tabIndex={-1} data-danger={Boolean(flags.remove)} onClick={() => {
          close();
          onEdit({ ...camera, ...flags });
        }}><Icon name={icon} size={16} />{label}</button>)}
    </section>, document.body);
}

export function CameraBank({ bank, network, send, switchCamera, onEdit, error }) {
  const dragged = useRef(null);
  const buttons = useRef(new Map());
  const [openMenu, setOpenMenu] = useState(null);
  const closeMenu = useCallback(() => {
    openMenu?.anchor.focus({ preventScroll: true });
    setOpenMenu(null);
  }, [openMenu]);
  const menuCamera = bank.profiles.find((camera) => camera.id === openMenu?.id);
  function navigate(event, index) {
    if (!['ArrowUp', 'ArrowDown'].includes(event.key)) return;
    event.preventDefault();
    event.stopPropagation();
    const current = bank.profiles[index];
    const next = bank.profiles[index + (event.key === 'ArrowUp' ? -1 : 1)];
    if (!next) return;
    if (event.altKey) send('bankReorder', { id: current.id, before: next.id });
    else buttons.current.get(next.id)?.focus();
  }

  return <>
    <header className="bank-heading">
      <h2>Камеры</h2>
      <Button id="bank-add" size="sm" label="Добавить камеру" title="Добавить камеру" onClick={() => onEdit({})}>+</Button>
    </header>
    <ul className="bank-list" aria-label="Список камер">
      {bank.profiles.map((camera, index) => {
        const active = bank.activeId === camera.id;
        const issue = active && error ? error : camera.error;
        const disconnect = camera.state === 'connected' || camera.state === 'connecting';
        return <li className="bank-card" key={camera.id} data-active={active} data-color={camera.color}
          onDragOver={(event) => { if (dragged.current) event.preventDefault(); }}
          onDrop={(event) => {
            event.preventDefault();
            if (dragged.current && dragged.current !== camera.id)
              send('bankReorder', { id: dragged.current, before: camera.id });
            dragged.current = null;
          }}>
          <button type="button" className="bank-select" data-virtual={Boolean(camera.isVirtual)} draggable
            onDragStart={(event) => { dragged.current = camera.id; event.dataTransfer.setData('text/plain', camera.id); }}
            onDragEnd={() => { dragged.current = null; }} ref={(button) => {
            if (button) buttons.current.set(camera.id, button);
            else buttons.current.delete(camera.id);
          }} aria-current={active ? 'true' : undefined} onClick={() => switchCamera(camera.id)}
            onKeyDown={(event) => navigate(event, index)}>
            <kbd>{index < 9 ? index + 1 : '·'}</kbd>
            <strong title={camera.name}>{camera.name}</strong>
            {camera.isVirtual && <small className="bank-virtual" title={virtualCameraWarning}>Виртуальная</small>}
          </button>
          <small className="bank-type">{camera.kind === 'usb' ? 'USB' : 'ONVIF'}</small>
          <Button className="bank-power" variant="ghost" size="sm" label={disconnect ? 'Отключить камеру' : 'Подключить камеру'}
            title={disconnect ? 'Отключить камеру' : 'Подключить камеру'}
            onClick={() => switchCamera(camera.id, disconnect ? 'bankDisconnect' : 'bankActivate')}><Icon name="power" /></Button>
          <button className="bank-menu-trigger" type="button" aria-label={`Действия: ${camera.name}`}
            aria-haspopup="menu" aria-expanded={openMenu?.id === camera.id} onClick={(event) => {
              if (openMenu?.id === camera.id) closeMenu();
              else setOpenMenu({ id: camera.id, anchor: event.currentTarget });
            }}>⋯</button>
          <p className="bank-state" data-state={issue ? 'error' : camera.state} title={issue || cameraStates[camera.state]}>
            <i aria-hidden="true" /><span>{issue ? shortError(issue) : cameraStates[camera.state]}</span>
          </p>
          {issue && <Button className="bank-retry" label="Повторить" title={issue} size="sm" variant="ghost"
            onClick={() => switchCamera(camera.id, error && active ? 'previewRetry' : 'bankActivate')}><Icon name="retry" /></Button>}
        </li>;
      })}
      {!bank.profiles.length && <li className="bank-empty">
        <p>Камер пока нет</p><Button label="Добавить камеру" onClick={() => onEdit({})} />
      </li>}
    </ul>
    {menuCamera && <CameraMenu anchor={openMenu.anchor} camera={menuCamera} close={closeMenu} onEdit={onEdit} />}
  </>;
}

export function CameraEditor({ profile, usb, network, initialColor, close, save, send }) {
  const dialog = useRef(null);
  const [kind, setKind] = useState(profile.kind || 'usb');
  const [name, setName] = useState(profile.name || '');
  const [nameTouched, setNameTouched] = useState(Boolean(profile.id));
  const [target, setTarget] = useState(profile.target || '');
  const [color, setColor] = useState(profile.color || initialColor);
  const [username, setUsername] = useState(profile.username || '');
  const [password, setPassword] = useState('');
  const [clearPassword, setClearPassword] = useState(false);
  const [visible, setVisible] = useState(false);
  const isVirtual = kind === 'usb' && (usb.find((item) => item.target === target)?.isVirtual ||
    (target === profile.target && profile.isVirtual));
  const title = profile.remove ? `Удалить камеру «${profile.name}»?` : profile.rename ? 'Переименовать камеру' :
    profile.id ? 'Изменить камеру' : 'Добавить камеру';

  useEffect(() => {
    const element = dialog.current;
    const opener = document.activeElement;
    element.showModal();
    element.querySelector('[data-initial-focus]')?.focus();
    return () => {
      element.close();
      const destination = profile.id && opener?.isConnected ? opener : document.getElementById('bank-add');
      destination?.focus();
    };
  }, []);

  function chooseKind(next) {
    if (kind === next) return;
    setKind(next);
    setTarget('');
    if (!nameTouched) setName('');
    send(next === 'usb' ? 'bankUsb' : 'bankSearch');
  }

  return <dialog className="camera-editor" ref={dialog} aria-labelledby="camera-editor-title"
    onCancel={(event) => { event.preventDefault(); close(); }}>
    <form className="connection-form" onSubmit={(event) => {
      event.preventDefault();
      save(profile.remove ? 'bankDelete' : profile.rename ? 'bankRename' : 'bankSave',
        { id: profile.id, name: name.trim(), kind, target: target.trim(), color, username, password,
          clearPassword: Number(clearPassword) });
    }}>
      <h2 id="camera-editor-title">{title}</h2>
      <section className="editor-body">
      {!profile.rename && !profile.remove && <>
        <nav className="connection-tabs" aria-label="Тип камеры">
          <Button type="button" label="USB" aria-pressed={kind === 'usb'} onClick={() => chooseKind('usb')} />
          <Button type="button" label="Сеть" aria-pressed={kind === 'onvif'} onClick={() => chooseKind('onvif')} />
        </nav>
        {kind === 'usb' ? <section className="editor-discovery">
          <label>Устройство USB
          <select data-initial-focus value={target} required onChange={(event) => {
            setTarget(event.target.value);
            if (!nameTouched) setName(usb.find((item) => item.target === event.target.value)?.name.slice(0, 80) || '');
          }}>
            <option value="">Выберите устройство</option>
            {target && !usb.some((item) => item.target === target) &&
              <option value={target}>{profile.name}{profile.isVirtual ? ' · Виртуальная' : ''} · недоступна</option>}
            {usb.map((item) => <option key={item.target} value={item.target}>
              {item.name}{item.isVirtual ? ' · Виртуальная' : ''}
            </option>)}
          </select>
          </label>
          <Button className="editor-refresh" size="sm" type="button" variant="secondary"
            label="Обновить USB-устройства" title="Обновить USB-устройства" onClick={() => send('bankUsb')}><Icon name="retry" /></Button>
        </section> : <>
          <section className="editor-discovery editor-onvif-discovery">
          <section className="discovery-field">
            <label htmlFor="bank-discovery">Найденные камеры</label>
            <section className="discovery-select" data-searching={Boolean(network?.searching)}>
            <select id="bank-discovery" data-initial-focus
              value={(network?.cameras ?? []).some((item) => (item.Endpoint || item.Address) === target) ? target : ''}
              aria-busy={Boolean(network?.searching)}
              onChange={(event) => {
                const match = network?.cameras.find((item) => (item.Endpoint || item.Address) === event.target.value);
                if (!match) return;
                setTarget(event.target.value);
                if (!nameTouched) setName((match.Name || match.Address).slice(0, 80));
              }}>
              <option value="">{network?.searching ? 'Поиск…' : 'Выберите камеру'}</option>
              {network?.cameras.map((item) => <option key={item.Endpoint || item.Address}
                value={item.Endpoint || item.Address}>{item.Name} ({item.Address})</option>)}
            </select>
            {network?.searching && <i className="select-spinner" aria-hidden="true" />}
            </section>
          </section>
          <Button className="editor-refresh" size="sm" type="button" variant="secondary"
            label="Обновить поиск ONVIF" title="Обновить поиск ONVIF" disabled={network?.searching}
            onClick={() => send('bankSearch')}><Icon name="retry" /></Button>
          <p className="hint discovery-result" role="status">{network?.searching ? '' :
            network?.cameras.length ? `Найдено камер: ${network.cameras.length}` :
              'Камеры не найдены, введите адрес вручную'}</p>
          </section>
          <label>IP или адрес ONVIF<input value={target} required maxLength={512}
            onChange={(event) => {
              setTarget(event.target.value);
              if (!nameTouched) setName(event.target.value.slice(0, 80));
            }} /></label>
          <label>Логин<input value={username} autoComplete="username" maxLength={512}
            onChange={(event) => setUsername(event.target.value)} /></label>
          <label htmlFor="bank-password">Пароль</label>
          <section className="password-field">
            <input id="bank-password" type={visible ? 'text' : 'password'} value={password}
              placeholder={profile.hasPassword ? 'Пароль сохранён' : undefined} autoComplete="new-password"
              maxLength={2048} onChange={(event) => { setPassword(event.target.value); setClearPassword(false); }} />
            <Button className="password-toggle" label={visible ? 'Скрыть пароль' : 'Показать пароль'}
              type="button" variant="ghost" size="sm" onClick={() => setVisible(!visible)}>
              <i className="eye-icon" data-hidden={!visible} aria-hidden="true" />
            </Button>
          </section>
          {profile.hasPassword && <label className="clear-password">
            <input type="checkbox" checked={clearPassword} onChange={(event) => {
              setClearPassword(event.target.checked);
              if (event.target.checked) setPassword('');
            }} /> Удалить сохранённый пароль
          </label>}
        </>}
        {isVirtual && <p className="hint">{virtualCameraWarning}</p>}
      </>}
      {!profile.remove && <label>Название<input required maxLength={80} value={name}
        data-initial-focus={profile.rename ? '' : undefined}
        onChange={(event) => { setNameTouched(true); setName(event.target.value); }} /></label>}
      {!profile.rename && !profile.remove && <fieldset className="editor-colors">
        <legend>Цветовая метка</legend>
        {Object.entries(colors).map(([value, label]) => <label key={value} title={label}>
          <input type="radio" name="camera-color" value={value} checked={color === value}
            aria-label={label} onChange={() => setColor(value)} />
          <i className="camera-color" data-color={value} aria-hidden="true" />
        </label>)}
      </fieldset>}
      </section>
      <section className="connection-actions">
        <Button type="button" variant="secondary" label="Отмена" data-initial-focus={profile.remove ? '' : undefined}
          onClick={close} />
        <Button className="connect-primary" type="submit" variant="primary"
          label={profile.remove ? 'Удалить' : profile.id ? 'Сохранить' : 'Добавить'}
          disabled={!profile.remove && (!name.trim() || (!profile.rename && !target.trim()))} />
      </section>
    </form>
  </dialog>;
}
