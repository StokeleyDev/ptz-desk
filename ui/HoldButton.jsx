import React, { useEffect, useRef, useState } from 'react';

// Destructive action that fires only after the pointer (or Space/Enter) is held for `ms`.
// Releasing early cancels; the fill shows progress. Keeps accidental taps harmless on a phone.
export function HoldButton({ onConfirm, ms = 900, label, hint = 'Удерживайте', className = '', children, ...rest }) {
  const [holding, setHolding] = useState(false);
  const timer = useRef(0);
  const cancel = () => { clearTimeout(timer.current); setHolding(false); };
  const start = (event) => {
    if (event.type === 'keydown') {
      if (![' ', 'Enter'].includes(event.key)) return;
      event.preventDefault();
      if (event.repeat) return;
    }
    if (event.type === 'pointerdown' && event.button !== 0) return;
    clearTimeout(timer.current);
    setHolding(true);
    timer.current = setTimeout(() => { setHolding(false); onConfirm(); }, ms);
  };
  useEffect(() => () => clearTimeout(timer.current), []);
  return <button type="button" className={'hold-button ' + className} data-holding={holding}
    style={{ '--hold-ms': ms + 'ms' }} aria-label={label + '. ' + hint} title={hint + ': ' + label.toLowerCase()} {...rest}
    onPointerDown={start} onPointerUp={cancel} onPointerLeave={cancel} onPointerCancel={cancel}
    onKeyDown={start} onKeyUp={cancel} onBlur={cancel}>
    <span className="hold-fill" aria-hidden="true" />
    {children}
  </button>;
}
