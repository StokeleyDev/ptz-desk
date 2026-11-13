import { useLayoutEffect, useRef, useState } from 'react';
import { Button } from '@astryxdesign/core/Button';

const normalize = (name) => name.trim().replace(/\s*\([\da-f]{4}:[\da-f]{4}\)$/i, '').toLowerCase();
const stopStream = (stream) => stream?.getTracks().forEach((track) => track.stop());
const openingMessage = 'Открываем камеру…';

export function CameraPreview({ name, ambiguous, pan, tilt, zoom, onError }) {
  const video = useRef(null);
  const [message, setMessage] = useState(openingMessage);
  const [attempt, setAttempt] = useState(0);

  useLayoutEffect(() => {
    let closed = false;
    let stream;
    let probe;
    const element = video.current;
    const media = navigator.mediaDevices;
    setMessage(openingMessage);
    onError?.('');

    async function start() {
      if (ambiguous) throw new Error('У нескольких камер одинаковое имя. Отключите лишнюю камеру и попробуйте снова.');
      if (!media?.getUserMedia) throw new Error('Предпросмотр камеры недоступен в этой среде.');
      let inputs = (await media.enumerateDevices()).filter((item) => item.kind === 'videoinput');
      if (closed) return;
      if (!inputs.some((item) => item.label)) {
        // Chromium can hide device names until the first permitted video capture.
        probe = await media.getUserMedia({ video: true, audio: false });
        try {
          if (!closed) inputs = (await media.enumerateDevices()).filter((item) => item.kind === 'videoinput');
          const matches = inputs.filter((item) => item.deviceId && normalize(item.label) === normalize(name));
          if (!closed && matches.length === 1 &&
              probe.getVideoTracks()[0]?.getSettings().deviceId === matches[0].deviceId) stream = probe;
        } finally {
          if (stream !== probe) stopStream(probe);
          probe = undefined;
        }
      }
      if (closed) return;
      const matches = inputs.filter((item) => item.deviceId && normalize(item.label) === normalize(name));
      if (matches.length !== 1) throw new Error(`Не удалось однозначно найти видеопоток «${name}».`);
      stream ??= await media.getUserMedia({
        video: { deviceId: { exact: matches[0].deviceId }, width: { ideal: 1280 }, height: { ideal: 720 } },
        audio: false,
      });
      if (closed) {
        stopStream(stream);
        return;
      }
      const track = stream.getVideoTracks()[0];
      track.addEventListener('ended', () => {
        if (!closed) {
          stopStream(stream);
          element.srcObject = null;
          setMessage('Видеопоток прерван. Проверьте подключение камеры.');
          onError?.('Видеопоток прерван. Проверьте подключение камеры.');
        }
      }, { once: true });
      element.srcObject = stream;
      await element.play();
      if (!closed) setMessage('');
    }

    function release() {
      closed = true;
      stopStream(probe);
      stopStream(stream);
      element.srcObject = null;
    }

    window.addEventListener('pagehide', release, { once: true });
    start().catch((error) => {
      stopStream(stream);
      if (closed) return;
      element.srcObject = null;
      const reason = {
        NotAllowedError: 'Windows не разрешила доступ к камере.',
        NotReadableError: 'Камера используется другой программой',
        NotFoundError: 'Windows не обнаружила видеокамеру.',
        OverconstrainedError: 'Выбранная камера больше недоступна.',
      }[error.name] ?? error.message;
      setMessage(reason);
      onError?.(reason);
    });

    return () => {
      window.removeEventListener('pagehide', release);
      release();
    };
  }, [name, ambiguous, attempt, onError]);

  const opening = message === openingMessage;

  return (
    <>
      <video ref={video} aria-label={`Видео с камеры ${name}`} muted playsInline autoPlay />
      {message ? <section className="preview-message" data-state={opening ? 'opening' : 'error'}
        aria-label="Состояние предпросмотра">
        <strong role="status">{message}</strong>
        {!opening &&
          <Button label="Повторить" size="sm" onClick={() => setAttempt((value) => value + 1)} />}
      </section> : <section className="preview-hud" aria-label="Показания камеры">
        <strong>LIVE</strong>
        <p>Pan {pan ?? '—'}　Tilt {tilt ?? '—'}　Zoom {zoom}</p>
      </section>}
    </>
  );
}
