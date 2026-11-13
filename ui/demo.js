export function createDemoBridge(nativeBridge) {
  const events = new EventTarget();
  const name = 'PTZ — деморежим';
  const base = {
    cameras: [name], selected: 0, selectedConnected: true, connectionState: 'connected', connectedName: name,
    status: 'Деморежим. Камера не используется.', report: '', canMove: true, canZoom: true,
    pan: 24, tilt: -8, speed: 5, zoom: 42, zoomMin: 0, zoomMax: 100, preset: null,
    canPreset: true, presets: Array(8).fill(false),
  };
  let state = { ...base };
  const demoQr = './app-icon.png';
  const remote = { enabled:true, adapterId:'demo',port:8787,address:'http://192.0.2.20:8787',
    status:'Ожидает подключений',sessions:0,owner:{kind:'none'},
    adapters:[{id:'demo',name:'Wi-Fi (demo)',address:'192.0.2.20',eligible:true}],
    devices:[{id:'one',name:'Телефон оператора',lastSeen:new Date().toISOString()},
      {id:'two',name:'Планшет',lastSeen:new Date().toISOString()}] };
  const profiles = [
    { id: 'demo-usb', name: 'Общий план', kind: 'usb', target: 'demo-usb', color: 'teal', speed: 5 },
    { id: 'demo-absolute', name: 'Трибуна', kind: 'onvif', target: '192.0.2.21', color: 'amber', speed: 4, zoomMode: 'absolute' },
    { id: 'demo-spring', name: 'Зал', kind: 'onvif', target: '192.0.2.22', color: 'blue', speed: 6, zoomMode: 'spring' },
  ];
  const usb = [{ name, target: 'demo-usb' },
    { name: 'OBS Virtual Camera', target: 'demo-virtual-usb', isVirtual: true }];
  const discovered = profiles.filter((item) => item.kind === 'onvif')
    .map((item) => ({ Name: item.name, Address: item.target }));
  const readings = new Map();
  let activeId = null;
  let generation = 0;
  let request = 0;
  let searching = false;
  resetConnection();
  state.network = { active: false, cameras: discovered };
  let direction = [0, 0];
  let zoomHold = 0;
  const hub = { busy: false, error: null, rooms: [
    { id: 'r301', name: 'Аудитория 301', address: 'http://172.21.30.11:8787', online: true, status: 'В сети',
      audio: { speaker: { Present: true, Volume: 55, Muted: false }, mic: { Present: true, Volume: 40, Muted: true } } },
    { id: 'r205', name: 'Аудитория 205', address: 'http://172.21.20.14:8787', online: false, status: 'Нет связи', audio: null },
  ] };
  const demoPresets = {};
  let lastInput = 0;
  const keys = new Set();
  const limit = (value, min, max) => Math.max(min, Math.min(max, value));
  const publish = () => events.dispatchEvent(new MessageEvent('message', {
    data: { ...state, remote: {...remote}, hub: { ...hub }, network: { ...state.network, searching, zoomPercent: state.zoom },
      zoomLabel: state.canZoom ? `${state.zoom}%` : '—',
      bank: { activeId, generation, request, usb, profiles: profiles.map((item) => ({
        ...item, state: item.id === activeId ? state.connectionState : 'idle',
      })) } },
  }));
  const zoomBy = (amount) => { state.zoom = limit(state.zoom + amount, 0, 100); };
  const stop = () => { direction = [0, 0]; keys.clear(); zoomHold = 0; };

  setInterval(() => {
    if (performance.now() - lastInput > 350) stop();
    const [x, y] = keys.size
      ? [Number(keys.has(39)) - Number(keys.has(37)), Number(keys.has(38)) - Number(keys.has(40))]
      : direction;
    if (!x && !y && !zoomHold) return;
    state.pan = limit(state.pan + Math.round(x * state.speed), -170, 170);
    state.tilt = limit(state.tilt + Math.round(y * state.speed), -30, 90);
    if (zoomHold) zoomBy(Math.sign(zoomHold) * Math.max(1, Math.round(Math.abs(zoomHold) * state.speed)));
    publish();
  }, 90);

  function release() {
    if (activeId) readings.set(activeId, { pan: state.pan, tilt: state.tilt, zoom: state.zoom, preset: state.preset });
    activeId = null;
    generation++;
    stop();
    resetConnection();
    state.network = { active: false, cameras: discovered };
  }

  events.postMessage = (message) => {
    const { type, value, down, x, y, id, cameraId } = message;
    if(type.startsWith('roomAudio')){
      const room = hub.rooms.find(r => r.id === message.roomId), end = room?.audio?.[message.target];
      if (end) { if (type === 'roomAudioVolume') end.Volume = message.value; else end.Muted = message.value; }
      publish(); return;
    }
    if(type === 'hubRemove'){ hub.rooms = hub.rooms.filter(r => r.id !== id); publish(); return; }
    if(type === 'hubAdd'){
      hub.error = /^http:\/\/[\d.]+:\d+\/pair#code=/.test(message.link ?? '') ? null : 'Вставьте ссылку привязки с экрана панели: http://IP:порт/pair#code=…';
      if (!hub.error) hub.rooms = [...hub.rooms, { id: 'r' + hub.rooms.length + 1, name: message.name || 'Новая аудитория',
        address: 'http://172.21.0.1:8787', online: true, status: 'В сети', audio: { speaker: { Present: true, Volume: 50, Muted: false }, mic: { Present: true, Volume: 50, Muted: false } } }];
      publish(); return;
    }
    if(type.startsWith('remote')){
      if(type==='remoteOpen'||type==='remoteNewCode')remote.offer=remote.enabled?{qr:demoQr,secondsLeft:120}:null;
      if(type==='remoteClose')remote.offer=null;
      if(type==='remoteRevoke')remote.devices=remote.devices.filter(d=>d.id!==id);
      if(type==='remoteSetEnabled')remote.enabled=Boolean(value);
      if(type==='remoteSetPort')remote.port=value;
      remote.address=remote.enabled?`http://192.0.2.20:${remote.port}`:null;
      remote.status=remote.enabled?'Ожидает подключений':'Выключено';
      publish();return;
    }
    const profile = profiles.find((item) => item.id === id);
    const index = profiles.indexOf(profile);
    if (type.startsWith('bank')) {
      switch (type) {
        case 'bankSearch':
          if (!searching) {
            searching = true;
            setTimeout(() => { searching = false; publish(); }, 300);
          }
          break;
        case 'bankActivate':
          request = message.request;
          if (!profile || activeId === id) break;
          release();
          activeId = id;
          state = { ...base, ...readings.get(id), speed: profile.speed,
            network: { active: profile.kind === 'onvif', connected: profile.kind === 'onvif',
              zoomMode: profile.zoomMode || 'spring', cameras: discovered, status: base.status },
            selectedConnected: profile.kind === 'usb', canMove: !profile.isVirtual, canZoom: !profile.isVirtual,
            pan: profile.isVirtual ? null : readings.get(id)?.pan ?? base.pan,
            tilt: profile.isVirtual ? null : readings.get(id)?.tilt ?? base.tilt };
          break;
        case 'bankDisconnect':
          request = message.request;
          if (id === activeId) release();
          break;
        case 'bankDelete':
          if (!profile) break;
          if (id === activeId) release();
          profiles.splice(index, 1);
          readings.delete(id);
          break;
        case 'bankRename':
          if (profile) profile.name = message.name;
          break;
        case 'bankSave': {
          const { name, kind, color, target, username } = message;
          if (profile && id === activeId) release();
          const updated = { id: profile?.id || crypto.randomUUID(), name, kind, color, target,
            username, speed: profile?.speed ?? 5, zoomMode: profile?.zoomMode,
            isVirtual: kind === 'usb' && Boolean(usb.find((item) => item.target === target)?.isVirtual) };
          if (profile) profiles[index] = updated;
          else profiles.push(updated);
          break;
        }
        case 'bankReorder': {
          const destination = profiles.findIndex((item) => item.id === message.before);
          if (profile && destination >= 0 && destination !== index) {
            profiles.splice(index, 1);
            profiles.splice(destination, 0, profile);
          }
          break;
        }
      }
      publish();
      return;
    }
    if (type !== 'ready' && (!activeId || activeId !== cameraId || generation !== message.generation ||
        profiles.find((item) => item.id === activeId)?.isVirtual)) return;
    switch (type) {
      case 'ready': nativeBridge?.postMessage({ type: 'ready' }); break;
      case 'heartbeat': lastInput = performance.now(); return;
      case 'stick': direction = [x, y]; lastInput = performance.now(); return;
      case 'stop': stop(); return;
      case 'key':
        if (value >= 37 && value <= 40) {
          if (down) keys.add(value);
          else keys.delete(value);
          lastInput = performance.now();
        } else if (down) zoomBy(value === 187 ? 2 : -2);
        break;
      case 'center': state.pan = state.tilt = 0; break;
      case 'speed': state.speed = value; profiles.find((item) => item.id === activeId).speed = value; break;
      case 'zoom': state.zoom = value; break;
      case 'networkZoomSeek': state.zoom = value; break;
      case 'networkZoom': zoomBy(value * 3); break;
      case 'networkZoomHold': zoomHold = value; lastInput = performance.now(); return;
      case 'wide': state.zoom = 0; break;
      case 'tight': state.zoom = 100; break;
      case 'presetSave':
        demoPresets[value] = { pan: state.pan, tilt: state.tilt, zoom: state.zoom };
        state.presets = state.presets.map((saved, index) => index === value - 1 ? true : saved);
        break;
      case 'presetClear':
        delete demoPresets[value];
        state.presets = state.presets.map((saved, index) => index === value - 1 ? false : saved);
        break;
      case 'presetGo':
        if (demoPresets[value]) { Object.assign(state, demoPresets[value]); state.preset = value; }
        break;
    }
    publish();
  };
  function resetConnection() {
    state.connectionState = 'idle';
    state.connectedName = null;
    state.selectedConnected = false;
    state.canMove = state.canZoom = false;
    state.pan = state.tilt = null;
    state.status = 'Демокамера отключена.';
    if (state.network) state.network = { ...state.network, connected: false, zoomMode: 'none',
      status: state.status, name: null, address: null, username: null };
  }
  return events;
}
