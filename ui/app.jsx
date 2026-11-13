import React, { useEffect, useRef, useState } from 'react';
import { AudioDialog } from './AudioDialog.jsx';
import { RoomsDialog } from './RoomsDialog.jsx';
import { Icon } from './Icons.jsx';
import { useScheme, SchemeToggle } from './Scheme.jsx';
import { RemoteDialog } from './RemoteDialog.jsx';
import { flushSync } from 'react-dom';
import { createRoot } from 'react-dom/client';
import { Button } from '@astryxdesign/core/Button';
import { Theme } from '@astryxdesign/core/theme';
import '@astryxdesign/core/reset.css';
import '@astryxdesign/core/astryx.css';
import { defaultThemeSlug, themes } from './astryx-themes.js';
import { CameraPreview } from './CameraPreview.jsx';
import { CameraBank, CameraEditor, cameraStates, nextCameraColor, virtualCameraWarning } from './CameraBank.jsx';
import { createDemoBridge } from './demo.js';

const demo = new URLSearchParams(location.search).get('demo') === '1';
const bridge = demo ? createDemoBridge(window.chrome?.webview) : window.chrome?.webview;
let commandRoute = null;
let requestedSwitch = null;
let nextRequest = 0;
const emptyBank = { profiles: [], usb: [], activeId: null };
const send = (type, payload) => bridge?.postMessage({ ...commandRoute, type, ...payload });
const keyCodes = new Map([
  ['ArrowLeft', 37], ['ArrowUp', 38], ['ArrowRight', 39], ['ArrowDown', 40],
  ['+', 187], ['=', 187], ['-', 189],
]);
const directions = [
  ['up', 'Вверх', '↑', 38, 0, -1], ['left', 'Влево', '←', 37, -1, 0],
  ['right', 'Вправо', '→', 39, 1, 0], ['down', 'Вниз', '↓', 40, 0, 1],
];
const initialState = {
  cameras: [], selected: -1, connection: 'Не подключено', status: 'Ожидание Windows…',
  report: '', canMove: false, canZoom: false, pan: null, tilt: null, speed: 5,
  zoom: 0, zoomMin: 0, zoomMax: 1, zoomLabel: '—',
  zoomDetails: 'Зум доступен после подключения',
};

function RangeControl({ label, accessibleName = label, value, min, max, display,
  disabled = false, reason, onChange, onCommit, onStep, minusLabel, plusLabel, children, sliderEvents, spring }) {
  const percent = disabled || max <= min ? 0 : Math.max(0, Math.min(100, 100 * (value - min) / (max - min)));
  return (
    <section className="instrument" aria-label={label}>
      <header><h2>{label}</h2>{display != null && <output>{display}</output>}</header>
      <section className="range-control" aria-label={`Регулировка: ${label}`}>
        <Button className="step-button" label={minusLabel} size="sm" disabled={disabled}
          onClick={() => onStep(-1)}>−</Button>
        <input aria-label={accessibleName} type="range" min={min} max={max} data-spring={spring || undefined}
          value={value} disabled={disabled} style={{ '--range-fill': `${percent}%`,
            '--spring-start': `${Math.min(50, percent)}%`, '--spring-end': `${Math.max(50, percent)}%` }}
          onChange={(event) => onChange(Number(event.target.value))} onPointerUp={onCommit}
          {...sliderEvents} />
        <Button className="step-button" label={plusLabel} size="sm" disabled={disabled}
          onClick={() => onStep(1)}>+</Button>
        {spring && <p className="spring-marks" aria-hidden="true"><i>−</i><i>+</i></p>}
      </section>
      {reason && <p className="hint">{reason}</p>}
      {children}
    </section>
  );
}

function App() {
  const [scheme, toggleScheme] = useScheme();
  const [audioOpen, setAudioOpen] = useState(false);
  const [roomsOpen, setRoomsOpen] = useState(false);
  const [presetMode, setPresetMode] = useState('go');
  const [remoteOpen, setRemoteOpen] = useState(false);
  const [state, setState] = useState(initialState);
  const [preview, setPreview] = useState(false);
  const [previewError, setPreviewError] = useState('');
  const [previewAttempt, setPreviewAttempt] = useState(0);
  const [editor, setEditor] = useState(null);
  const [stick, setStick] = useState({ x: 0, y: 0 });
  const pointer = useRef(null);
  const held = useRef(new Set());
  const [springOffset, setSpringOffset] = useState(0);
  const springInput = useRef(null);
  const springPosition = useRef(0);
  const springAnimation = useRef(0);
  const centerAvailable = !state.network?.active && state.canMove;

  function stop() {
    releaseSpring();
    pointer.current = null;
    held.current.clear();
    setStick({ x: 0, y: 0 });
    send('stop');
  }

  useEffect(() => {
    let previousActiveId;
    const receive = (event) => {
      const incoming = event.data.bank;
      if (nextRequest && incoming?.request !== nextRequest) return;
      if (requestedSwitch === null && previousActiveId !== incoming?.activeId) closePreview();
      previousActiveId = incoming?.activeId;
      requestedSwitch = null;
      commandRoute = incoming?.activeId ? { cameraId: incoming.activeId, generation: incoming.generation } : null;
      setState(event.data);
    };
    const visibilityChanged = () => { if (document.hidden) stop(); };
    bridge?.addEventListener('message', receive);
    window.addEventListener('blur', stop);
    document.addEventListener('visibilitychange', visibilityChanged);
    const heartbeat = setInterval(() => {
      if (pointer.current !== null || held.current.size || springInput.current !== null) send('heartbeat');
    }, 100);
    send('ready');
    return () => {
      clearInterval(heartbeat);
      bridge?.removeEventListener('message', receive);
      window.removeEventListener('blur', stop);
      document.removeEventListener('visibilitychange', visibilityChanged);
      cancelAnimationFrame(springAnimation.current);
    };
  }, []);

  useEffect(() => {
    if (!state.canMove && (pointer.current !== null || held.current.size)) stop();
  }, [state.canMove]);

  useEffect(() => {
    function keyDown(event) {
      if (editor || event.defaultPrevented ||
          event.target.closest?.('dialog, input:not([type="checkbox"]):not([type="range"]), select, textarea, [contenteditable]')) return;
      if (!event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey && /^[1-9]$/.test(event.key)) {
        const profile = state.bank?.profiles[Number(event.key) - 1];
        if (profile) {
          event.preventDefault();
          if (!event.repeat) switchCamera(profile.id);
        }
        return;
      }
      if (event.target.closest?.('.bank-list') && event.key.startsWith('Arrow')) return;
      if (event.code === 'Space' && centerAvailable &&
          !event.altKey && !event.ctrlKey && !event.metaKey &&
          !event.target.closest?.('button, summary, select, input, textarea, a, [contenteditable]')) {
        event.preventDefault();
        if (!event.repeat) center();
        return;
      }
      const key = keyCodes.get(event.key);
      if (!key || event.altKey || event.ctrlKey || event.metaKey ||
          event.target.closest?.('select, input, textarea, [contenteditable]')) return;
      const arrow = key < 100;
      if (arrow ? !state.canMove : !state.canZoom) return;
      event.preventDefault();
      if (arrow) {
        if (event.repeat || held.current.has(key)) return;
        held.current.add(key);
        showHeldDirection();
      }
      send('key', { value: key, down: 1 });
    }
    function keyUp(event) {
      const key = keyCodes.get(event.key);
      if (!held.current.delete(key)) return;
      showHeldDirection();
      event.preventDefault();
      send('key', { value: key, down: 0 });
    }
    window.addEventListener('keydown', keyDown);
    window.addEventListener('keyup', keyUp);
    return () => {
      window.removeEventListener('keydown', keyDown);
      window.removeEventListener('keyup', keyUp);
    };
  }, [state.canMove, state.canZoom, centerAvailable, state.bank, editor]);

  function moveStick(event) {
    if (pointer.current !== event.pointerId) return;
    const bounds = event.currentTarget.getBoundingClientRect();
    const radius = bounds.width * 0.34;
    const dx = (event.clientX - bounds.left - bounds.width / 2) / radius;
    const dy = (event.clientY - bounds.top - bounds.height / 2) / radius;
    const scale = Math.max(1, Math.hypot(dx, dy));
    const x = dx / scale;
    const y = dy / scale;
    setStick({ x, y });
    send('stick', { x, y: -y });
  }

  function releasePointer(event) {
    if (pointer.current === event.pointerId) stop();
  }

  function showHeldDirection() {
    const pressed = (key) => Number(held.current.has(key));
    setStick({ x: pressed(39) - pressed(37), y: pressed(40) - pressed(38) });
  }

  function closePreview() {
    stop();
    setPreview(false);
    setPreviewError('');
  }

  function center() {
    stop();
    send('center');
  }

  function switchCamera(id, action = 'bankActivate') {
    if (action === 'previewRetry') {
      setPreviewError('');
      setPreviewAttempt((value) => value + 1);
      return;
    }
    if (action === 'bankActivate' && requestedSwitch === null && state.bank?.activeId === id &&
        ['connected', 'connecting'].includes(state.connectionState)) return;
    const request = ++nextRequest;
    requestedSwitch = request;
    flushSync(() => {
      closePreview();
      setState((current) => ({ ...current, canMove: false, canZoom: false, pan: null, tilt: null,
        selectedConnected: false, zoomLabel: '—', connectionState: action === 'bankActivate' ? 'connecting' : 'idle',
        bank: { ...current.bank, activeId: action === 'bankActivate' ? id : null,
          profiles: current.bank.profiles.map((item) => ({ ...item,
            state: item.id === id && action === 'bankActivate' ? 'connecting' : item.error && item.id !== id ? 'error' : 'idle',
            error: item.id === id ? null : item.error,
          })) },
      }));
    });
    commandRoute = null;
    send(action, { id, request });
  }

  function openEditor(profile) {
    stop();
    setEditor(profile);
    if (profile.rename || profile.remove) return;
    send(profile.kind === 'onvif' ? 'bankSearch' : 'bankUsb');
  }

  function saveEditor(action, payload) {
    if (payload.id && payload.id === state.bank?.activeId && action !== 'bankRename') {
      flushSync(closePreview);
      commandRoute = null;
    }
    send(action, payload);
    setEditor(null);
  }

  function startDirection(event, key, x, y) {
    if (!state.canMove || (pointer.current !== null && pointer.current !== event.pointerId)) return;
    if (event.type === 'pointerdown') {
      if (event.button !== 0) return;
      event.currentTarget.setPointerCapture(event.pointerId);
      pointer.current = event.pointerId;
    } else {
      if (![' ', 'Enter'].includes(event.key)) return;
      event.preventDefault();
      if (event.repeat) return;
    }
    held.current.add(key);
    setStick({ x, y });
    send('key', { value: key, down: 1 });
  }

  const cameraName = state.cameras[state.selected];
  const bank = state.bank || emptyBank;
  const activeCamera = bank.profiles.find((camera) => camera.id === bank.activeId);
  const network = state.network;
  const networkMode = network?.active === true;
  const springZoom = networkMode && network.zoomMode === 'spring';
  const absoluteZoom = networkMode && network.zoomMode === 'absolute';
  const previewAvailable = !networkMode && state.selectedConnected && Boolean(cameraName);
  const previewOpen = previewAvailable && preview;
  const connectionState = state.connectionState ?? (network?.connecting ? 'connecting' :
    network?.connected || state.connection?.startsWith('●') ? 'connected' : 'idle');
  const activeIssue = previewError || activeCamera?.error;
  const shownState = activeIssue ? 'error' : activeCamera?.state || 'idle';
  const canPreset = Boolean(state.canPreset) && connectionState === 'connected' && !activeCamera?.isVirtual;
  const connectedCount = bank.profiles.filter((camera) =>
    (camera.id === bank.activeId ? shownState : camera.state) === 'connected').length;
  const connectionLabel = shownState === 'error' ? 'Ошибка камеры' : shownState === 'connecting' ? 'Подключение…' :
    connectedCount ? `Подключено: ${connectedCount} из ${bank.profiles.length}` : 'Не подключено';
  const statusText = activeIssue || bank.message || (shownState === 'connecting' ? cameraStates.connecting :
    activeCamera ? (networkMode ? network?.status : state.status) : 'Выберите камеру');
  const centerNote = !activeCamera ? 'Выберите камеру слева' :
    activeCamera.isVirtual && connectionState === 'connected' ? 'PTZ недоступен для виртуальной камеры' :
    !state.canMove ? (connectionState === 'connected'
    ? (state.canZoom ? 'Поворот и наклон не поддерживаются' : 'PTZ не поддерживается')
    : 'Подключите камеру') : networkMode ? 'Возврат в центр доступен по USB' : '';
  function updateSpeed(speed) {
    setState((current) => ({ ...current, speed }));
    send('speed', { value: speed });
  }

  function updateZoom(zoom) {
    setState((current) => ({ ...current, zoom }));
    send('zoom', { value: zoom });
  }

  function setSpring(value) {
    cancelAnimationFrame(springAnimation.current);
    springPosition.current = value;
    setSpringOffset(value);
    send('networkZoomHold', { value: value / 100 });
  }

  function releaseSpring() {
    if (springInput.current === null) return;
    springInput.current = null;
    send('networkZoomHold', { value: 0 });
    const from = springPosition.current;
    springPosition.current = 0;
    cancelAnimationFrame(springAnimation.current);
    if (!from || matchMedia('(prefers-reduced-motion: reduce)').matches) {
      setSpringOffset(0);
      return;
    }
    const start = performance.now();
    function settle(now) {
      const remaining = Math.max(0, 1 - (now - start) / 140);
      setSpringOffset(Math.round(from * remaining * remaining));
      if (remaining > 0) springAnimation.current = requestAnimationFrame(settle);
    }
    springAnimation.current = requestAnimationFrame(settle);
  }

  useEffect(() => {
    if (!springZoom || !state.canZoom) releaseSpring();
  }, [springZoom, state.canZoom]);

  const springEvents = {
    onPointerDown: (event) => {
      if (event.button !== 0) return;
      springInput.current = event.pointerId;
      event.currentTarget.setPointerCapture(event.pointerId);
    },
    onPointerUp: releaseSpring,
    onPointerCancel: releaseSpring,
    onLostPointerCapture: releaseSpring,
    onBlur: releaseSpring,
    onKeyDown: (event) => {
      if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
      event.preventDefault();
      if (event.repeat || springInput.current !== null) return;
      springInput.current = event.key;
      setSpring(event.key === 'ArrowRight' ? 55 : -55);
    },
    onKeyUp: (event) => {
      if (springInput.current === event.key) releaseSpring();
    },
  };

  function stepZoom(direction) {
    if (networkMode) send('networkZoom', { value: direction });
    else send('key', { value: direction > 0 ? 187 : 189, down: 1 });
  }

  function changePreview(enabled) {
    if (!enabled) {
      closePreview();
      return;
    }
    stop();
    setPreview(true);
  }

  return (
    <Theme theme={themes[defaultThemeSlug]} mode={scheme}>
      <main className="console">
        <header className="console-header">
          <h1><img src="./app-icon.png" alt="" />AGL PTZ Controller</h1>
          <Button className="rooms-open-button" label="Аудитории" title="Аудитории" size="sm" onClick={() => setRoomsOpen(true)}>
            <span className="btn-content"><Icon name="rooms" /><span className="remote-open-label">Аудитории{state.hub?.rooms?.length ? ` · ${state.hub.rooms.filter(r => r.online).length}/${state.hub.rooms.length}` : ''}</span></span>
          </Button>
          <Button className="audio-open-button" label="Звук" title="Звук" size="sm" onClick={() => setAudioOpen(true)}>
            <span className="btn-content"><Icon name="speaker" /><span className="remote-open-label">Звук</span></span>
          </Button>
          <Button className="remote-open-button" label="Удалённое управление" title="Удалённое управление"
            size="sm" onClick={() => { setRemoteOpen(true); send('remoteOpen'); }}>
            <span className="btn-content"><Icon name="remote" /><span className="remote-open-label">Удалённое управление</span></span>
          </Button>
          <SchemeToggle scheme={scheme} toggle={toggleScheme} className="header-scheme" />
          <p className="connection-badge" data-state={shownState} role="status" title={activeIssue || connectionLabel}>
            <i aria-hidden="true" /><b key={shownState}>{connectionLabel}</b>
          </p>
        <Button label="Проверить обновления" title="Проверить обновления" size="sm" onClick={() => send("checkUpdates")}>↻</Button>
        </header>

        <aside className="connection-rail" aria-label="Подключение камеры">
          <CameraBank bank={bank} send={send} switchCamera={switchCamera} onEdit={openEditor} error={previewError} />
          <footer className="connection-status">
            <h2>Статус</h2>
            <p className="bank-status-detail" data-state={shownState} role="status">{statusText}</p>
            {!networkMode && state.report && <details>
              <summary>Диагностика DirectShow</summary>
              <pre>{state.report}</pre>
            </details>}
            <dl className="shortcuts" aria-label="Горячие клавиши">
              <dt><kbd>1–9</kbd></dt><dd>Выбор камеры</dd>
              <dt title="Поворот"><kbd>←</kbd><kbd>↑</kbd><kbd>↓</kbd><kbd>→</kbd></dt><dd>Поворот</dd>
              <dt title={networkMode ? 'Центр по USB' : 'Центр'}><kbd>Пробел</kbd></dt><dd>{networkMode ? 'Центр по USB' : 'Центр'}</dd>
              <dt title="Зум"><kbd>+</kbd><kbd>−</kbd></dt><dd>Зум</dd>
            </dl>
          </footer>
        </aside>

        <section className="movement" aria-label="Поворот и наклон">
          <header className="active-camera" key={activeCamera?.id} title={activeIssue || cameraStates[shownState]}>
            {activeCamera && <i className="active-camera-dot" data-state={shownState}
              role="img" aria-label={activeIssue || cameraStates[shownState]} />}
            <strong>{activeCamera?.name || 'Камера не выбрана'}</strong>
            {activeCamera && <><i className="camera-color" data-color={activeCamera.color} aria-hidden="true" />
              <small>{activeCamera.kind === 'usb' ? 'USB' : 'ONVIF'}</small></>}
          </header>
          {activeCamera && shownState === 'error' && <p className="active-issue" role="alert">
            <span>{activeIssue}</span>
            <button type="button" onClick={() => switchCamera(activeCamera.id, previewError ? 'previewRetry' : 'bankActivate')}>
              <span className="btn-content"><Icon name="retry" size={16} />Переподключить</span></button>
          </p>}
          <dl className="axes">
            <dt>Pan</dt><dd>{state.pan ?? '—'}</dd>
            <dt>Tilt</dt><dd>{state.tilt ?? '—'}</dd>
          </dl>
          <section className="dial-board" aria-label="Управление направлением">
            {directions.map(([direction, label, arrow, key, x, y]) => (
              <Button key={direction} className={`direction ${direction}`} label={label}
                data-held={state.canMove && (stick.x * x + stick.y * y > .15) ? 'true' : undefined}
                disabled={!state.canMove}
                onPointerDown={(event) => startDirection(event, key, x, y)}
                onPointerUp={releasePointer} onPointerCancel={releasePointer} onLostPointerCapture={releasePointer}
                onKeyDown={(event) => startDirection(event, key, x, y)}
                onKeyUp={(event) => { if (held.current.has(key) && [' ', 'Enter'].includes(event.key)) stop(); }}
                onBlur={() => { if (held.current.has(key)) stop(); }}>
                <Icon name="chevron" size={18} style={{ rotate: ({ up: '0deg', right: '90deg', down: '180deg', left: '270deg' })[direction] }} />
              </Button>
            ))}
            <button type="button" className="joystick" aria-label="Джойстик поворота и наклона, управление стрелками"
              data-active={pointer.current !== null || held.current.size > 0}
              style={{ '--thumb-x': `${stick.x * 130.77}%`, '--thumb-y': `${stick.y * 130.77}%`,
                '--stick-angle': `${Math.atan2(stick.x, -stick.y)}rad`, '--stick-len': `${Math.min(1, Math.hypot(stick.x, stick.y)) * 34}%` }}
              disabled={!state.canMove} onPointerDown={(event) => {
                if (event.button !== 0 || pointer.current !== null) return;
                event.currentTarget.setPointerCapture(event.pointerId);
                pointer.current = event.pointerId;
                moveStick(event);
              }} onPointerMove={moveStick} onPointerUp={releasePointer} onPointerCancel={releasePointer}
              onLostPointerCapture={releasePointer}>
              <i className="stick-boot" aria-hidden="true" />
              {Array.from({ length: 24 }, (_, index) =>
                <i key={index} className="dial-tick" data-major={index % 3 === 0 ? 'true' : undefined}
                  aria-hidden="true" style={{ '--tick-angle': `${index * 15}deg` }} />)}
            </button>
          </section>
          <footer className="movement-actions">
            <Button label="Центр" disabled={!centerAvailable} onClick={center} />
          </footer>
          <p className="hint center-note" data-empty={!centerNote} title={centerNote || undefined}>
            {centerNote || '\u00a0'}
          </p>
        </section>

        <aside className="instrument-rail" aria-label="Видео и настройки камеры">
          <section className="preview-module" aria-label="Предпросмотр">
            <header>
              <h2>Предпросмотр</h2>
              <label className="preview-toggle" data-disabled={!previewAvailable}>
                <span aria-hidden="true">{previewOpen ? 'Вкл.' : 'Выкл.'}</span>
                <input className="preview-switch" type="checkbox" role="switch" aria-label="Видео с камеры"
                  checked={previewOpen} disabled={!previewAvailable}
                  onChange={(event) => changePreview(event.target.checked)} />
              </label>
            </header>
            <section className="preview-frame" aria-label="Кадр камеры">
              {previewOpen && demo ? <section className="demo-preview" aria-label="Подставной видеокадр">
                <section className="preview-hud" aria-label="Показания демокамеры">
                  <strong>DEMO</strong>
                  <p>Pan {state.pan}　Tilt {state.tilt}　Zoom {state.zoomLabel}</p>
                </section>
              </section> : previewOpen ? <CameraPreview key={`${bank.activeId}:${previewAttempt}`} onError={setPreviewError} name={cameraName}
                ambiguous={state.cameras.filter((name) => name === cameraName).length > 1}
                pan={state.pan} tilt={state.tilt} zoom={state.zoomLabel} /> :
                <section className="preview-message" aria-label="Состояние предпросмотра" role="status">
                  <strong>{activeCamera?.kind === 'onvif' ? 'Предпросмотр недоступен для сетевых камер' : 'Превью выключено'}</strong>
                  {activeCamera?.kind !== 'onvif' && <p>{activeCamera?.isVirtual ? virtualCameraWarning : previewAvailable
                    ? 'Камера свободна для других программ' : 'Выберите USB-камеру'}</p>}
                </section>}
            </section>
            {previewOpen && activeCamera?.isVirtual && <p className="hint">{virtualCameraWarning}</p>}
          </section>

          <RangeControl label="Зум" value={springZoom ? springOffset : networkMode ? network.zoomPercent ?? 0 : state.zoom}
            min={springZoom ? -100 : networkMode ? 0 : state.zoomMin} max={networkMode ? 100 : state.zoomMax}
            display={springZoom ? undefined : absoluteZoom && network.zoomPercent != null ? `${network.zoomPercent}%` :
              networkMode ? '—' : state.zoomLabel}
            disabled={!state.canZoom}
            onChange={springZoom ? (value) => { if (springInput.current !== null) setSpring(value); } :
              networkMode ? (value) => {
                setState((current) => ({ ...current, network: { ...current.network, zoomPercent: value } }));
                send('networkZoomSeek', { value });
              } : updateZoom}
            onCommit={networkMode ? undefined : () => send('flushZoom')} onStep={stepZoom}
            sliderEvents={springZoom ? springEvents : undefined}
            spring={springZoom}
            minusLabel="Отдалить" plusLabel="Приблизить"
            reason={springZoom ? 'Тяните и удерживайте. Отпустите, чтобы остановить' :
              !state.canZoom ? (connectionState === 'connected' ? 'Зум не поддерживается' : 'Подключите камеру') : undefined}>
          {!networkMode && state.canZoom && <section className="zoom-limits" aria-label="Границы зума">
            <Button label="Минимум" size="sm" variant="secondary" onClick={() => send('wide')} />
            <Button label="Максимум" size="sm" variant="secondary" onClick={() => send('tight')} />
          </section>}
          </RangeControl>
          <section className="instrument-bank" aria-label="Скорость и пресеты">
          <RangeControl label="Скорость" accessibleName="Скорость движения"
            disabled={connectionState !== 'connected' || activeCamera?.isVirtual} value={state.speed} min={1} max={10} display={`${state.speed}/10`}
            onChange={updateSpeed} onStep={(delta) => updateSpeed(Math.max(1, Math.min(10, state.speed + delta)))}
            minusLabel="Уменьшить скорость" plusLabel="Увеличить скорость" />

          <section className="presets" aria-labelledby="presets-title">
            <h2 id="presets-title">Пресеты</h2>
            <section className="preset-modes" role="group" aria-label="Режим пресетов">
              {[['go', 'Вызвать'], ['save', 'Запомнить'], ['clear', 'Удалить']].map(([mode, label]) =>
                <button key={mode} type="button" aria-pressed={presetMode === mode} disabled={!canPreset}
                  onClick={() => setPresetMode(mode)}>{label}</button>)}
            </section>
            <section className="preset-grid" aria-label="Восемь позиций">
              {Array.from({ length: 8 }, (_, index) => {
                const saved = Boolean(state.presets?.[index]);
                return <Button key={index} label={`Пресет ${index + 1}${saved ? ', сохранён' : ''}`} aria-describedby="presets-reason"
                  disabled={!canPreset || (presetMode === 'go' && !saved) || (presetMode === 'clear' && !saved)}
                  onClick={() => {
                    send({ go: 'presetGo', save: 'presetSave', clear: 'presetClear' }[presetMode], { value: index + 1 });
                    if (presetMode !== 'go') setPresetMode('go');
                  }}
                  size="sm">{index + 1}{saved && <i className="preset-dot" aria-hidden="true" />}</Button>;
              })}
            </section>
            <p id="presets-reason" className="hint">{!canPreset ?
              (connectionState !== 'connected' ? 'Подключите камеру' : activeCamera?.isVirtual ? 'Недоступно для виртуальной камеры' : 'Пресеты недоступны для этой камеры') :
              presetMode === 'save' ? 'Выберите номер: текущая позиция будет запомнена' :
              presetMode === 'clear' ? 'Выберите сохранённый номер для удаления' :
              state.network?.active ? 'Позиции хранятся в камере' : 'Позиции хранятся в профиле камеры'}</p>
          </section>
          </section>
        </aside>
        {roomsOpen && <RoomsDialog hub={state.hub} send={send} close={() => setRoomsOpen(false)} />}
        {audioOpen && <AudioDialog audio={state.audio ?? (demo ? {speaker:{Present:true,Volume:62,Muted:false},mic:{Present:true,Volume:48,Muted:false}} : null)} send={send} close={() => setAudioOpen(false)} />}
        {remoteOpen && <RemoteDialog state={state.remote} send={send}
          close={() => { send('remoteClose'); setRemoteOpen(false); }} />}
        {editor && <CameraEditor profile={editor} usb={bank.usb} network={network} initialColor={nextCameraColor(bank.profiles)}
          close={() => setEditor(null)} save={saveEditor} send={send} />}
      </main>
    </Theme>
  );
}

createRoot(document.getElementById('root')).render(<App />);
