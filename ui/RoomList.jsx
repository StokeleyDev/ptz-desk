import React from 'react';
import {PhoneAudio} from './PhoneAudio.jsx';

// Audio of every room known to the hub. `send` takes a message object.
export function RoomList({rooms, send, disabled = false, className = 'phone-room'}) {
  return <>{rooms.map(room => <section key={room.id} className={className} data-online={room.online}>
    <h3>{room.name}<small>{room.online ? 'в сети' : (room.status || 'нет связи')}</small></h3>
    <PhoneAudio audio={room.audio} roomId={room.id} send={send} disabled={disabled || !room.online}/>
  </section>)}</>;
}
